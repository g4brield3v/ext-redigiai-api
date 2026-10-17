namespace RedigiAI.Domain.Common;

/// <summary>Uma pendência de validação: o caminho do campo no JSON enviado e a mensagem para quem cadastra.</summary>
public sealed record ValidationIssue(string Path, string Message);

/// <summary>Erro conhecido, devolvido ao cliente como {code, message, retryable, traceId}.</summary>
public sealed class AppError(
    string code, int httpStatus, string userMessage, bool retryable = false, IReadOnlyList<ValidationIssue>? details = null)
    : Exception(userMessage)
{
    public string Code { get; } = code;
    public int HttpStatus { get; } = httpStatus;
    public string UserMessage { get; } = userMessage;
    public bool Retryable { get; } = retryable;

    /// <summary>Só em <c>VALIDATION_FAILED</c>: cada pendência, com o campo a que se refere.</summary>
    public IReadOnlyList<ValidationIssue>? Details { get; } = details;
}
