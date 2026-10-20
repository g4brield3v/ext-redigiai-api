using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Common;

namespace RedigiAI.Application.Billing;

/// <summary>O que o formulário de cartão do Mercado Pago (Card Payment Brick) manda: conferido antes de ir para lá.</summary>
internal static class CardInput
{
    private const int MaxFieldLength = 100;

    /// <exception cref="AppError"><c>VALIDATION_ERROR</c> sem o token ou com um campo fora do formato.</exception>
    public static CardToken From(CardPaymentRequest? card)
    {
        if (card is null
            || !IsField(card.Token, required: true)
            || !IsField(card.PaymentMethodId, required: true)
            || !IsField(card.IssuerId, required: false)
            || !IsField(card.IdentificationType, required: false)
            || !IsField(card.IdentificationNumber, required: false))
        {
            throw Errors.Validation("Confira os dados do cartão e tente de novo.");
        }

        return new CardToken(card.Token!, card.PaymentMethodId!, card.IssuerId, card.IdentificationType, card.IdentificationNumber);
    }

    private static bool IsField(string? value, bool required) =>
        value is null
            ? !required
            : value.Length is > 0 and <= MaxFieldLength && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
