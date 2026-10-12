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
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Nada foi gravado; solta o que ficou pendente para o caso de uso reler o estado real.
            db.ChangeTracker.Clear();
            throw new UniqueConflictException(ex);
        }
    }

    public async Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var result = await work();
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return result;
        }
        catch (Exception ex)
        {
            // Desfaz também as escritas diretas (débito de crédito) e solta o que ficou pendente.
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            if (ex is DbUpdateException update && IsUniqueViolation(update))
            {
                throw new UniqueConflictException(ex);
            }

            throw;
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}

internal sealed class DatabaseAdmin(AppDbContext db) : IDatabaseAdmin
{
    public Task MigrateAsync(CancellationToken ct) => db.Database.MigrateAsync(ct);

    public Task<bool> CanConnectAsync(CancellationToken ct) => db.Database.CanConnectAsync(ct);
}
