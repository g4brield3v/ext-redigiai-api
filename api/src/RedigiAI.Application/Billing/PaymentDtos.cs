using RedigiAI.Domain.Billing;

namespace RedigiAI.Application.Billing;

/// <summary>Compra de correções avulsas. O valor não vem do navegador: é R$ 16 × <paramref name="Quantity"/>.</summary>
/// <param name="Quantity">De 1 a 10.</param>
/// <param name="GuardianAuthorized">Declaração "Meu responsável autorizou esta compra", exigida de 16 e 17 anos.</param>
/// <param name="Card">Só no cartão: o que o formulário do Mercado Pago (Card Payment Brick) devolve.</param>
public sealed record CheckoutSingleRequest(int? Quantity, PaymentMethod? Method, bool? GuardianAuthorized, CardPaymentRequest? Card);

/// <summary>O token do cartão gerado no navegador. O número do cartão nunca chega à API.</summary>
/// <param name="IdentificationType">CPF ou CNPJ do titular, pedido pelo formulário; não é guardado.</param>
public sealed record CardPaymentRequest(
    string? Token, string? PaymentMethodId, string? IssuerId, string? IdentificationType, string? IdentificationNumber);

public sealed record CheckoutSingleCommand(
    string ClerkUserId,
    string? IdempotencyKey,
    int? Quantity,
    PaymentMethod? Method,
    bool GuardianAuthorized,
    CardPaymentRequest? Card);

/// <summary>Uma compra, como o aluno a vê.</summary>
/// <param name="Refundable">Dentro dos 7 dias do direito de arrependimento.</param>
/// <param name="Pix">O QR code e o copia-e-cola, só enquanto o Pix espera o pagamento.</param>
public sealed record PaymentDto(
    Guid Id,
    PaymentKind Kind,
    PaymentMethod Method,
    long AmountCents,
    int Quantity,
    PaymentStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ApprovedAt,
    bool Refundable,
    PixDto? Pix);

/// <param name="QrCodeBase64">A imagem do QR code em PNG, codificada em base64.</param>
/// <param name="CopyPaste">O código "copia e cola".</param>
public sealed record PixDto(string QrCodeBase64, string CopyPaste, DateTimeOffset ExpiresAt);

public sealed record PaymentListDto(IReadOnlyList<PaymentDto> Items);
