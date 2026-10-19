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
public sealed record ProviderPayment(string ProviderId, PaymentStatus Status, string? ExternalReference, long AmountCents, string Currency);

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
}

/// <summary>Confere a assinatura das notificações do provedor.</summary>
public interface IPaymentWebhookVerifier
{
    /// <param name="signature">Cabeçalho <c>x-signature</c>.</param>
    /// <param name="requestId">Cabeçalho <c>x-request-id</c>.</param>
    /// <param name="resourceId">O <c>data.id</c> da notificação.</param>
    bool IsValid(string? signature, string? requestId, string resourceId);
}
