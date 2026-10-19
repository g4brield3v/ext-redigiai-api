using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RedigiAI.Api.Auth;
using RedigiAI.Api.Http;
using RedigiAI.Application.Accounts;
using RedigiAI.Application.Billing;

namespace RedigiAI.Api.Controllers;

/// <summary>Compras do aluno logado no Mercado Pago (especificação, seção 9).</summary>
[ApiController]
[Authorize]
[Route("v1")]
[Tags("Payments")]
[Produces("application/json")]
[ProducesResponseType<ErrorResponse>(StatusCodes.Status401Unauthorized)]
public sealed class PaymentsController(IPaymentService payments, ISubscriptionService subscriptions) : ControllerBase
{
    /// <summary>
    /// Compra correções avulsas (R$ 16 cada, de 1 a 10) por Pix ou cartão. Exige <c>Idempotency-Key</c>; repetir a
    /// chave devolve a mesma compra. O Pix volta com o QR code, válido por 30 minutos. Os créditos entram quando o
    /// pagamento é confirmado: consulte <c>GET /v1/payments/{id}</c>.
    /// </summary>
    [HttpPost("checkout/single", Name = "checkoutSingle")]
    [ProducesResponseType<PaymentDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status402PaymentRequired)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CheckoutSingle(
        [FromBody] CheckoutSingleRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        var payment = await payments.CheckoutSingleAsync(
            new CheckoutSingleCommand(
                User.GetClerkIdentity().UserId,
                idempotencyKey,
                request.Quantity,
                request.Method,
                request.GuardianAuthorized == true,
                request.Card),
            ct);
        return CreatedAtRoute("getPayment", new { id = payment.Id }, payment);
    }

    /// <summary>
    /// Assina o plano mensal (R$ 39,90 no cartão, 12 correções por cobrança): devolve o checkout do Mercado Pago, onde o
    /// aluno autoriza o cartão. Exige <c>Idempotency-Key</c>; repetir a chave devolve o mesmo checkout. A assinatura
    /// aparece em <c>GET /v1/me</c> quando o Mercado Pago confirma.
    /// </summary>
    [HttpPost("checkout/plan", Name = "checkoutPlan")]
    [ProducesResponseType<PlanCheckoutDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CheckoutPlan(
        [FromBody] CheckoutPlanRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct) =>
        Ok(await subscriptions.CheckoutPlanAsync(
            new CheckoutPlanCommand(User.GetClerkIdentity().UserId, idempotencyKey, request.GuardianAuthorized == true), ct));

    /// <summary>
    /// Cancela a renovação do plano mensal. As correções já concedidas valem até o fim do ciclo pago. Sem plano ativo
    /// ou pausado, 404.
    /// </summary>
    [HttpPost("subscription/cancel", Name = "cancelSubscription")]
    [ProducesResponseType<SubscriptionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CancelSubscription(CancellationToken ct) =>
        Ok(await subscriptions.CancelAsync(User.GetClerkIdentity().UserId, ct));

    /// <summary>As compras do aluno, da mais recente para a mais antiga.</summary>
    [HttpGet("payments", Name = "listPayments")]
    [ProducesResponseType<PaymentListDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(await payments.ListAsync(User.GetClerkIdentity().UserId, ct));

    /// <summary>Uma compra do aluno, com a situação atual. A tela do Pix consulta a cada 3 segundos.</summary>
    [HttpGet("payments/{id:guid}", Name = "getPayment")]
    [ProducesResponseType<PaymentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        Ok(await payments.GetAsync(User.GetClerkIdentity().UserId, id, ct));
}
