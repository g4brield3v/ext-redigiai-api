using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Billing;

namespace RedigiAI.Application.Billing;

/// <param name="Actor">Quem concedeu, para a trilha de auditoria.</param>
public sealed record GrantCreditsCommand(Guid UserId, CreditSource Source, int Amount, DateTimeOffset? ExpiresAt, string Actor);

public interface ICreditService
{
    /// <summary>Concede correções: o bucket e o lançamento no ledger são gravados juntos.</summary>
    Task GrantAsync(GrantCreditsCommand command, CancellationToken ct);
}

internal sealed class CreditService(ICreditRepository credits, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider time)
    : ICreditService
{
    public async Task GrantAsync(GrantCreditsCommand command, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var bucket = CreditBucket.Grant(command.UserId, command.Source, command.Amount, command.ExpiresAt, now);

        credits.AddGrant(bucket, CreditLedgerEntry.ForGrant(bucket, now));
        audit.Record(
            command.Actor,
            "credit.granted",
            "credit_bucket",
            bucket.Id,
            new { source = command.Source.ToString().ToLowerInvariant(), delta = bucket.Granted });
        await unitOfWork.SaveChangesAsync(ct);
    }
}
