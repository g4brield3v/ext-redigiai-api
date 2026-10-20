using Microsoft.Extensions.Options;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Common;

namespace RedigiAI.Application.Accounts;

internal sealed class AccountService(
    IUserRepository users,
    ICreditRepository credits,
    ISubscriptionRepository subscriptions,
    Billing.PlanResume planResume,
    IAuditLog audit,
    IAccessLog accessLog,
    IUnitOfWork unitOfWork,
    IOptions<LegalOptions> legal,
    TimeProvider time) : IAccountService
{
    public async Task<RegisterUserResult> RegisterAsync(RegisterUserCommand command, CancellationToken ct)
    {
        var existing = await users.FindByClerkIdAsync(command.ClerkUserId, ct);
        if (existing is not null)
        {
            return new RegisterUserResult(await ReadProfileAsync(existing, ct), Created: false);
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
            return new RegisterUserResult(await ReadProfileAsync(winner, ct), Created: false);
        }

        return new RegisterUserResult(await ReadProfileAsync(user, ct), Created: true);
    }

    public async Task<ProfileDto> GetProfileAsync(string clerkUserId, RequestOrigin origin, CancellationToken ct)
    {
        var user = await users.FindByClerkIdAsync(clerkUserId, ct) ?? throw Errors.ProfileNotFound();

        await users.TouchAsync(user.Id, time.GetUtcNow(), ct);
        await accessLog.RecordSessionOnceAsync(user.Id, origin, ct);

        return await ReadProfileAsync(user, ct);
    }

    public async Task<ProfileDto> AcceptTermsAsync(string clerkUserId, RequestOrigin origin, CancellationToken ct)
    {
        var user = await users.FindByClerkIdAsync(clerkUserId, ct) ?? throw Errors.ProfileNotFound();
        if (user.AcceptTerms(legal.Value.TermsVersion, time.GetUtcNow()))
        {
            audit.Record($"user:{user.Id}", "user.terms_accepted", "user", user.Id, new { version = legal.Value.TermsVersion });
            // Prova do aceite, como no cadastro: de onde veio. Sem a sessão, que já pode ter o seu registro de acesso.
            accessLog.Record(user.Id, origin with { SessionId = null });
            await unitOfWork.SaveChangesAsync(ct);
        }

        return await ReadProfileAsync(user, ct);
    }

    public async Task<ProfileDto> UpdateProfileAsync(UpdateProfileCommand command, CancellationToken ct)
    {
        var user = await users.FindByClerkIdAsync(command.ClerkUserId, ct) ?? throw Errors.ProfileNotFound();

        if (command.Name is not null)
        {
            user.Rename(command.Name);
        }

        if (command.EmailPreferences is { } changes)
        {
            var current = user.EmailPreferences;
            user.ChangeEmailPreferences(new EmailPreferences(
                changes.CorrectionReady ?? current.CorrectionReady,
                changes.IllegibleReminder ?? current.IllegibleReminder,
                changes.NewThemes ?? current.NewThemes));
        }

        await unitOfWork.SaveChangesAsync(ct);
        return await ReadProfileAsync(user, ct);
    }

    public async Task SyncEmailAsync(string clerkUserId, string email, CancellationToken ct)
    {
        var user = await users.FindByClerkIdAsync(clerkUserId, ct);
        if (user is null || string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        user.ChangeEmail(email);
        audit.Record("clerk", "user.email_changed", "user", user.Id);

        await unitOfWork.SaveChangesAsync(ct);
    }

    private async Task<ProfileDto> ReadProfileAsync(User user, CancellationToken ct)
    {
        var buckets = await credits.ListAvailableAsync(user.Id, time.GetUtcNow(), ct);
        var bySource = buckets
            .GroupBy(b => (b.Source, b.ExpiresAt))
            .Select(g => new CreditBucketDto(g.Key.Source, g.Sum(b => b.Remaining), g.Key.ExpiresAt))
            .ToList();
        var subscription = await subscriptions.FindLatestAsync(user.Id, ct);

        return new ProfileDto(
            user.Id,
            user.Email,
            user.Name,
            user.IsAdmin,
            TermsAcceptanceRequired: user.TermsVersion != legal.Value.TermsVersion,
            AiNoticeAccepted: user.AiNoticeAcceptedAt is not null,
            new EmailPreferencesDto(
                user.EmailPreferences.CorrectionReady,
                user.EmailPreferences.IllegibleReminder,
                user.EmailPreferences.NewThemes),
            new CreditBalanceDto(bySource.Sum(b => b.Remaining), bySource),
            subscription is null
                ? null
                : new SubscriptionDto(subscription.Status, subscription.NextBillingAt, subscription.CardLast4),
            // A tela de compra pede a declaração do responsável; o backend a exige de novo na compra.
            NeedsGuardianAuthorization: user.BirthDate.IsMinorOn(BrasiliaTime.DateOf(time.GetUtcNow())),
            // Só vale para quem não tem plano aberto (cancelado ou nunca assinou): é o que uma assinatura nova faria.
            PlanResumesAt: subscription?.Status is SubscriptionStatus.Active or SubscriptionStatus.Paused or SubscriptionStatus.Pending
                ? null
                : await planResume.FirstChargeAtAsync(user.Id, ct));
    }
}
