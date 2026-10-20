namespace RedigiAI.Domain.Billing;

public enum SubscriptionStatus
{
    /// <summary>Criada aqui e no Mercado Pago, esperando o aluno autorizar o cartão lá.</summary>
    Pending,
    Active,
    Paused,
    Cancelled,
}

/// <summary>
/// Plano mensal, cobrado no cartão pelo Mercado Pago (preapproval). Especificação, seção 9: cada cobrança aprovada dá
/// 12 correções, que valem até a cobrança seguinte. A situação só muda pelo que o Mercado Pago informa (ou pelo
/// cancelamento pedido pelo aluno), com <c>UPDATE</c> condicional no repositório (regra 10).
/// </summary>
public sealed class Subscription
{
    /// <summary>Correções de cada cobrança aprovada. Não acumulam: a renovação zera as que sobraram.</summary>
    public const int CreditsPerCycle = 12;

    public const int MaxKeyLength = 100;

    private Subscription() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }

    /// <summary>O id da assinatura no Mercado Pago; nulo até a criação lá responder.</summary>
    public string? MpPreapprovalId { get; private set; }
    public SubscriptionStatus Status { get; private set; }

    /// <summary>A <c>Idempotency-Key</c> do pedido: repetir a chave devolve esta assinatura.</summary>
    public string IdempotencyKey { get; private set; } = null!;

    /// <summary>
    /// Até quando uma requisição está criando a assinatura lá: outra, no mesmo instante, não cria uma segunda (cada
    /// tentativa leva um token de cartão diferente, e o Mercado Pago não as juntaria). Vale só se o processo cair no meio.
    /// </summary>
    public DateTimeOffset? CreatingUntil { get; private set; }

    /// <summary>Quanto dura a trava da criação lá, folgado sobre o tempo limite da chamada ao Mercado Pago.</summary>
    public static readonly TimeSpan CreationLock = TimeSpan.FromMinutes(2);

    /// <summary>Quando o assinante de 16 ou 17 anos declarou a autorização do responsável.</summary>
    public DateTimeOffset? GuardianAuthorizedAt { get; private set; }
    public DateTimeOffset? NextBillingAt { get; private set; }
    public string? CardLast4 { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }

    /// <summary>O pedido de assinatura: começa pendente, antes de chamar o Mercado Pago.</summary>
    public static Subscription Request(Guid userId, string idempotencyKey, DateTimeOffset? guardianAuthorizedAt, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > MaxKeyLength)
        {
            throw new ArgumentException("Invalid idempotency key.", nameof(idempotencyKey));
        }

        return new Subscription
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Status = SubscriptionStatus.Pending,
            IdempotencyKey = idempotencyKey,
            GuardianAuthorizedAt = guardianAuthorizedAt,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// As situações de onde se pode chegar a <paramref name="to"/>. Pendente vira ativa (cartão autorizado); ativa e
    /// pausada se alternam (cobrança recusada, cobrança regularizada); qualquer uma, menos cancelada, vira cancelada.
    /// </summary>
    public static IReadOnlyList<SubscriptionStatus> ReachableFrom(SubscriptionStatus to) => to switch
    {
        SubscriptionStatus.Active => [SubscriptionStatus.Pending, SubscriptionStatus.Paused],
        SubscriptionStatus.Paused => [SubscriptionStatus.Active],
        SubscriptionStatus.Cancelled => [SubscriptionStatus.Pending, SubscriptionStatus.Active, SubscriptionStatus.Paused],
        _ => [],
    };

    /// <summary>Os 4 últimos dígitos do cartão, se o Mercado Pago informou algo nesse formato.</summary>
    public static string? CardLast4From(string? digits) =>
        digits is { Length: 4 } && digits.All(char.IsAsciiDigit) ? digits : null;
}
