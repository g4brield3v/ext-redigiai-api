using Microsoft.EntityFrameworkCore;
using Npgsql;
using RedigiAI.Application.Abstractions;

namespace RedigiAI.Infrastructure.Persistence.Repositories;

internal sealed class UnitOfWork(AppDbContext db) : IUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Nada foi gravado; solta o que ficou pendente para o caso de uso reler o estado real.
            db.ChangeTracker.Clear();
            throw new UniqueConflictException(ex);
        }
    }
}

internal sealed class DatabaseAdmin(AppDbContext db) : IDatabaseAdmin
{
    public Task MigrateAsync(CancellationToken ct) => db.Database.MigrateAsync(ct);

    public Task<bool> CanConnectAsync(CancellationToken ct) => db.Database.CanConnectAsync(ct);
}
