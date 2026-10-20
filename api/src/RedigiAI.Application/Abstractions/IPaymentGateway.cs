using RedigiAI.Domain.Billing;

namespace RedigiAI.Application.Abstractions;

/// <param name="PaymentId">O id da compra aqui: vai como referência externa e como chave de idempotência lá.</param>
/// <param name="PayerEmail">Exigido pelo provedor; não vai para log.</param>
public sealed record ChargeRequest(Guid PaymentId, long AmountCents, string Description, string PayerEmail);

/// <summary>O que o formulário de cartão do provedor (Card Payment Brick) devolve no navegador. Nunca o número do cartão.</summary>
public sealed record CardToken(
    string Token, string PaymentMethodId, string? IssuerId, string? IdentificationType, string? IdentificationNumber);

/// <param name="Status">A situação que o provedor informou na criação.</param>
public sealed record CreatedCharge(
    string ProviderId,
    PaymentStatus Status,
    string? PixQrCode = null,
    string? PixQrCodeBase64 = null,
    DateTimeOffset? PixExpiresAt = null);

/// <summary>Um pagamento como o provedor o vê agora.</summary>
/// <param name="ExternalReference">O id da compra aqui, que foi enviado na criação.</param>
/// <param name="RefundedCents">Quanto já foi estornado, somando os estornos parciais.</param>
public sealed record ProviderPayment(
    string ProviderId, PaymentStatus Status, string? ExternalReference, long AmountCents, string Currency, long RefundedCents = 0);

/// <param name="SubscriptionId">O id da assinatura aqui: vai como referência externa.</param>
/// <param name="BackUrl">Para onde o Mercado Pago devolve o aluno depois do checkout.</param>
public sealed record SubscriptionRequest(Guid SubscriptionId, long AmountCents, string Reason, string PayerEmail, string BackUrl);

/// <param name="CheckoutUrl">O checkout do Mercado Pago, onde o aluno autoriza o cartão.</param>
public sealed record CreatedSubscription(string ProviderId, string CheckoutUrl);

/// <summary>Uma assinatura como o provedor a vê agora.</summary>
/// <param name="ExternalReference">O id da assinatura aqui, enviado na criação.</param>
/// <param name="NextChargeAt">A próxima cobrança.</param>
public sealed record ProviderSubscription(
    string ProviderId, SubscriptionStatus Status, string? ExternalReference, DateTimeOffset? NextChargeAt, string? CardLast4);

/// <summary>Uma cobrança de uma assinatura; o pagamento dela só existe depois que o provedor tenta cobrar.</summary>
public sealed record ProviderSubscriptionCharge(string ProviderId, string PreapprovalId, string? PaymentId);

/// <summary>O provedor de pagamentos (Mercado Pago). O cartão nunca passa por aqui: só o token do navegador.</summary>
public interface IPaymentGateway
{
    /// <exception cref="Domain.Common.AppError">
    /// <c>PAYMENT_UNAVAILABLE</c> quando o provedor não responde ou não está configurado; <c>PAYMENT_FAILED</c> quando
    /// recusa o pedido.
    /// </exception>
    Task<CreatedCharge> CreatePixAsync(ChargeRequest request, DateTimeOffset expiresAt, CancellationToken ct);

    /// <inheritdoc cref="CreatePixAsync" />
    Task<CreatedCharge> CreateCardAsync(ChargeRequest request, CardToken card, CancellationToken ct);

    /// <summary>Consulta o pagamento na API do provedor. É a única fonte para conceder créditos (regra 19).</summary>
    /// <returns><c>null</c> se o provedor não conhece o id.</returns>
    Task<ProviderPayment?> GetPaymentAsync(string providerId, CancellationToken ct);

    /// <summary>Cria a assinatura (preapproval) pendente; o aluno autoriza o cartão no checkout de lá.</summary>
    /// <inheritdoc cref="CreatePixAsync" path="/exception" />
    Task<CreatedSubscription> CreateSubscriptionAsync(SubscriptionRequest request, CancellationToken ct);

    /// <returns><c>null</c> se o provedor não conhece o id.</returns>
    Task<ProviderSubscription?> GetSubscriptionAsync(string providerId, CancellationToken ct);

    /// <returns><c>null</c> se o provedor não conhece o id.</returns>
    Task<ProviderSubscriptionCharge?> GetSubscriptionChargeAsync(string providerId, CancellationToken ct);

    /// <summary>
    /// Devolve <paramref name="amountCents"/> do pagamento ao comprador. <paramref name="idempotencyKey"/> faz a mesma
    /// devolução, repetida, acontecer uma vez só lá.
    /// </summary>
    /// <returns>O id do reembolso lá.</returns>
    /// <inheritdoc cref="CreatePixAsync" path="/exception" />
    Task<string> RefundAsync(string paymentProviderId, long amountCents, Guid idempotencyKey, CancellationToken ct);

    /// <summary>Cancela a assinatura lá: não há mais cobranças.</summary>
    Task CancelSubscriptionAsync(string providerId, CancellationToken ct);
}

/// <summary>Confere a assinatura das notificações do provedor.</summary>
public interface IPaymentWebhookVerifier
{
    /// <param name="signature">Cabeçalho <c>x-signature</c>.</param>
    /// <param name="requestId">Cabeçalho <c>x-request-id</c>.</param>
    /// <param name="resourceId">O <c>data.id</c> da notificação.</param>
    bool IsValid(string? signature, string? requestId, string resourceId);
}
