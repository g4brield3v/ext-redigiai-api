using RedigiAI.Application.Essays;

namespace RedigiAI.Api;

/// <summary>
/// A cada 15 minutos, agenda o lembrete das redações com 12 h sem preencher as palavras ilegíveis e cancela as
/// que passaram do prazo de 24 h, devolvendo o crédito de cada uma (especificação, seção 7, jobs agendados).
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
                var essays = scope.ServiceProvider.GetRequiredService<IEssayService>();
                var reminded = await essays.ScheduleReviewRemindersAsync(stoppingToken);
                if (reminded > 0)
                {
                    logger.LogInformation("Scheduled {Count} illegible-word reminders", reminded);
                }

                var cancelled = await essays.CancelExpiredReviewsAsync(stoppingToken);
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
