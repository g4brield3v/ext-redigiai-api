using RedigiAI.Application.Abstractions;
using RedigiAI.Application.Catalog;

namespace RedigiAI.Api;

/// <summary>
/// Só no ambiente <c>Development</c>: ao subir a API, aplica as migrações e carrega o catálogo no banco local. Não cria
/// usuário nenhum: o acesso é sempre pelo login do Clerk.
/// </summary>
internal sealed class LocalDatabaseSetup(IServiceScopeFactory scopes) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseAdmin>().MigrateAsync(ct);
        await scope.ServiceProvider.GetRequiredService<ICatalogSeedService>().SeedAsync(ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
