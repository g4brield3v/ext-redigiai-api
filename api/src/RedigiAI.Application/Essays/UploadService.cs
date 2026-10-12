using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Common;
using RedigiAI.Domain.Essays;

namespace RedigiAI.Application.Essays;

public interface IUploadService
{
    /// <summary>
    /// Reserva o arquivo e devolve a assinatura para o navegador enviar a foto direto ao provedor.
    /// O arquivo só é conferido (JPG, até 10 MB) e ligado a uma redação na confirmação do envio.
    /// </summary>
    Task<UploadSignatureDto> SignAsync(string clerkUserId, CancellationToken ct);
}

internal sealed class UploadService(
    IUserRepository users,
    IAssetRepository assets,
    IPhotoStorage storage,
    IUnitOfWork unitOfWork,
    TimeProvider time) : IUploadService
{
    public async Task<UploadSignatureDto> SignAsync(string clerkUserId, CancellationToken ct)
    {
        var user = await users.FindByClerkIdAsync(clerkUserId, ct) ?? throw Errors.ProfileNotFound();

        var now = time.GetUtcNow();
        var asset = Asset.Reserve(user.Id, now);
        var upload = storage.SignUpload(asset.PublicId, now);

        assets.Add(asset);
        await unitOfWork.SaveChangesAsync(ct);

        return new UploadSignatureDto(asset.Id, upload.UploadUrl, upload.Fields);
    }
}
