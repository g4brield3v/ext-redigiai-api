namespace RedigiAI.Application.Abstractions;

/// <param name="Template">Para o registro e para os testes; não vai no e-mail.</param>
public sealed record EmailMessage(string To, string Subject, string Html, string Text, string Template, Guid RefId);

public interface IEmailSender
{
    /// <summary>
    /// Envia um e-mail. Um <c>AppError</c> não retentável indica pedido recusado pelo provedor; qualquer outra
    /// exceção leva a nova tentativa do job.
    /// </summary>
    /// <returns>O id da mensagem no provedor, quando ele informa.</returns>
    Task<string?> SendAsync(EmailMessage message, CancellationToken ct);
}
