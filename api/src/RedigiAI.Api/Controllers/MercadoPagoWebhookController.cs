using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RedigiAI.Api.Http;
using RedigiAI.Application.Billing;
using RedigiAI.Domain.Common;

namespace RedigiAI.Api.Controllers;

/// <summary>Notificações do Mercado Pago. Sem token: a autenticação é a assinatura <c>x-signature</c>.</summary>
[ApiController]
[AllowAnonymous]
[Route("v1/webhooks/mercadopago")]
[Tags("Webhooks")]
public sealed class MercadoPagoWebhookController(IPaymentWebhookService webhooks) : ControllerBase
{
    private const int MaxBodyBytes = 64 * 1024;

    /// <summary>
    /// Recebe uma notificação (cabeçalhos <c>x-signature</c> e <c>x-request-id</c>; consulta <c>data.id</c> e
    /// <c>type</c>). Grava e responde 200 em seguida; o pagamento é consultado na API do Mercado Pago depois, na fila.
    /// </summary>
    [HttpPost(Name = "receiveMercadoPagoWebhook")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        var body = await ReadBodyAsync(Request, ct) ?? throw Errors.Unauthenticated();
        await webhooks.ReceiveMercadoPagoAsync(
            new MercadoPagoNotification(
                Request.Headers["x-signature"],
                Request.Headers["x-request-id"],
                Request.Query["data.id"],
                Request.Query["type"],
                body),
            ct);
        return Ok();
    }

    /// <returns><c>null</c> se o corpo passa do limite.</returns>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
