using Microsoft.Extensions.Options;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Common;

namespace RedigiAI.Application.Accounts;

public sealed record RegisterUserCommand(
    string ClerkUserId,
    string Email,
    string? Name,
    DateOnly? BirthDate,
    bool TermsAccepted,
    RequestOrigin Origin);

public sealed record RegisterUserResult(ProfileDto Profile, bool Created);

/// <summary>
/// Primeiro acesso: cria o usuário local e concede o crédito grátis uma única vez.
/// Repetido ou simultâneo, devolve o usuário que já existe.
/// </summary>
public sealed class RegisterUserHandler(
    IUserRepository users,
    ICreditRepository credits,
    IAuditLog audit,
    IAccessLog accessLog,
    IUnitOfWork unitOfWork,
    IOptions<LegalOptions> legal,
    TimeProvider time)
{
    public async Task<RegisterUserResult> HandleAsync(RegisterUserCommand command, CancellationToken ct)
    {
        var reader = new ProfileReader(credits, legal, time);

        var existing = await users.FindByClerkIdAsync(command.ClerkUserId, ct);
        if (existing is not null)
        {
            return new RegisterUserResult(await reader.ReadAsync(existing, ct), Created: false);
        }

        if (command.BirthDate is not { } birthDate)
        {
            throw Errors.Validation("Informe a data de nascimento.");
        }

        if (!command.TermsAccepted)
        {
            throw Errors.Validation("Aceite os Termos de uso e a Política de privacidade para continuar.");
        }

        var now = time.GetUtcNow();
        var user = User.Register(
            command.ClerkUserId, command.Email, command.Name, birthDate, legal.Value.TermsVersion, now);
        var bucket = CreditBucket.GrantTrial(user.Id, now);

        users.Add(user);
        credits.AddGrant(bucket, CreditLedgerEntry.ForGrant(bucket, now));
        audit.Record($"user:{user.Id}", "user.registered", "user", user.Id);
        audit.Record("system", "credit.granted", "credit_bucket", bucket.Id, new { source = "trial", delta = bucket.Granted });
        accessLog.Record(user.Id, command.Origin);

        try
        {
            await unitOfWork.SaveChangesAsync(ct);
        }
        catch (UniqueConflictException)
        {
            // Outra requisição da mesma conta chegou primeiro: vale o usuário dela, sem segundo crédito.
            var winner = await users.FindByClerkIdAsync(command.ClerkUserId, ct) ?? throw Errors.Internal();
            return new RegisterUserResult(await reader.ReadAsync(winner, ct), Created: false);
        }

        return new RegisterUserResult(await reader.ReadAsync(user, ct), Created: true);
    }
}
