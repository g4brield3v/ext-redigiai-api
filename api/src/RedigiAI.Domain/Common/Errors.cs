namespace RedigiAI.Domain.Common;

/// <summary>Catálogo de erros da seção 12 da especificação.</summary>
public static class Errors
{
    public static AppError Unauthenticated() =>
        new("UNAUTHENTICATED", 401, "Entre na sua conta para continuar.");

    public static AppError AgeBelowMinimum() =>
        new("AGE_BELOW_MINIMUM", 422, "A plataforma é destinada a pessoas com 16 anos ou mais.");

    public static AppError QuotaExceeded() =>
        new("QUOTA_EXCEEDED", 402, "Você não tem correções suficientes para este envio.");

    public static AppError InvalidFile() =>
        new("INVALID_FILE", 422, "Envie uma foto JPG da folha, com até 10 MB.");

    public static AppError EssayNotFound() =>
        new("ESSAY_NOT_FOUND", 404, "Não encontramos essa redação na sua conta.");

    public static AppError ReviewIncomplete() =>
        new("REVIEW_INCOMPLETE", 422, "Preencha todas as palavras para enviar.");

    public static AppError ReviewExpired() =>
        new(
            "REVIEW_EXPIRED",
            409,
            "O prazo para preencher as palavras acabou. A redação foi cancelada e a correção voltou para o seu saldo.");

    public static AppError InvalidState() =>
        new("INVALID_STATE", 409, "Essa ação não está disponível para esta redação agora.");

    /// <summary>Uso interno da fila: a IA está fora do ar ou sobrecarregada; o job tenta de novo.</summary>
    public static AppError AiUnavailable() =>
        new("AI_UNAVAILABLE", 503, "Algo deu errado do nosso lado. Tente novamente em instantes.", retryable: true);

    /// <summary>Uso interno da fila: falha que não adianta repetir (pedido recusado, resposta inválida duas vezes, teto de gasto).</summary>
    public static AppError AiFailed(string code) =>
        new(code, 500, "Algo deu errado do nosso lado. Tente novamente em instantes.");

    public static AppError PromptNotAvailable() =>
        new("PROMPT_NOT_AVAILABLE", 404, "Essa proposta não está disponível. Escolha outra.");

    public static AppError PromptNotDraft() =>
        new("PROMPT_NOT_DRAFT", 409, "Esta proposta já foi publicada. Crie uma nova versão para alterar.");

    public static AppError PromptChanged() =>
        new("PROMPT_CHANGED", 409, "A proposta foi alterada em outro lugar. Recarregue para continuar.");

    public static AppError PromptInvalidState() =>
        new("INVALID_STATE", 409, "Essa ação não está disponível para esta proposta agora.");

    /// <summary>Só nas rotas de administração: as pendências vão em <see cref="AppError.Details"/>.</summary>
    public static AppError ValidationFailed(IReadOnlyList<ValidationIssue> details) =>
        new("VALIDATION_FAILED", 422, "Há pendências na proposta.", details: details);

    public static AppError AdminSecondFactorRequired() =>
        new("ADMIN_2FA_REQUIRED", 403, "Ative a verificação em duas etapas para acessar a administração.");

    public static AppError PaymentFailed() =>
        new("PAYMENT_FAILED", 402, "O pagamento não foi aprovado. Tente outro meio de pagamento.", retryable: true);

    public static AppError PlanAlreadyActive() =>
        new("PLAN_ALREADY_ACTIVE", 409, "Você já tem um plano mensal ativo.");

    public static AppError GuardianAuthorizationRequired() =>
        new("GUARDIAN_AUTHORIZATION_REQUIRED", 422, "Confirme que o seu responsável autorizou esta compra.");

    public static AppError RateLimited() =>
        new("RATE_LIMITED", 429, "Muitas tentativas seguidas. Aguarde um minuto.", retryable: true);

    public static AppError Internal() =>
        new("INTERNAL_ERROR", 500, "Algo deu errado do nosso lado. Tente novamente em instantes.");

    // Códigos fora da tabela da especificação; ver docs/decisoes.md.
    public static AppError Validation(string userMessage) =>
        new("VALIDATION_ERROR", 422, userMessage);

    public static AppError ProfileNotFound() =>
        new("PROFILE_NOT_FOUND", 404, "Conclua o seu cadastro para continuar.");

    /// <summary>O Mercado Pago não respondeu ou não está configurado: nada foi cobrado.</summary>
    public static AppError PaymentUnavailable() =>
        new("PAYMENT_UNAVAILABLE", 503, "Não conseguimos falar com o meio de pagamento. Tente de novo em instantes.", retryable: true);

    public static AppError NotFound() =>
        new("NOT_FOUND", 404, "Não encontramos o que você procura.");
}
