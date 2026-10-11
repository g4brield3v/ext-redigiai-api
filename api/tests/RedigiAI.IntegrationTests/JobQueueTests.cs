using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Common;
using RedigiAI.Domain.Jobs;
using RedigiAI.Infrastructure.Jobs;
using RedigiAI.Infrastructure.Persistence;

namespace RedigiAI.IntegrationTests;

[Collection(ApiCollection.Name)]
public class JobQueueTests(ApiFixture api)
{
    [Fact]
    public async Task Job_runs_once_and_is_done()
    {
        await using var queue = await Queue.CreateAsync(api);
        var jobId = await queue.EnqueueAsync();

        Assert.True(await queue.RunNextAsync());
        Assert.False(await queue.RunNextAsync());

        var job = await queue.FindAsync(jobId);
        Assert.Equal((JobStatus.Done, 1, (DateTimeOffset?)null, (string?)null), (job.Status, job.Attempts, job.LockedUntil, job.LastError));
        Assert.Equal(1, queue.Handler.Handled);
        Assert.Equal(0, queue.Handler.Buried);
    }

    [Fact]
    public async Task Failed_job_waits_longer_each_time_and_dies_after_the_last_attempt()
    {
        await using var queue = await Queue.CreateAsync(api);
        queue.Handler.Failure = () => new InvalidOperationException("redação do aluno: não pode ir para o banco");
        var jobId = await queue.EnqueueAsync(maxAttempts: 4);

        foreach (var (attempt, wait) in new[] { (1, 30), (2, 120), (3, 480) })
        {
            Assert.True(await queue.RunNextAsync());

            var waiting = await queue.FindAsync(jobId);
            Assert.Equal((JobStatus.Ready, attempt, "InvalidOperationException"), (waiting.Status, waiting.Attempts, waiting.LastError));
            Assert.Equal(queue.Clock.Now.AddSeconds(wait), waiting.RunAt);

            // Antes da hora, ninguém pega.
            queue.Clock.Now = queue.Clock.Now.AddSeconds(wait - 1);
            Assert.False(await queue.RunNextAsync());
            queue.Clock.Now = queue.Clock.Now.AddSeconds(1);
        }

        Assert.True(await queue.RunNextAsync());

        var dead = await queue.FindAsync(jobId);
        Assert.Equal((JobStatus.Dead, 4), (dead.Status, dead.Attempts));
        Assert.Equal((4, 1), (queue.Handler.Handled, queue.Handler.Buried));
        Assert.False(await queue.RunNextAsync());
    }

    [Fact]
    public async Task Definitive_error_kills_the_job_on_the_first_attempt()
    {
        await using var queue = await Queue.CreateAsync(api);
        queue.Handler.Failure = Errors.InvalidFile;
        var jobId = await queue.EnqueueAsync(maxAttempts: 4);

        Assert.True(await queue.RunNextAsync());

        var job = await queue.FindAsync(jobId);
        Assert.Equal((JobStatus.Dead, 1, "INVALID_FILE"), (job.Status, job.Attempts, job.LastError));
        Assert.Equal((1, 1), (queue.Handler.Handled, queue.Handler.Buried));
    }

    [Fact]
    public async Task Job_of_a_worker_that_died_is_resumed_after_the_lock_expires()
    {
        await using var queue = await Queue.CreateAsync(api);
        var jobId = await queue.EnqueueAsync();

        // O worker pega o job e é desligado no meio.
        using var shutdown = new CancellationTokenSource();
        queue.Handler.OnHandle = () => shutdown.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.RunNextAsync(shutdown.Token));
        queue.Handler.OnHandle = null;

        var locked = await queue.FindAsync(jobId);
        Assert.Equal((JobStatus.Running, 1), (locked.Status, locked.Attempts));
        Assert.Equal(queue.Clock.Now.AddMinutes(10), locked.LockedUntil);

        queue.Clock.Now = queue.Clock.Now.AddMinutes(10);
        Assert.False(await queue.RunNextAsync());

        queue.Clock.Now = queue.Clock.Now.AddSeconds(1);
        Assert.True(await queue.RunNextAsync());

        var done = await queue.FindAsync(jobId);
        Assert.Equal((JobStatus.Done, 2), (done.Status, done.Attempts));
    }

    [Fact]
    public async Task Worker_that_died_on_the_last_attempt_leaves_a_dead_job_with_the_failure_handled()
    {
        await using var queue = await Queue.CreateAsync(api);
        var jobId = await queue.EnqueueAsync(maxAttempts: 1);

        using var shutdown = new CancellationTokenSource();
        queue.Handler.OnHandle = () => shutdown.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.RunNextAsync(shutdown.Token));
        queue.Handler.OnHandle = null;
        queue.Clock.Now = queue.Clock.Now.AddMinutes(11);

        Assert.True(await queue.RunNextAsync());

        var job = await queue.FindAsync(jobId);
        Assert.Equal((JobStatus.Dead, "LOCK_EXPIRED"), (job.Status, job.LastError));
        Assert.Equal((1, 1), (queue.Handler.Handled, queue.Handler.Buried));
    }

    [Fact]
    public async Task Failure_handler_that_fails_keeps_the_job_to_run_again()
    {
        await using var queue = await Queue.CreateAsync(api);
        queue.Handler.Failure = Errors.InvalidFile;
        queue.Handler.BuryFailure = () => new TimeoutException();
        var jobId = await queue.EnqueueAsync();

        Assert.True(await queue.RunNextAsync());
        Assert.Equal(JobStatus.Running, (await queue.FindAsync(jobId)).Status);

        queue.Handler.BuryFailure = null;
        queue.Clock.Now = queue.Clock.Now.AddMinutes(11);
        Assert.True(await queue.RunNextAsync());

        Assert.Equal(JobStatus.Dead, (await queue.FindAsync(jobId)).Status);
        Assert.Equal(2, queue.Handler.Buried);
    }

    [Fact]
    public async Task Two_workers_never_run_the_same_job()
    {
        await using var queue = await Queue.CreateAsync(api);
        queue.Handler.Delay = TimeSpan.FromMilliseconds(300);
        await queue.EnqueueAsync();

        var ran = await Task.WhenAll(queue.RunNextAsync(), queue.RunNextAsync(), queue.RunNextAsync());

        Assert.Single(ran, picked => picked);
        Assert.Equal(1, queue.Handler.Handled);
    }

    [Fact]
    public async Task Inserting_a_job_wakes_the_listening_workers()
    {
        await using var queue = await Queue.CreateAsync(api);
        var signal = queue.Factory.Services.GetRequiredService<JobSignal>();
        using var stop = new CancellationTokenSource();
        var listening = signal.ListenAsync(stop.Token);
        await Task.Delay(500);

        var waiting = signal.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        await queue.EnqueueAsync();
        var first = await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(waiting, first);
        await stop.CancelAsync();
        await listening;
    }

    private sealed class TestClock : TimeProvider
    {
        // O PostgreSQL guarda microssegundos: o relógio do teste não usa frações menores.
        public DateTimeOffset Now { get; set; } = new(2026, 10, 11, 15, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class TestHandler(string type) : IJobHandler
    {
        private int _handled;
        private int _buried;

        public string Type { get; } = type;

        public int Handled => _handled;

        public int Buried => _buried;

        public Func<Exception>? Failure { get; set; }

        public Func<Exception>? BuryFailure { get; set; }

        public Action? OnHandle { get; set; }

        public TimeSpan Delay { get; set; }

        public async Task HandleAsync(string payload, CancellationToken ct)
        {
            Interlocked.Increment(ref _handled);
            OnHandle?.Invoke();
            ct.ThrowIfCancellationRequested();
            await Task.Delay(Delay, ct);
            if (Failure is not null)
            {
                throw Failure();
            }
        }

        public Task OnDeadAsync(string payload, CancellationToken ct)
        {
            Interlocked.Increment(ref _buried);
            return BuryFailure is null ? Task.CompletedTask : Task.FromException(BuryFailure());
        }
    }

    /// <summary>Uma fila só do teste: tipo de job próprio, relógio controlado e um handler que o teste comanda.</summary>
    private sealed class Queue : IAsyncDisposable
    {
        private Queue(WebApplicationFactory<Program> factory, TestClock clock, TestHandler handler)
        {
            Factory = factory;
            Clock = clock;
            Handler = handler;
        }

        public WebApplicationFactory<Program> Factory { get; }

        public TestClock Clock { get; }

        public TestHandler Handler { get; }

        public static Task<Queue> CreateAsync(ApiFixture api)
        {
            var clock = new TestClock();
            var handler = new TestHandler($"t-{Guid.NewGuid():N}");
            var factory = api.CreateFactory(builder => builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<IJobHandler>(handler);
            }));

            // Sobe o host.
            _ = factory.Services;
            return Task.FromResult(new Queue(factory, clock, handler));
        }

        public async Task<Guid> EnqueueAsync(int maxAttempts = 4)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var marker = Guid.NewGuid();
            scope.ServiceProvider.GetRequiredService<IJobQueue>().Enqueue(Handler.Type, new { marker }, maxAttempts);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(CancellationToken.None);
            return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Jobs
                .Where(j => j.Type == Handler.Type).OrderByDescending(j => j.Id).Select(j => j.Id).FirstAsync();
        }

        public Task<bool> RunNextAsync(CancellationToken ct = default) =>
            Factory.Services.GetRequiredService<IJobRunner>().RunNextAsync([Handler.Type], ct);

        public async Task<Job> FindAsync(Guid jobId)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        }

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            NpgsqlConnection.ClearAllPools();
        }
    }
}
