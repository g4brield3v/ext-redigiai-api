using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Common;
using RedigiAI.Domain.Jobs;

namespace RedigiAI.Application.Billing;

public interface IPaymentService
{
    /// <summary>
    /// Compra de correções avulsas por Pix ou cartão. Exige <c>Idempotency-Key</c>: repetir a chave devolve a mesma
    /// compra, sem cobrar de novo. Os créditos só entram depois que o job <c>process_payment</c> consulta o Mercado Pago.
    /// </summary>
    Task<PaymentDto> CheckoutSingleAsync(CheckoutSingleCommand command, CancellationToken ct);

    /// <summary>Uma compra do aluno; de outro aluno, 404.</summary>
    Task<PaymentDto> GetAsync(string clerkUserId, Guid paymentId, CancellationToken ct);

    Task<PaymentListDto> ListAsync(string clerkUserId, CancellationToken ct);

    /// <summary>
    /// Reconciliação: volta a consultar no Mercado Pago as compras pendentes há mais de 2 minutos (até 3 dias), para
    /// que um aviso perdido ou um job morto não deixe um Pix pago sem as correções.
    /// </summary>
    /// <returns>Quantas consultas entraram na fila agora.</returns>
    Task<int> RecheckPendingAsync(CancellationToken ct);
}

internal sealed class PaymentService(
    IUserRepository users,
    IPaymentRepository payments,
    IPaymentGateway gateway,
    IJobQueue jobs,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    TimeProvider time) : IPaymentService
{
    private const int AdultAge = 18;
    private const int ListLimit = 50;
    private const int MaxCardFieldLength = 100;
    private const int RechecksPerSweep = 200;
    private static readonly TimeSpan RecheckAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RecheckFor = TimeSpan.FromDays(3);

    public async Task<PaymentDto> CheckoutSingleAsync(CheckoutSingleCommand command, CancellationToken ct)
    {
        var user = await users.FindByClerkIdAsync(command.ClerkUserId, ct) ?? throw Errors.ProfileNotFound();

        var key = command.IdempotencyKey?.Trim();
        if (string.IsNullOrEmpty(key) || key.Length > Payment.MaxKeyLength || key.Any(char.IsControl))
        {
            throw Errors.Validation("Não foi possível iniciar o pagamento. Atualize a página e tente de novo.");
        }

        if (await payments.FindByKeyAsync(user.Id, key, ct) is { } existing)
        {
            return await ResumeAsync(existing, user, command.Card, ct);
        }

        if (command.Quantity is not { } quantity || quantity is < 1 or > Payment.MaxQuantity)
        {
            throw Errors.Validation($"Escolha de 1 a {Payment.MaxQuantity} correções.");
        }

        if (command.Method is not { } method)
        {
            throw Errors.Validation("Escolha Pix ou cartão.");
        }

        if (method == PaymentMethod.Card)
        {
            _ = CardTokenFrom(command.Card);
        }

        var now = time.GetUtcNow();
        // Compras de 16 e 17 anos exigem a declaração de autorização do responsável (regra 9).
        var minor = user.BirthDate.AgeOn(BrasiliaTime.DateOf(now)) < AdultAge;
        if (minor && !command.GuardianAuthorized)
        {
            throw Errors.GuardianAuthorizationRequired();
        }

        var payment = Payment.StartSingle(user.Id, method, quantity, key, minor ? now : null, now);
        payments.Add(payment);
        audit.Record(
            $"user:{user.Id}",
            "payment.started",
            "payment",
            payment.Id,
            new { kind = "single", method = method.ToString().ToLowerInvariant(), quantity, amountCents = payment.AmountCents });
        try
        {
            await unitOfWork.SaveChangesAsync(ct);
        }
        catch (UniqueConflictException)
        {
            // Outra requisição com a mesma chave chegou primeiro: vale a compra dela.
            var winner = await payments.FindByKeyAsync(user.Id, key, ct) ?? throw Errors.Internal();
            return await ResumeAsync(winner, user, command.Card, ct);
        }

        return await ChargeAsync(payment, user, command.Card, ct);
    }

    public async Task<PaymentDto> GetAsync(string clerkUserId, Guid paymentId, CancellationToken ct)
    {
        var user = await users.FindByClerkIdAsync(clerkUserId, ct) ?? throw Errors.ProfileNotFound();
        var payment = await payments.FindAsync(paymentId, ct);
        if (payment is null || payment.UserId != user.Id || payment.MpPaymentId is null)
        {
            throw Errors.NotFound();
        }

        return ToDto(payment);
    }

    public async Task<PaymentListDto> ListAsync(string clerkUserId, CancellationToken ct)
    {
        var user = await users.FindByClerkIdAsync(clerkUserId, ct) ?? throw Errors.ProfileNotFound();
        return new PaymentListDto([.. (await payments.ListByUserAsync(user.Id, ListLimit, ct)).Select(ToDto)]);
    }

    public async Task<int> RecheckPendingAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var pending = await payments.ListPendingToRecheckAsync(now - RecheckFor, now - RecheckAfter, RechecksPerSweep, ct);
        foreach (var mpPaymentId in pending)
        {
            jobs.Enqueue(JobTypes.ProcessPayment, new ProcessPaymentPayload(mpPaymentId, EventId: null), ProcessPaymentHandler.Attempts);
        }

        await unitOfWork.SaveChangesAsync(ct);
        return pending.Count;
    }

    /// <summary>A mesma chave de novo: devolve a compra; se ela não chegou ao Mercado Pago, tenta de novo lá.</summary>
    private async Task<PaymentDto> ResumeAsync(Payment payment, User user, CardPaymentRequest? card, CancellationToken ct)
    {
        if (payment.MpPaymentId is null)
        {
            return await ChargeAsync(payment, user, card, ct);
        }

        return payment.Status == PaymentStatus.Rejected ? throw Errors.PaymentFailed() : ToDto(payment);
    }

    private async Task<PaymentDto> ChargeAsync(Payment payment, User user, CardPaymentRequest? card, CancellationToken ct)
    {
        var description = payment.Quantity == 1 ? "RedigiAI: 1 correção avulsa" : $"RedigiAI: {payment.Quantity} correções avulsas";
        // O id da compra vai como chave de idempotência: repetir a criação lá devolve o mesmo pagamento.
        var request = new ChargeRequest(payment.Id, payment.AmountCents, description, user.Email);
        var created = payment.Method == PaymentMethod.Pix
            ? await gateway.CreatePixAsync(request, time.GetUtcNow() + Payment.PixValidity, ct)
            : await gateway.CreateCardAsync(request, CardTokenFrom(card), ct);

        var now = time.GetUtcNow();
        await unitOfWork.InTransactionAsync(
            async () =>
            {
                if (!await payments.TryAttachProviderAsync(
                    payment.Id, created.ProviderId, created.PixQrCode, created.PixQrCodeBase64, created.PixExpiresAt, now, ct))
                {
                    // Outra requisição com a mesma chave já gravou: nada a fazer.
                    return false;
                }

                if (created.Status == PaymentStatus.Rejected)
                {
                    // Recusa é a única situação tirada da resposta da criação: não concede nada.
                    await payments.TryTransitionAsync(payment.Id, PaymentStatus.Rejected, now, ct);
                    audit.Record("system", "payment.rejected", "payment", payment.Id);
                }
                else
                {
                    // A aprovação só vale depois da consulta ao Mercado Pago, feita pelo job (regra 19).
                    jobs.Enqueue(JobTypes.ProcessPayment, new ProcessPaymentPayload(created.ProviderId, EventId: null), ProcessPaymentHandler.Attempts);
                }

                return true;
            },
            ct);

        var current = await payments.FindAsync(payment.Id, ct) ?? throw Errors.Internal();
        return current.Status == PaymentStatus.Rejected ? throw Errors.PaymentFailed() : ToDto(current);
    }

    private static CardToken CardTokenFrom(CardPaymentRequest? card)
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
            : value.Length is > 0 and <= MaxCardFieldLength && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private PaymentDto ToDto(Payment payment) =>
        new(
            payment.Id,
            payment.Kind,
            payment.Method,
            payment.AmountCents,
            payment.Quantity,
            payment.Status,
            payment.CreatedAt,
            payment.ApprovedAt,
            payment.RefundableOn(time.GetUtcNow()),
            payment is { Status: PaymentStatus.Pending, PixQrCode: { } code, PixQrCodeBase64: { } image, PixExpiresAt: { } expires }
                ? new PixDto(image, code, expires)
                : null);
}
