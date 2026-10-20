using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using RedigiAI.Application.Abstractions;
using RedigiAI.Application.Accounts;
using RedigiAI.Application.Notifications;
using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Common;
using RedigiAI.Domain.Jobs;

namespace RedigiAI.Application.Billing;

/// <param name="GuardianAuthorized">Declaração "Meu responsável autorizou esta compra", exigida de 16 e 17 anos.</param>
/// <param name="Card">O que o formulário de cartão do Mercado Pago devolve na nossa página (Card Payment Brick).</param>
public sealed record CheckoutPlanRequest(bool? GuardianAuthorized, CardPaymentRequest? Card);

public sealed record CheckoutPlanCommand(string ClerkUserId, string? IdempotencyKey, bool GuardianAuthorized, CardPaymentRequest? Card);

/// <param name="Status">
/// <c>pending</c> até a consulta ao Mercado Pago confirmar o cartão (segundos); o front acompanha em <c>GET /v1/me</c>.
/// </param>
public sealed record PlanCheckoutDto(Guid SubscriptionId, SubscriptionStatus Status);

public interface ISubscriptionService
{
    /// <summary>
    /// Assina o plano mensal com o cartão tokenizado na nossa página (checkout transparente): cria a assinatura
    /// autorizada no Mercado Pago e põe na fila a consulta dela. Exige <c>Idempotency-Key</c>: repetir a chave devolve a
    /// mesma assinatura. Ela só fica ativa pela consulta ao Mercado Pago, e as correções só entram com a cobrança
    /// aprovada. Cartão recusado: <c>PAYMENT_FAILED</c>, e a nova tentativa traz outro token.
    /// </summary>
    Task<PlanCheckoutDto> CheckoutPlanAsync(CheckoutPlanCommand command, CancellationToken ct);

    /// <summary>
    /// Cancela a renovação no Mercado Pago. As correções já concedidas seguem valendo até o fim do ciclo pago.
    /// </summary>
    Task<SubscriptionDto> CancelAsync(string clerkUserId, CancellationToken ct);
}

internal sealed class SubscriptionService(
    IUserRepository users,
    ISubscriptionRepository subscriptions,
    IPaymentGateway gateway,
    IJobQueue jobs,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    IOptions<SiteOptions> site,
    TimeProvider time) : ISubscriptionService
{
    private const string Reason = "RedigiAI: plano mensal (12 correções por mês)";

    public async Task<PlanCheckoutDto> CheckoutPlanAsync(CheckoutPlanCommand command, CancellationToken ct)
    {
        var user = await users.FindByClerkIdAsync(command.ClerkUserId, ct) ?? throw Errors.ProfileNotFound();

        var key = command.IdempotencyKey?.Trim();
        if (string.IsNullOrEmpty(key) || key.Length > Subscription.MaxKeyLength || key.Any(char.IsControl))
        {
            throw Errors.Validation("Não foi possível iniciar a assinatura. Atualize a página e tente de novo.");
        }

        var card = CardInput.From(command.Card);
        if (await subscriptions.FindByKeyAsync(user.Id, key, ct) is { } existing)
        {
            return await ResumeAsync(existing, user, card, ct);
        }

        if (await subscriptions.FindOpenAsync(user.Id, ct) is not null)
        {
            throw Errors.PlanAlreadyActive();
        }

        // Uma assinatura pendente é reaproveitada: duas abas ou dois cliques não viram duas assinaturas lá.
        if (await subscriptions.FindPendingAsync(user.Id, ct) is { } pending)
        {
            return await ResumeAsync(pending, user, card, ct);
        }

        var now = time.GetUtcNow();
        // Compras de 16 e 17 anos exigem a declaração de autorização do responsável (regra 9).
        var minor = user.BirthDate.IsMinorOn(BrasiliaTime.DateOf(now));
        if (minor && !command.GuardianAuthorized)
        {
            throw Errors.GuardianAuthorizationRequired();
        }

        var subscription = Subscription.Request(user.Id, key, minor ? now : null, now);
        subscriptions.Add(subscription);
        audit.Record($"user:{user.Id}", "subscription.requested", "subscription", subscription.Id);
        try
        {
            await unitOfWork.SaveChangesAsync(ct);
        }
        catch (UniqueConflictException)
        {
            // Outra requisição (a mesma chave, ou outra aba) gravou a pendente primeiro: vale a assinatura dela.
            var winner = await subscriptions.FindByKeyAsync(user.Id, key, ct)
                ?? await subscriptions.FindPendingAsync(user.Id, ct)
                ?? throw Errors.Internal();
            return await ResumeAsync(winner, user, card, ct);
        }

        return await CreateThereAsync(subscription, user, card, ct);
    }

    public async Task<SubscriptionDto> CancelAsync(string clerkUserId, CancellationToken ct)
    {
        var user = await users.FindByClerkIdAsync(clerkUserId, ct) ?? throw Errors.ProfileNotFound();
        // O plano aberto ou, sem ele, o checkout pendente (que ainda poderia ser autorizado lá).
        var subscription = await subscriptions.FindOpenAsync(user.Id, ct)
            ?? await subscriptions.FindPendingAsync(user.Id, ct)
            ?? throw Errors.NotFound();

        // Primeiro lá: se o Mercado Pago não cancelar, nada muda aqui e o aluno pode tentar de novo.
        if (subscription.MpPreapprovalId is { } providerId)
        {
            await gateway.CancelSubscriptionAsync(providerId, ct);
        }

        var now = time.GetUtcNow();
        await unitOfWork.InTransactionAsync(
            async () =>
            {
                if (await subscriptions.TryTransitionAsync(subscription.Id, SubscriptionStatus.Cancelled, now, ct))
                {
                    audit.Record($"user:{user.Id}", "subscription.cancelled", "subscription", subscription.Id);
                }

                return true;
            },
            ct);

        var current = await subscriptions.FindAsync(subscription.Id, ct) ?? throw Errors.Internal();
        return new SubscriptionDto(current.Status, current.NextBillingAt, current.CardLast4);
    }

    /// <summary>
    /// A assinatura pendente de novo: já criada lá, devolve a mesma (a consulta está na fila); se a criação não chegou ao
    /// Mercado Pago ou o cartão foi recusado, tenta lá com o cartão desta requisição.
    /// </summary>
    private async Task<PlanCheckoutDto> ResumeAsync(Subscription subscription, User user, CardToken card, CancellationToken ct)
    {
        if (subscription.Status != SubscriptionStatus.Pending)
        {
            throw Errors.PlanAlreadyActive();
        }

        return subscription.MpPreapprovalId is not null
            ? new PlanCheckoutDto(subscription.Id, subscription.Status)
            : await CreateThereAsync(subscription, user, card, ct);
    }

    private async Task<PlanCheckoutDto> CreateThereAsync(Subscription subscription, User user, CardToken card, CancellationToken ct)
    {
        // Uma criação por vez: o duplo clique, a outra aba ou a mesma chave em paralelo não criam lá uma segunda
        // assinatura com outro token; recebem um erro que dá para repetir em instantes.
        if (await subscriptions.TryBeginCreationAsync(subscription.Id, time.GetUtcNow(), ct) is not { } lockedUntil)
        {
            throw Errors.CheckoutInProgress();
        }

        CreatedSubscription created;
        try
        {
            // Uma tentativa anterior pode ter sido aceita lá sem a resposta chegar (tempo esgotado): vale a dela.
            created = await gateway.FindSubscriptionByReferenceAsync(subscription.Id, ct) is { } lost
                ? new CreatedSubscription(lost.ProviderId)
                : await gateway.CreateSubscriptionAsync(
                    new SubscriptionRequest(
                        subscription.Id, AttemptKey(subscription.Id, card), Payment.PlanPriceCents, Reason, user.Email, card, BackUrl()),
                    ct);
        }
        catch (Exception error)
        {
            // Nada foi gravado lá (ou a busca da próxima tentativa acha o que foi): a trava sai para tentar de novo.
            try
            {
                await subscriptions.EndCreationAsync(subscription.Id, lockedUntil, CancellationToken.None);
            }
            catch (Exception)
            {
                // A trava vence sozinha; o erro que importa é o da criação.
            }

            if (error is AppError { Code: "PAYMENT_FAILED" })
            {
                // Sem o token: só que houve a recusa, para notar tentativas seguidas de cartões.
                audit.Record($"user:{user.Id}", "subscription.card_declined", "subscription", subscription.Id);
                await unitOfWork.SaveChangesAsync(CancellationToken.None);
            }

            throw;
        }

        // A assinatura já existe lá: gravar o id não depende de o aluno continuar na página.
        ct = CancellationToken.None;
        var now = time.GetUtcNow();
        var attached = await unitOfWork.InTransactionAsync(
            async () =>
            {
                if (!await subscriptions.TryAttachProviderAsync(subscription.Id, created.ProviderId, now, ct))
                {
                    return false;
                }

                audit.Record("system", "subscription.created", "subscription", subscription.Id, new { mpPreapprovalId = created.ProviderId });
                // A ativação vem da consulta, não da resposta da criação (regra 19); o aviso do Mercado Pago faz o mesmo.
                jobs.Enqueue(
                    JobTypes.ProcessSubscription,
                    new ProcessSubscriptionPayload(SubscriptionNotice.Subscription, created.ProviderId, EventId: null),
                    ProcessSubscriptionHandler.Attempts);
                return true;
            },
            ct);
        if (attached)
        {
            return new PlanCheckoutDto(subscription.Id, SubscriptionStatus.Pending);
        }

        // Outra requisição já ligou esta assinatura a outro preapproval lá: vale o dela, e este, sem dono, é cancelado.
        // Se o cancelamento falhar, o aviso dele chega ao process_subscription, que o cancela.
        audit.Record("system", "subscription.orphan", "subscription", subscription.Id, new { mpPreapprovalId = created.ProviderId });
        await unitOfWork.SaveChangesAsync(ct);
        await gateway.CancelSubscriptionAsync(created.ProviderId, ct);
        return new PlanCheckoutDto(subscription.Id, SubscriptionStatus.Pending);
    }

    /// <summary>
    /// A chave de idempotência lá, por tentativa: a mesma assinatura com o mesmo token é a mesma criação; um token novo
    /// (depois de um cartão recusado) é outra.
    /// </summary>
    private static string AttemptKey(Guid subscriptionId, CardToken card) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{subscriptionId:N}:{card.Token}")))[..32];

    /// <summary>
    /// Exigido pelo Mercado Pago na criação: o perfil, onde o aluno vê a assinatura. No checkout transparente ninguém é
    /// levado para lá, mas o Mercado Pago só aceita endereço público em https: no desenvolvimento (http, localhost), vai
    /// o perfil do site.
    /// </summary>
    private string BackUrl()
    {
        var options = site.Value;
        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var url))
        {
            throw Errors.PaymentUnavailable();
        }

        if (url.Scheme == Uri.UriSchemeHttps && !url.IsLoopback)
        {
            return $"{options.BaseUrl.TrimEnd('/')}/perfil";
        }

        return !options.RequireHttps ? PublicProfileUrl : throw Errors.PaymentUnavailable();
    }

    private const string PublicProfileUrl = "https://redigiai.com.br/perfil";
}
