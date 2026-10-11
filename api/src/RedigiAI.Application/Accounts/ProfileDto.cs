using Microsoft.Extensions.Options;
using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Billing;

namespace RedigiAI.Application.Accounts;

public sealed record ProfileDto(
    Guid Id,
    string Email,
    string? Name,
    bool TermsAcceptanceRequired,
    bool AiNoticeAccepted,
    EmailPreferencesDto EmailPreferences,
    CreditBalanceDto Credits);

public sealed record EmailPreferencesDto(bool CorrectionReady, bool IllegibleReminder, bool NewThemes);

public sealed record CreditBalanceDto(int Total, IReadOnlyList<CreditBucketDto> Buckets);

public sealed record CreditBucketDto(CreditSource Source, int Remaining, DateTimeOffset? ExpiresAt);

internal sealed class ProfileReader(ICreditRepository credits, IOptions<LegalOptions> legal, TimeProvider time)
{
    public async Task<ProfileDto> ReadAsync(User user, CancellationToken ct)
    {
        var buckets = await credits.ListAvailableAsync(user.Id, time.GetUtcNow(), ct);
        var bySource = buckets
            .GroupBy(b => (b.Source, b.ExpiresAt))
            .Select(g => new CreditBucketDto(g.Key.Source, g.Sum(b => b.Remaining), g.Key.ExpiresAt))
            .ToList();

        return new ProfileDto(
            user.Id,
            user.Email,
            user.Name,
            TermsAcceptanceRequired: user.TermsVersion != legal.Value.TermsVersion,
            AiNoticeAccepted: user.AiNoticeAcceptedAt is not null,
            new EmailPreferencesDto(
                user.EmailPreferences.CorrectionReady,
                user.EmailPreferences.IllegibleReminder,
                user.EmailPreferences.NewThemes),
            new CreditBalanceDto(bySource.Sum(b => b.Remaining), bySource));
    }
}
