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
            // Vencimento e concessão são independentes: um erro em um não impede o outro.
            await RunAsync("expiry", credits => credits.ExpirePlanCreditsAsync(stoppingToken), stoppingToken);
            await RunAsync("weekly grant", credits => credits.GrantWeeklyPlanCreditsAsync(stoppingToken), stoppingToken);

            await Task.Delay(Interval, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task RunAsync(string step, Func<ICreditService, Task<CreditSweepResult>> sweep, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var result = await sweep(scope.ServiceProvider.GetRequiredService<ICreditService>());
            if (result.Done > 0)
            {
                logger.LogInformation("Credit calendar {Step}: {Count} done", step, result.Done);
            }

            if (result.Failed.Count > 0)
            {
                // Só os ids (assinatura ou bucket): nada de dado pessoal no log.
                logger.LogError("Credit calendar {Step} failed for {Ids}", step, string.Join(", ", result.Failed));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Banco fora do ar, por exemplo: a próxima passada tenta de novo.
            logger.LogError(ex, "Credit calendar {Step} failed", step);
        }
    }
}
