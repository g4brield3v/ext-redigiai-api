using Microsoft.Extensions.Options;
using RedigiAI.Application.Abstractions;
using RedigiAI.Application.Essays;
using RedigiAI.Domain.Jobs;
using RedigiAI.Infrastructure.Jobs;

namespace RedigiAI.Api;

/// <summary>
/// Roda os jobs da fila. Cada tipo tem o seu número de execuções simultâneas
/// (<c>Jobs__Concurrency__&lt;tipo&gt;</c>, padrão 2), para respeitar os limites dos serviços externos.
/// </summary>
internal sealed class JobWorker(
    IServiceScopeFactory scopes,
    IJobRunner runner,
    JobSignal signal,
    IConfiguration configuration,
    IOptions<AiBudgetOptions> ai,
    ILogger<JobWorker> logger) : BackgroundService
{
    private const int DefaultConcurrency = 2;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        List<string> types;
        await using (var scope = scopes.CreateAsyncScope())
        {
            types = [.. scope.ServiceProvider.GetServices<IJobHandler>().Select(handler => handler.Type).Distinct()];
        }

        if (!ai.Value.Enabled && types.RemoveAll(type => type is JobTypes.Transcribe or JobTypes.Grade) > 0)
        {
            logger.LogWarning("AI is disabled (Ai__Enabled=false): transcribe and grade jobs stay in the queue");
        }

        if (types.Count == 0)
        {
            logger.LogInformation("Job worker started; no job handlers registered yet");
            return;
        }

        var loops = new List<Task> { signal.ListenAsync(stoppingToken) };
        foreach (var type in types)
        {
            var concurrency = Math.Max(1, configuration.GetValue($"Jobs:Concurrency:{type}", DefaultConcurrency));
            logger.LogInformation("Job worker running {Type} with concurrency {Concurrency}", type, concurrency);
            loops.AddRange(Enumerable.Range(0, concurrency).Select(_ => RunLoopAsync(type, stoppingToken)));
        }

        await Task.WhenAll(loops);
    }

    private async Task RunLoopAsync(string type, CancellationToken ct)
    {
        string[] types = [type];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await runner.RunNextAsync(types, ct))
                {
                    await signal.WaitAsync(PollInterval, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Banco fora do ar, por exemplo: espera e tenta de novo, sem derrubar o processo.
                logger.LogError(ex, "Job loop for {Type} failed; retrying", type);
                await Task.Delay(PollInterval, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }
}
