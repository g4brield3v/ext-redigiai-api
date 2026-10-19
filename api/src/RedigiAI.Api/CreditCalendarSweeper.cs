using RedigiAI.Application.Billing;

namespace RedigiAI.Api;

/// <summary>
/// A cada 5 minutos, faz o calendário do plano semanal (especificação, seção 7, jobs agendados, horário de Brasília):
/// as 3 correções de segunda-feira 00h e o vencimento do dia 1º 00h. A primeira passada depois da hora faz o trabalho;
/// as seguintes não repetem nada.
/// </summary>
internal sealed class CreditCalendarSweeper(IServiceScopeFactory scopes, ILogger<CreditCalendarSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var credits = scope.ServiceProvider.GetRequiredService<ICreditService>();
                var expired = await credits.ExpirePlanCreditsAsync(stoppingToken);
                if (expired > 0)
                {
                    logger.LogInformation("Expired {Count} plan credit buckets", expired);
                }

                var granted = await credits.GrantWeeklyPlanCreditsAsync(stoppingToken);
                if (granted > 0)
                {
                    logger.LogInformation("Granted the weekly plan credits to {Count} subscriptions", granted);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Banco fora do ar, por exemplo: a próxima passada tenta de novo.
                logger.LogError(ex, "Credit calendar sweep failed");
            }

            await Task.Delay(Interval, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}
