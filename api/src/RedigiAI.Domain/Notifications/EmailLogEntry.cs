namespace RedigiAI.Domain.Notifications;

/// <summary>
/// Um e-mail por evento: <c>template</c> + <c>ref_id</c> é único. A linha é reservada antes do envio e marcada
/// como enviada depois; reserva antiga sem envio (o processo caiu no meio) pode ser retomada.
/// </summary>
public sealed class EmailLogEntry
{
    private EmailLogEntry() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Template { get; private set; } = null!;

    /// <summary>O que originou o e-mail; nos e-mails da redação, o id da redação.</summary>
    public Guid RefId { get; private set; }

    /// <summary>Quando um envio começou; nulo depois de uma falha, para a nova tentativa poder seguir.</summary>
    public DateTimeOffset? ClaimedAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }
    public string? ProviderMessageId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static EmailLogEntry Claim(Guid userId, string template, Guid refId, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Template = template,
            RefId = refId,
            ClaimedAt = now,
            CreatedAt = now,
        };
}

public interface IEmailLogRepository
{
    /// <summary>
    /// Reserva o envio na hora, fora de transação: cria a linha ou retoma uma reserva sem envio mais antiga que
    /// <paramref name="staleBefore"/>.
    /// </summary>
    /// <returns>O id da linha reservada; nulo se o e-mail já saiu ou outro processo está enviando.</returns>
    Task<Guid?> TryClaimAsync(EmailLogEntry entry, DateTimeOffset staleBefore, CancellationToken ct);

    Task MarkSentAsync(Guid id, string? providerMessageId, DateTimeOffset now, CancellationToken ct);

    /// <summary>Desfaz a reserva depois de uma falha no envio, para a nova tentativa do job não esperar.</summary>
    Task ReleaseAsync(Guid id, CancellationToken ct);
}
