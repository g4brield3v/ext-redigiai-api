using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Billing;

namespace RedigiAI.Application.Billing;

/// <summary>
/// Os créditos do plano mensal (decisão de 19/10/2026): cada cobrança aprovada dá 12 correções, que não acumulam. A
/// renovação zera o que sobrou do ciclo anterior e concede as 12 novas, na mesma transação, seja qual for o horário em
/// que a cobrança chega.
/// </summary>
internal sealed class PlanCredits(ICreditRepository credits, IAuditLog audit)
{
    /// <summary>
    /// Renova os créditos do plano do aluno com a cobrança <paramref name="payment"/>, válidos até
    /// <paramref name="cycleEnd"/> (a cobrança seguinte). Roda dentro da transação de quem chama, que grava no fim.
    /// </summary>
    public async Task RenewAsync(Payment payment, DateTimeOffset cycleEnd, DateTimeOffset now, CancellationToken ct)
    {
        // Duas cobranças do mesmo aluno aprovadas juntas: a segunda espera e vê o ciclo da primeira para zerá-lo.
        await credits.LockUserAsync(payment.UserId, ct);
        foreach (var previous in await credits.ListOpenPlanBucketsAsync(payment.UserId, now, ct))
        {
            // Encerrado mesmo sem saldo: uma correção reservada no ciclo anterior não volta para ele, volta como avulsa.
            if (await credits.CloseAsync(previous.Id, now, ct) is > 0 and var left)
            {
                credits.AddEntry(CreditLedgerEntry.ForExpiry(previous, left, now));
                audit.Record("system", "credit.expired", "credit_bucket", previous.Id, new { delta = -left, reason = "plan_renewal" });
            }
        }

        var bucket = CreditBucket.GrantPlanCycle(payment, cycleEnd, now);
        credits.AddGrant(bucket, CreditLedgerEntry.ForPaymentGrant(bucket, payment.Id, now));
        audit.Record("system", "credit.granted", "credit_bucket", bucket.Id, new { source = "plan", delta = bucket.Granted, paymentId = payment.Id });
    }
}
