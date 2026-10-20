using RedigiAI.Domain.Billing;

namespace RedigiAI.Application.Billing;

/// <summary>
/// Assinar de novo com o ciclo pago valendo (decisão de 20/10/2026): a primeira cobrança fica para o fim desse ciclo, como
/// se o cancelamento fosse desfeito. Nem cobra duas vezes no mesmo mês, nem tira as correções que o aluno já pagou; a
/// cobrança, quando vier, renova o saldo do plano para 12 como sempre. Ciclo estornado não conta. A mesma conta vale para
/// o aviso antes de pagar (perfil) e para a assinatura (regra 23).
/// </summary>
internal sealed class PlanResume(ICreditRepository credits, TimeProvider time)
{
    /// <summary>
    /// Um ciclo que termina antes disso não adia a cobrança: ela é feita agora (com as 12 novas na hora). O Mercado Pago só
    /// adia quando o início cai em outro dia; no mesmo dia, ele cobra na criação (medido no sandbox em 20/10/2026).
    /// </summary>
    private static readonly TimeSpan MinimumDeferral = TimeSpan.FromHours(24);

    /// <returns>A data da primeira cobrança de uma assinatura feita agora; nula se a cobrança é agora.</returns>
    public async Task<DateTimeOffset?> FirstChargeAtAsync(Guid userId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var cycleEnd = (await credits.ListOpenPlanBucketsAsync(userId, now, ct))
            .Where(b => b.RevokedAt is null && b.ExpiresAt is not null)
            .Select(b => b.ExpiresAt!.Value)
            .DefaultIfEmpty()
            .Max();
        return cycleEnd > now + MinimumDeferral ? cycleEnd : null;
    }
}
