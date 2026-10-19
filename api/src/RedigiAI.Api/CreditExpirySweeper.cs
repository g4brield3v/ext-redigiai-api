using RedigiAI.Application.Billing;

namespace RedigiAI.Api;

/// <summary>
/// A cada 5 minutos, vence as correções que sobraram nos buckets vencidos: as do plano mensal vencem na data da
/// cobrança seguinte (especificação, seção 7, jobs agendados). A primeira passada depois da hora faz o trabalho; as
/// seguintes não repetem nada.
/// </summary>
internal sealed class CreditExpirySweeper(IServiceScopeFactory scopes, ILogger<CreditExpirySweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<ICreditService>().ExpirePlanCreditsAsync(stoppingToken);
                if (result.Done > 0)
                {
                    logger.LogInformation("Expired {Count} credit buckets", result.Done);
                }

                if (result.Failed.Count > 0)
                {
                    // Só os ids dos buckets: nada de dado pessoal no log.
                    logger.LogError("Credit expiry failed for {Ids}", string.Join(", ", result.Failed));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Banco fora do ar, por exemplo: a próxima passada tenta de novo.
                logger.LogError(ex, "Credit expiry sweep failed");
            }

            await Task.Delay(Interval, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}
