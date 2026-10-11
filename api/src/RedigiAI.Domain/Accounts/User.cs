using RedigiAI.Domain.Common;

namespace RedigiAI.Domain.Accounts;

public sealed record EmailPreferences(bool CorrectionReady, bool IllegibleReminder, bool NewThemes)
{
    public static EmailPreferences Default { get; } = new(true, true, false);
}

/// <summary>Usuário local, ligado à conta do Clerk por <see cref="ClerkUserId"/>.</summary>
public sealed class User : AggregateRoot
{
    public const int MaxNameLength = 120;

    private User() { }

    public Guid Id { get; private set; }
    public string ClerkUserId { get; private set; } = null!;
    public string Email { get; private set; } = null!;
    public string? Name { get; private set; }
    public BirthDate BirthDate { get; private set; }
    public string TermsVersion { get; private set; } = null!;
    public DateTimeOffset TermsAcceptedAt { get; private set; }
    public DateTimeOffset? AiNoticeAcceptedAt { get; private set; }
    public EmailPreferences EmailPreferences { get; private set; } = EmailPreferences.Default;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset LastActiveAt { get; private set; }
    public DateTimeOffset? DeletionWarnedAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }

    public static User Register(
        string clerkUserId,
        string email,
        string? name,
        DateOnly birthDate,
        string termsVersion,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clerkUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(termsVersion);

        var today = BrasiliaTime.DateOf(now);
        var birth = BirthDate.Create(birthDate, today);
        if (!birth.MeetsMinimumAgeOn(today))
        {
            throw Errors.AgeBelowMinimum();
        }

        return new User
        {
            Id = Guid.CreateVersion7(),
            ClerkUserId = clerkUserId,
            Email = email,
            Name = NormalizeName(name),
            BirthDate = birth,
            TermsVersion = termsVersion,
            TermsAcceptedAt = now,
            CreatedAt = now,
            LastActiveAt = now,
        };
    }

    public void Rename(string? name) => Name = NormalizeName(name);

    public void ChangeEmail(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        Email = email;
    }

    public void ChangeEmailPreferences(EmailPreferences preferences) => EmailPreferences = preferences;

    private static string? NormalizeName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw Errors.Validation($"O nome pode ter até {MaxNameLength} caracteres.");
        }

        return trimmed;
    }
}
