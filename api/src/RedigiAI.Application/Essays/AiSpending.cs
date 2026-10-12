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
    /// <summary>Antes de cada chamada: se ela não cabe no limite rígido, a redação falha com o crédito devolvido.</summary>
    /// <exception cref="AppError"><c>AI_BUDGET_EXCEEDED</c>, não retentável.</exception>
    public async Task EnsureRoomForAnotherCallAsync(Guid essayId, CancellationToken ct)
    {
        if (!budget.AllowsAnotherCall(await usages.ListCostsAsync(essayId, ct)))
        {
            audit.Record("system", "ai.hard_cap_reached", "essay", essayId);
            await unitOfWork.SaveChangesAsync(ct);
            throw Errors.AiFailed("AI_BUDGET_EXCEEDED");
        }
    }

    /// <summary>O consumo é gravado logo depois da chamada, mesmo que o resto do job falhe.</summary>
    public async Task RecordAsync(Guid essayId, string stage, string model, AiTokens tokens, CancellationToken ct)
    {
        var cost = budget.CostOf(model, tokens);
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
