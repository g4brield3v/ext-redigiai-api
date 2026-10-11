namespace RedigiAI.Domain.Common;

/// <summary>Erro conhecido, devolvido ao cliente como {code, message, retryable, traceId}.</summary>
public sealed class AppError(string code, int httpStatus, string userMessage, bool retryable = false)
    : Exception(userMessage)
{
    public string Code { get; } = code;
    public int HttpStatus { get; } = httpStatus;
    public string UserMessage { get; } = userMessage;
    public bool Retryable { get; } = retryable;
}
