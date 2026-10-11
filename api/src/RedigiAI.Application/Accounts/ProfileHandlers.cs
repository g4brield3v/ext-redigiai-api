using Microsoft.Extensions.Options;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Common;

namespace RedigiAI.Application.Accounts;

public sealed class GetProfileHandler(
    IUserRepository users,
    ICreditRepository credits,
    IAccessLog accessLog,
    IOptions<LegalOptions> legal,
    TimeProvider time)
{
    public async Task<ProfileDto> HandleAsync(string clerkUserId, RequestOrigin origin, CancellationToken ct)
    {
        var user = await users.FindByClerkIdAsync(clerkUserId, ct) ?? throw Errors.ProfileNotFound();

        await users.TouchAsync(user.Id, time.GetUtcNow(), ct);
        await accessLog.RecordSessionOnceAsync(user.Id, origin, ct);

        return await new ProfileReader(credits, legal, time).ReadAsync(user, ct);
    }
}

public sealed record UpdateEmailPreferences(bool? CorrectionReady, bool? IllegibleReminder, bool? NewThemes);

/// <param name="Name">Ausente = não muda; vazio = apaga o nome.</param>
public sealed record UpdateProfileCommand(string ClerkUserId, string? Name, UpdateEmailPreferences? EmailPreferences);

public sealed class UpdateProfileHandler(
    IUserRepository users,
    ICreditRepository credits,
    IUnitOfWork unitOfWork,
    IOptions<LegalOptions> legal,
    TimeProvider time)
{
    public async Task<ProfileDto> HandleAsync(UpdateProfileCommand command, CancellationToken ct)
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
        return await new ProfileReader(credits, legal, time).ReadAsync(user, ct);
    }
}

/// <summary>Reflete no usuário local o e-mail alterado no Clerk (webhook <c>user.updated</c>).</summary>
public sealed class SyncClerkEmailHandler(IUserRepository users, IAuditLog audit, IUnitOfWork unitOfWork)
{
    public async Task HandleAsync(string clerkUserId, string email, CancellationToken ct)
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
}
