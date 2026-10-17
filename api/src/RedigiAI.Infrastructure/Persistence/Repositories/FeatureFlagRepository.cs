using Microsoft.EntityFrameworkCore;
using RedigiAI.Domain.Common;

namespace RedigiAI.Infrastructure.Persistence.Repositories;

internal sealed class FeatureFlagRepository(AppDbContext db) : IFeatureFlagRepository
{
    public Task<bool> IsEnabledAsync(string key, CancellationToken ct) =>
        db.FeatureFlags.AsNoTracking().AnyAsync(flag => flag.Key == key && flag.Enabled, ct);
}
