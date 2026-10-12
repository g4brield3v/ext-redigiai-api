namespace RedigiAI.Application.Abstractions;

/// <summary>Operações do banco que não pertencem a nenhum agregado.</summary>
public interface IDatabaseAdmin
{
    /// <summary>Aplica as migrações pendentes.</summary>
    Task MigrateAsync(CancellationToken ct);

    Task<bool> CanConnectAsync(CancellationToken ct);
}
