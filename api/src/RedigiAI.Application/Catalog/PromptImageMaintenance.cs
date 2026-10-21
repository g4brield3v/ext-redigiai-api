using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Essays;

namespace RedigiAI.Application.Catalog;

public interface IPromptImageMaintenance
{
    /// <summary>Torna privadas as imagens de proposta enviadas como públicas (antes de 20/10/2026). Pode rodar de novo.</summary>
    /// <returns>Quantas mudaram, de quantas existem.</returns>
    Task<(int Changed, int Total)> MakeAllPrivateAsync(CancellationToken ct);
}

internal sealed class PromptImageMaintenance(IAssetRepository assets, IPromptImageStorage images) : IPromptImageMaintenance
{
    public async Task<(int Changed, int Total)> MakeAllPrivateAsync(CancellationToken ct)
    {
        var publicIds = await assets.AllPromptImagePublicIdsAsync(ct);
        var changed = 0;
        foreach (var publicId in publicIds)
        {
            if (await images.MakePrivateAsync(publicId, ct))
            {
                changed++;
            }
        }

        return (changed, publicIds.Count);
    }
}
