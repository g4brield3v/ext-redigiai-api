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
