using RedigiAI.Application.Essays;

namespace RedigiAI.Api;

/// <summary>
/// A cada 15 minutos, cancela as redações cuja revisão de palavras ilegíveis passou do prazo de 24 h e
/// devolve o crédito de cada uma (especificação, seção 7, jobs agendados).
/// </summary>
internal sealed class ReviewDeadlineSweeper(IServiceScopeFactory scopes, ILogger<ReviewDeadlineSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var cancelled = await scope.ServiceProvider.GetRequiredService<IEssayService>().CancelExpiredReviewsAsync(stoppingToken);
                if (cancelled > 0)
                {
                    logger.LogInformation("Cancelled {Count} essays whose review deadline expired", cancelled);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Banco fora do ar, por exemplo: a próxima passada tenta de novo.
                logger.LogError(ex, "Review deadline sweep failed");
            }

            await Task.Delay(Interval, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}
