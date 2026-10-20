using RedigiAI.Application.Abstractions;
using RedigiAI.Application.Billing;
using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Common;

namespace RedigiAI.Application.Accounts;

/// <param name="ExtraCredits">Correções avulsas além do teste grátis.</param>
/// <param name="IsAdmin">O administrador de teste: a flag é ligada aqui, só no desenvolvimento.</param>
public sealed record TestAccount(string ClerkUserId, string Email, string Name, int Age, int ExtraCredits, bool IsAdmin = false);

/// <summary>Alunos de teste do ambiente de desenvolvimento. Nunca é chamado fora dele.</summary>
public interface ITestAccountService
{
    /// <summary>Cadastra a conta se ela ainda não existe; os créditos extras entram só nessa primeira vez.</summary>
    Task EnsureAsync(TestAccount account, CancellationToken ct);
}

internal sealed class TestAccountService(
    IAccountService accounts, ICreditService credits, IUserRepository users, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider time)
    : ITestAccountService
{
    private const string Actor = "dev-setup";
    private static readonly RequestOrigin Origin = new("127.0.0.1", Actor, null);

    public async Task EnsureAsync(TestAccount account, CancellationToken ct)
    {
        // Cem dias depois do aniversário: a idade não muda de um dia para o outro durante os testes.
        var birthDate = BrasiliaTime.DateOf(time.GetUtcNow()).AddYears(-account.Age).AddDays(-100);

        var result = await accounts.RegisterAsync(
            new RegisterUserCommand(account.ClerkUserId, account.Email, account.Name, birthDate, TermsAccepted: true, Origin),
            ct);

        if (result.Created && account.ExtraCredits > 0)
        {
            await credits.GrantAsync(
                new GrantCreditsCommand(result.Profile.Id, CreditSource.Single, account.ExtraCredits, ExpiresAt: null, Actor), ct);
        }

        if (account.IsAdmin && await users.FindByClerkIdAsync(account.ClerkUserId, ct) is { } user && user.MakeAdmin())
        {
            audit.Record(Actor, "user.admin_granted", "user", user.Id);
            await unitOfWork.SaveChangesAsync(ct);
        }
    }
}
