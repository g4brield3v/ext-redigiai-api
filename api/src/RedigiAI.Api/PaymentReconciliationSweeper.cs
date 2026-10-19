using RedigiAI.Application.Billing;

namespace RedigiAI.Api;

/// <summary>
/// A cada 5 minutos, põe na fila a consulta das compras que continuam pendentes: o Pix pago cujo aviso se perdeu, ou
/// cujo job morreu com o Mercado Pago fora do ar, recebe as correções mesmo assim.
/// </summary>
internal sealed class PaymentReconciliationSweeper(IServiceScopeFactory scopes, ILogger<PaymentReconciliationSweeper> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var queued = await scope.ServiceProvider.GetRequiredService<IPaymentService>().RecheckPendingAsync(stoppingToken);
                if (queued > 0)
                {
                    logger.LogInformation("Queued {Count} pending payment checks", queued);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Banco fora do ar, por exemplo: a próxima passada tenta de novo.
                logger.LogError(ex, "Payment reconciliation failed");
            }

            await Task.Delay(Interval, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}
