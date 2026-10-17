using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Common;
using RedigiAI.Domain.Essays;

namespace RedigiAI.Application.Essays;

/// <summary>
/// Gasto de IA de uma redação, somando transcrição, correção e novas tentativas: grava o consumo de cada
/// chamada e aplica o teto de referência (alerta) e o limite rígido (a chamada não é feita).
/// </summary>
internal sealed class AiSpending(
    IAiUsageRepository usages, IAuditLog audit, IUnitOfWork unitOfWork, AiBudget budget, TimeProvider time)
{
    /// <summary>Uma resposta com menos que isso não serve para nada: melhor não fazer a chamada.</summary>
    public const int MinOutputTokens = 1_024;

    /// <summary>Folga para a definição da ferramenta e a estrutura da mensagem, que também são cobradas como entrada.</summary>
    private const int RequestOverheadTokens = 2_000;

    /// <summary>
    /// Entrada estimada por cima: um token a cada 3 caracteres (em português o comum é perto de 4) mais a folga
    /// da ferramenta. Serve só para o limite de gasto; a cobrança é pelo que a API informa.
    /// </summary>
    public static int EstimateInputTokens(params string?[] texts) =>
        texts.Sum(text => (text?.Length ?? 0) / 3) + RequestOverheadTokens;

    /// <summary>
    /// Chama a IA e, se a chamada falhar depois de a IA começar a responder, grava o consumo até ali antes de
    /// seguir com o erro: o que foi gerado é cobrado e entra no limite de gasto.
    /// </summary>
    public async Task<T> CallAsync<T>(Guid essayId, string stage, string model, Func<Task<T>> call, CancellationToken ct)
    {
        try
        {
            return await call();
        }
        catch (AiCallFailedException failed)
        {
            await RecordAsync(essayId, stage, model, failed.Tokens, ct);
            throw failed.Error;
        }
    }

    /// <summary>
    /// Antes de cada chamada: o <c>max_tokens</c> que ainda cabe no limite rígido, com a chamada contada pelo
    /// pior caso (entrada estimada e saída inteira). Se não couber nem <see cref="MinOutputTokens"/>, a redação
    /// falha com o crédito devolvido.
    /// </summary>
    /// <param name="estimatedInputTokens">Entrada da chamada, estimada por cima.</param>
    /// <param name="maxTokens">O <c>max_tokens</c> pedido.</param>
    /// <returns>O <c>max_tokens</c> pedido ou um menor, que ainda cabe.</returns>
    /// <exception cref="AppError"><c>AI_BUDGET_EXCEEDED</c>, não retentável.</exception>
    public async Task<int> MaxTokensWithinCapAsync(
        Guid essayId, string model, int estimatedInputTokens, int maxTokens, CancellationToken ct)
    {
        var costs = await usages.ListCostsAsync(essayId, ct);
        var fits = budget.OutputTokensWithinCap(costs, model, estimatedInputTokens);
        if (fits is null)
        {
            // Modelo sem preço: só dá para estimar pela chamada mais cara já feita.
            if (!budget.AllowsAnotherCall(costs))
            {
                await HardCapReachedAsync(essayId, ct);
            }

            return maxTokens;
        }

        if (fits < MinOutputTokens)
        {
            await HardCapReachedAsync(essayId, ct);
        }

        if (fits < maxTokens)
        {
            audit.Record("system", "ai.max_tokens_reduced", "essay", essayId, new { requested = maxTokens, allowed = fits });
            await unitOfWork.SaveChangesAsync(ct);
            return fits.Value;
        }

        return maxTokens;
    }

    private async Task HardCapReachedAsync(Guid essayId, CancellationToken ct)
    {
        audit.Record("system", "ai.hard_cap_reached", "essay", essayId);
        await unitOfWork.SaveChangesAsync(ct);
        throw Errors.AiFailed("AI_BUDGET_EXCEEDED");
    }

    /// <summary>O consumo é gravado logo depois da chamada, mesmo que o resto do job falhe.</summary>
    public async Task RecordAsync(Guid essayId, string stage, string model, AiTokens tokens, CancellationToken ct)
    {
        var cost = budget.CostOf(model, tokens);
        if (cost is null)
        {
            // Sem preço configurado para o modelo, esta chamada fica fora do teto: precisa aparecer.
            audit.Record("system", "ai.price_missing", "essay", essayId, new { model });
        }

        usages.Add(AiUsage.Record(
            essayId, stage, model, tokens.Input, tokens.Output, tokens.CacheRead, tokens.CacheWrite, cost, time.GetUtcNow()));

        var spent = (await usages.ListCostsAsync(essayId, ct)).Sum() + (cost ?? 0);
        if (spent > budget.ReferenceCapMicroUsd)
        {
            audit.Record("system", "ai.cost_above_reference", "essay", essayId, new { spentMicroUsd = spent });
        }

        await unitOfWork.SaveChangesAsync(ct);
    }
}
