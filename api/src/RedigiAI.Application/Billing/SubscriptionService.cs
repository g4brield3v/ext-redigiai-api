using Microsoft.Extensions.Options;
using RedigiAI.Application.Abstractions;
using RedigiAI.Application.Accounts;
using RedigiAI.Application.Notifications;
using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Common;

namespace RedigiAI.Application.Billing;

/// <param name="GuardianAuthorized">Declaração "Meu responsável autorizou esta compra", exigida de 16 e 17 anos.</param>
public sealed record CheckoutPlanRequest(bool? GuardianAuthorized);

public sealed record CheckoutPlanCommand(string ClerkUserId, string? IdempotencyKey, bool GuardianAuthorized);

/// <param name="CheckoutUrl">O checkout do Mercado Pago, onde o aluno autoriza o cartão.</param>
public sealed record PlanCheckoutDto(Guid SubscriptionId, string CheckoutUrl);

public interface ISubscriptionService
{
    /// <summary>
    /// Assina o plano mensal: cria a assinatura no Mercado Pago e devolve o checkout de lá. Exige
    /// <c>Idempotency-Key</c>: repetir a chave devolve o mesmo checkout. A assinatura só fica ativa quando o
    /// Mercado Pago avisar que o cartão foi autorizado, e as correções só entram com a cobrança aprovada.
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
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    IOptions<SiteOptions> site,
    TimeProvider time) : ISubscriptionService
{
    private const int AdultAge = 18;
    private const string Reason = "RedigiAI: plano mensal (12 correções por mês)";

    public async Task<PlanCheckoutDto> CheckoutPlanAsync(CheckoutPlanCommand command, CancellationToken ct)
    {
        var user = await users.FindByClerkIdAsync(command.ClerkUserId, ct) ?? throw Errors.ProfileNotFound();

        var key = command.IdempotencyKey?.Trim();
        if (string.IsNullOrEmpty(key) || key.Length > Subscription.MaxKeyLength || key.Any(char.IsControl))
        {
            throw Errors.Validation("Não foi possível iniciar a assinatura. Atualize a página e tente de novo.");
        }

        if (await subscriptions.FindByKeyAsync(user.Id, key, ct) is { } existing)
        {
            return await ResumeAsync(existing, user, ct);
        }

        if (await subscriptions.FindOpenAsync(user.Id, ct) is not null)
        {
            throw Errors.PlanAlreadyActive();
        }

        var now = time.GetUtcNow();
        // Compras de 16 e 17 anos exigem a declaração de autorização do responsável (regra 9).
        var minor = user.BirthDate.AgeOn(BrasiliaTime.DateOf(now)) < AdultAge;
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
            // Outra requisição com a mesma chave chegou primeiro: vale a assinatura dela.
            var winner = await subscriptions.FindByKeyAsync(user.Id, key, ct) ?? throw Errors.Internal();
            return await ResumeAsync(winner, user, ct);
        }

        return await CreateThereAsync(subscription, user, ct);
    }

    public async Task<SubscriptionDto> CancelAsync(string clerkUserId, CancellationToken ct)
    {
        var user = await users.FindByClerkIdAsync(clerkUserId, ct) ?? throw Errors.ProfileNotFound();
        var subscription = await subscriptions.FindOpenAsync(user.Id, ct);
        if (subscription?.MpPreapprovalId is not { } providerId)
        {
            throw Errors.NotFound();
        }

        // Primeiro lá: se o Mercado Pago não cancelar, nada muda aqui e o aluno pode tentar de novo.
        await gateway.CancelSubscriptionAsync(providerId, ct);
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

    /// <summary>A mesma chave de novo: devolve o checkout; se a criação não chegou ao Mercado Pago, tenta de novo lá.</summary>
    private async Task<PlanCheckoutDto> ResumeAsync(Subscription subscription, User user, CancellationToken ct)
    {
        if (subscription.Status != SubscriptionStatus.Pending)
        {
            throw Errors.PlanAlreadyActive();
        }

        return subscription.CheckoutUrl is { } url
            ? new PlanCheckoutDto(subscription.Id, url)
            : await CreateThereAsync(subscription, user, ct);
    }

    private async Task<PlanCheckoutDto> CreateThereAsync(Subscription subscription, User user, CancellationToken ct)
    {
        var created = await gateway.CreateSubscriptionAsync(
            new SubscriptionRequest(subscription.Id, Payment.PlanPriceCents, Reason, user.Email, BackUrl()), ct);

        var now = time.GetUtcNow();
        await unitOfWork.InTransactionAsync(
            async () =>
            {
                if (await subscriptions.TryAttachProviderAsync(subscription.Id, created.ProviderId, created.CheckoutUrl, now, ct))
                {
                    audit.Record("system", "subscription.created", "subscription", subscription.Id, new { mpPreapprovalId = created.ProviderId });
                }

                return true;
            },
            ct);

        return new PlanCheckoutDto(subscription.Id, created.CheckoutUrl);
    }

    /// <summary>O aluno volta do checkout do Mercado Pago para o perfil, onde vê a assinatura.</summary>
    private string BackUrl()
    {
        var options = site.Value;
        var valid = Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var url)
            && (url.Scheme == Uri.UriSchemeHttps || (!options.RequireHttps && url.Scheme == Uri.UriSchemeHttp));
        return valid ? $"{options.BaseUrl.TrimEnd('/')}/perfil" : throw Errors.PaymentUnavailable();
    }
}
