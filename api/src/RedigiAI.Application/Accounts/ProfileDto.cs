using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Billing;

namespace RedigiAI.Application.Accounts;

public sealed record ProfileDto(
    Guid Id,
    string Email,
    string? Name,
    UserRole Role,
    bool TermsAcceptanceRequired,
    bool AiNoticeAccepted,
    EmailPreferencesDto EmailPreferences,
    CreditBalanceDto Credits,
    SubscriptionDto? Subscription);

public sealed record EmailPreferencesDto(bool CorrectionReady, bool IllegibleReminder, bool NewThemes);

public sealed record CreditBalanceDto(int Total, IReadOnlyList<CreditBucketDto> Buckets);

public sealed record CreditBucketDto(CreditSource Source, int Remaining, DateTimeOffset? ExpiresAt);

/// <summary>O plano semanal do aluno; nulo se ele nunca assinou.</summary>
/// <param name="CardLast4">Os 4 últimos dígitos do cartão, quando o Mercado Pago informa.</param>
public sealed record SubscriptionDto(SubscriptionStatus Status, DateTimeOffset? NextBillingAt, string? CardLast4);
