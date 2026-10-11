namespace RedigiAI.Domain.Common;

/// <summary>Catálogo de erros da seção 12 da especificação.</summary>
public static class Errors
{
    public static AppError Unauthenticated() =>
        new("UNAUTHENTICATED", 401, "Entre na sua conta para continuar.");

    public static AppError AgeBelowMinimum() =>
        new("AGE_BELOW_MINIMUM", 422, "A plataforma é destinada a pessoas com 16 anos ou mais.");

    public static AppError ThemeNotAvailable() =>
        new("THEME_NOT_AVAILABLE", 404, "Esse tema não está disponível. Escolha outro.");

    public static AppError RateLimited() =>
        new("RATE_LIMITED", 429, "Muitas tentativas seguidas. Aguarde um minuto.", retryable: true);

    public static AppError Internal() =>
        new("INTERNAL_ERROR", 500, "Algo deu errado do nosso lado. Tente novamente em instantes.");

    // Códigos fora da tabela da especificação; ver docs/decisoes.md.
    public static AppError Validation(string userMessage) =>
        new("VALIDATION_ERROR", 422, userMessage);

    public static AppError ProfileNotFound() =>
        new("PROFILE_NOT_FOUND", 404, "Conclua o seu cadastro para continuar.");

    public static AppError NotFound() =>
        new("NOT_FOUND", 404, "Não encontramos o que você procura.");
}
