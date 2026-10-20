namespace RedigiAI.Domain.Billing;

public interface ISubscriptionRepository
{
    void Add(Subscription subscription);

    Task<Subscription?> FindAsync(Guid subscriptionId, CancellationToken ct);

    Task<Subscription?> FindByKeyAsync(Guid userId, string idempotencyKey, CancellationToken ct);

    Task<Subscription?> FindByProviderIdAsync(string mpPreapprovalId, CancellationToken ct);

    /// <summary>A assinatura pendente mais recente do aluno (checkout ainda não concluído), se houver.</summary>
    Task<Subscription?> FindPendingAsync(Guid userId, CancellationToken ct);

    /// <summary>A assinatura ativa ou pausada do aluno, se houver (no máximo uma, garantido pelo banco).</summary>
    Task<Subscription?> FindOpenAsync(Guid userId, CancellationToken ct);

    /// <summary>
    /// A assinatura mais recente do aluno que chegou a ser autorizada (ativa, pausada ou cancelada); <c>null</c> se ele
    /// nunca assinou. Um checkout abandonado (pendente) não conta.
    /// </summary>
    Task<Subscription?> FindLatestAsync(Guid userId, CancellationToken ct);

    /// <summary>
    /// Grava o que o Mercado Pago devolveu na criação: o id e a URL do checkout. Só vale enquanto a assinatura não tem id
    /// ou tem este mesmo.
    /// </summary>
    Task<bool> TryAttachProviderAsync(Guid subscriptionId, string mpPreapprovalId, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Trava a criação lá: <c>UPDATE … SET creating_until = @now + trava WHERE id = @id AND status = 'pending' AND
    /// mp_preapproval_id IS NULL AND (creating_until IS NULL OR creating_until &lt;= @now)</c>. Devolve o valor gravado,
    /// que identifica o dono da trava; nulo se outra requisição está criando, ou já criou. No sucesso, quem solta a trava
    /// é <see cref="TryAttachProviderAsync"/>, junto com o id de lá.
    /// </summary>
    Task<DateTimeOffset?> TryBeginCreationAsync(Guid subscriptionId, DateTimeOffset now, CancellationToken ct);

    /// <summary>Solta a trava depois de uma criação que falhou, só se ainda for a desta requisição.</summary>
    Task EndCreationAsync(Guid subscriptionId, DateTimeOffset lockedUntil, CancellationToken ct);

    /// <summary>
    /// Muda a situação com <c>UPDATE … WHERE status IN (…)</c> (regra 10), na transação aberta. Zero linhas: outro
    /// processo já avançou, ou a transição não é permitida.
    /// </summary>
    /// <remarks>Ativar com outra assinatura aberta do mesmo aluno viola o índice único: a transação falha.</remarks>
    Task<bool> TryTransitionAsync(Guid subscriptionId, SubscriptionStatus to, DateTimeOffset now, CancellationToken ct);

    /// <summary>A próxima cobrança e o final do cartão, como o Mercado Pago informa.</summary>
    Task UpdateBillingAsync(Guid subscriptionId, DateTimeOffset? nextBillingAt, string? cardLast4, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Ids no Mercado Pago das assinaturas pendentes criadas entre <paramref name="createdAfter"/> e
    /// <paramref name="createdBefore"/> que não têm consulta na fila: a reconciliação volta a perguntar por elas.
    /// </summary>
    Task<IReadOnlyList<string>> ListPendingToRecheckAsync(
        DateTimeOffset createdAfter, DateTimeOffset createdBefore, int limit, CancellationToken ct);
}
