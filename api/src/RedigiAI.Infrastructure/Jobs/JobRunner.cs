using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Common;
using RedigiAI.Domain.Jobs;
using RedigiAI.Infrastructure.Persistence;

namespace RedigiAI.Infrastructure.Jobs;

/// <summary>
/// Fila sobre a tabela <c>jobs</c> (especificação, seção 7): pega um job com <c>FOR UPDATE SKIP LOCKED</c>,
/// trava por 10 minutos e decide entre concluir, tentar de novo com espera crescente ou encerrar.
/// </summary>
internal sealed class JobRunner(IServiceScopeFactory scopes, TimeProvider time, ILogger<JobRunner> logger) : IJobRunner
{
    public async Task<bool> RunNextAsync(IReadOnlyCollection<string> types, CancellationToken ct)
    {
        var job = await ClaimAsync(types, ct);
        if (job is null)
        {
            return false;
        }

        await using var scope = scopes.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetServices<IJobHandler>().First(h => h.Type == job.Type);

        // O worker anterior morreu na última tentativa permitida: não há mais o que tentar.
        if (job.Attempts > job.MaxAttempts)
        {
            await BuryAsync(job, handler, "LOCK_EXPIRED", ct);
            return true;
        }

        try
        {
            await handler.HandleAsync(job.Payload, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Worker desligando: o job fica travado e volta quando locked_until vencer.
            throw;
        }
        catch (Exception ex)
        {
            // Só o tipo ou o código do erro: mensagens podem carregar dados do aluno.
            var error = ex is AppError known ? known.Code : ex.GetType().Name;
            logger.LogWarning(ex, "Job {JobId} ({Type}) failed on attempt {Attempt}/{Max}", job.Id, job.Type, job.Attempts, job.MaxAttempts);

            if (ex is AppError { Retryable: false } || job.Attempts >= job.MaxAttempts)
            {
                await BuryAsync(job, handler, error, ct);
            }
            else
            {
                var runAt = time.GetUtcNow() + Job.BackoffAfter(job.Attempts);
                await FinishAsync(job.Id, JobStatus.Ready, runAt, error, ct);
            }

            return true;
        }

        await FinishAsync(job.Id, JobStatus.Done, runAt: null, error: null, ct);
        return true;
    }

    /// <summary>
    /// O handler de falha roda antes de o job ser marcado como morto: se ele falhar, o job continua travado e
    /// é retomado, em vez de ficar morto com o crédito por devolver.
    /// </summary>
    private async Task BuryAsync(ClaimedJob job, IJobHandler handler, string error, CancellationToken ct)
    {
        try
        {
            await handler.OnDeadAsync(job.Payload, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failure handler of job {JobId} ({Type}) failed; it will run again", job.Id, job.Type);
            return;
        }

        await FinishAsync(job.Id, JobStatus.Dead, runAt: null, error, ct);
        logger.LogError("Job {JobId} ({Type}) is dead: {Error}", job.Id, job.Type, error);
    }

    private async Task<ClaimedJob?> ClaimAsync(IReadOnlyCollection<string> types, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var connection = (NpgsqlConnection)scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.GetDbConnection();
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            UPDATE jobs
               SET status = 'running', attempts = attempts + 1, locked_until = @locked_until
             WHERE id = (
                   SELECT id
                     FROM jobs
                    WHERE type = ANY(@types)
                      AND ((status = 'ready' AND run_at <= @now) OR (status = 'running' AND locked_until < @now))
                    ORDER BY run_at
                      FOR UPDATE SKIP LOCKED
                    LIMIT 1)
            RETURNING id, type, payload::text, attempts, max_attempts
            """,
            connection);
        var now = time.GetUtcNow();
        command.Parameters.AddWithValue("types", types.ToArray());
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("locked_until", now + Job.LockDuration);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new ClaimedJob(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4))
            : null;
    }

    /// <summary>Só mexe no job que este worker ainda segura (<c>status = running</c>).</summary>
    private async Task FinishAsync(Guid jobId, JobStatus status, DateTimeOffset? runAt, string? error, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Jobs
            .Where(j => j.Id == jobId && j.Status == JobStatus.Running)
            .ExecuteUpdateAsync(
                job => job
                    .SetProperty(j => j.Status, status)
                    .SetProperty(j => j.LockedUntil, (DateTimeOffset?)null)
                    .SetProperty(j => j.RunAt, j => runAt ?? j.RunAt)
                    .SetProperty(j => j.LastError, error),
                ct);
    }

    private sealed record ClaimedJob(Guid Id, string Type, string Payload, int Attempts, int MaxAttempts);
}

/// <summary>Acorda os workers quando um job novo é gravado (<c>NOTIFY jobs</c>, disparado por gatilho no banco).</summary>
public sealed class JobSignal(IServiceScopeFactory scopes, ILogger<JobSignal> logger)
{
    public const string Channel = "jobs";

    private volatile TaskCompletionSource _pulse = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Espera um job novo ou o tempo de consulta periódica, o que vier primeiro.</summary>
    public async Task WaitAsync(TimeSpan pollInterval, CancellationToken ct)
    {
        try
        {
            await _pulse.Task.WaitAsync(pollInterval, ct);
        }
        catch (TimeoutException)
        {
            // Consulta periódica: cobre jobs agendados e avisos perdidos.
        }
    }

    /// <summary>Fica ouvindo o banco até o cancelamento. Se a conexão cair, a consulta periódica segura a fila.</summary>
    public async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string connectionString;
                await using (var scope = scopes.CreateAsyncScope())
                {
                    connectionString = scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.GetConnectionString()!;
                }

                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(ct);
                connection.Notification += (_, _) => Pulse();
                await using (var listen = new NpgsqlCommand($"LISTEN {Channel}", connection))
                {
                    await listen.ExecuteNonQueryAsync(ct);
                }

                while (!ct.IsCancellationRequested)
                {
                    await connection.WaitAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Job notification listener lost its connection; retrying");
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private void Pulse() =>
        Interlocked.Exchange(ref _pulse, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
}
