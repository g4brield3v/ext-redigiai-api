using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Catalog;
using RedigiAI.Domain.Common;
using RedigiAI.Domain.Essays;
using RedigiAI.Domain.Grading;

namespace RedigiAI.Application.Catalog;

/// <summary>
/// Uma proposta do pacote de conteúdo: o JSON do cadastro (docs/07-propostas-de-redacao.md, B4) e os arquivos de imagem
/// que ele cita. No lugar de <c>assetId</c>, o bloco de imagem traz <c>imageFile</c>, o nome do arquivo no pacote.
/// </summary>
/// <param name="Name">Nome do pacote (a pasta), para o relatório.</param>
public sealed record PromptPackage(string Name, string Json, IReadOnlyDictionary<string, byte[]> Files);

public enum PromptImportOutcome
{
    /// <summary>Publicada agora.</summary>
    Created,

    /// <summary>Já existe uma proposta com esse endereço: nada foi mudado.</summary>
    AlreadyPresent,

    /// <summary>Não foi publicada; <see cref="PromptImportResult.Problems"/> diz por quê.</summary>
    Failed,
}

public sealed record PromptImportResult(string Package, string? Slug, PromptImportOutcome Outcome, IReadOnlyList<string> Problems);

public interface IPromptImportService
{
    /// <summary>
    /// Publica as propostas do pacote cujo endereço ainda não existe, enviando as imagens antes. Pode rodar de novo:
    /// proposta que já existe fica como está (mudar é criar versão nova pelo admin), e imagem já enviada é reaproveitada.
    /// </summary>
    /// <param name="adminClerkUserId">Quem importa: precisa ser administrador. Fica como autor das propostas e das imagens.</param>
    Task<IReadOnlyList<PromptImportResult>> ImportAsync(
        string adminClerkUserId, IReadOnlyList<PromptPackage> packages, CancellationToken ct);
}

internal sealed class PromptImportService(
    IUserRepository users,
    IPromptRepository prompts,
    IExamRepository exams,
    IEssayTypeRepository essayTypes,
    IGradingProfileRepository profiles,
    IAssetRepository assets,
    IPromptImageStorage images,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    TimeProvider time) : IPromptImportService
{
    private const string ImageFileField = "imageFile";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<PromptImportResult>> ImportAsync(
        string adminClerkUserId, IReadOnlyList<PromptPackage> packages, CancellationToken ct)
    {
        // O papel é conferido no banco, como nas rotas do admin.
        if (await users.FindByClerkIdAsync(adminClerkUserId, ct) is not { IsAdmin: true } admin)
        {
            throw new InvalidOperationException("The importing account must exist and be an administrator.");
        }

        var results = new List<PromptImportResult>();
        foreach (var package in packages.OrderBy(package => package.Name, StringComparer.Ordinal))
        {
            try
            {
                results.Add(await ImportOneAsync(admin.Id, package, ct));
            }
            catch (AppError error)
            {
                var problems = error.Details is { Count: > 0 } details
                    ? details.Select(issue => $"{issue.Path}: {issue.Message}").ToList()
                    : [error.UserMessage];
                results.Add(new PromptImportResult(package.Name, null, PromptImportOutcome.Failed, problems));
            }
            catch (Exception error) when (error is HttpRequestException or InvalidOperationException or JsonException or UniqueConflictException)
            {
                // Falha do Cloudinary, JSON com tipo errado ou corrida no registro da imagem: os outros pacotes seguem.
                results.Add(new PromptImportResult(package.Name, null, PromptImportOutcome.Failed, [$"{error.GetType().Name}: {error.Message}"]));
            }
        }

        return results;
    }

    private async Task<PromptImportResult> ImportOneAsync(Guid adminId, PromptPackage package, CancellationToken ct)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(package.Json);
        }
        catch (JsonException)
        {
            throw Errors.Validation("O prompt.json não é um JSON válido.");
        }

        if (root is not JsonObject)
        {
            throw Errors.Validation("O prompt.json precisa ser um objeto.");
        }

        var slug = root["slug"]?.GetValue<string>().Trim();
        if (string.IsNullOrEmpty(slug))
        {
            throw Errors.ValidationFailed([new ValidationIssue("slug", "Informe o endereço da proposta.")]);
        }

        // Antes de qualquer envio: o endereço entra no identificador das imagens no Cloudinary.
        if (slug.Length > PromptRules.MaxSlugLength || !PromptRules.IsValidSlug(slug))
        {
            throw Errors.ValidationFailed([new ValidationIssue("slug", "Endereço: só letras minúsculas, números e hífen.")]);
        }

        if (await prompts.MaxVersionAsync(slug, ct) > 0)
        {
            return new PromptImportResult(package.Name, slug, PromptImportOutcome.AlreadyPresent, []);
        }

        // As imagens vão antes da proposta: ela só é publicada com todas as imagens registradas.
        await ReplaceImageFilesAsync(adminId, slug, root, package, ct);
        var document = root.Deserialize<PromptDocument>(Json) ?? throw Errors.Validation("O prompt.json está vazio.");
        var exam = await exams.FindBySlugAsync(document.Exam?.Trim() ?? "", ct)
            ?? throw Errors.ValidationFailed([new ValidationIssue("exam", "Vestibular desconhecido.")]);

        var now = time.GetUtcNow();
        var content = await PromptAdminService.ToContentAsync(essayTypes, document, exam, slug, ct);
        var prompt = Prompt.Draft(content, adminId, now);
        var imageIds = await assets.PromptImagePublicIdsAsync([.. prompt.Blocks.SelectMany(block => block.Data.AssetIds()).Distinct()], ct);
        var hasProfile = prompt.EssayTypeId is { } typeId && await profiles.FindActiveAsync(exam.Id, typeId, ct) is not null;
        prompt.Publish(new PublicationContext(hasProfile, imageIds.Keys.ToHashSet()), adminId, now);
        prompts.Add(prompt);
        audit.Record($"user:{adminId}", "prompt.imported", "prompt", prompt.Id, new { prompt.Slug, prompt.Version, package = package.Name });

        try
        {
            await unitOfWork.SaveChangesAsync(ct);
        }
        catch (UniqueConflictException)
        {
            // Outra importação publicou o mesmo endereço ao mesmo tempo.
            return new PromptImportResult(package.Name, slug, PromptImportOutcome.AlreadyPresent, []);
        }

        return new PromptImportResult(package.Name, slug, PromptImportOutcome.Created, []);
    }

    /// <summary>Troca cada <c>imageFile</c> pelo <c>assetId</c> da imagem enviada.</summary>
    private async Task ReplaceImageFilesAsync(Guid adminId, string slug, JsonNode node, PromptPackage package, CancellationToken ct)
    {
        switch (node)
        {
            case JsonObject item:
                if (item[ImageFileField]?.GetValue<string>() is { } fileName)
                {
                    item.Remove(ImageFileField);
                    item["assetId"] = (await UploadAsync(adminId, slug, fileName, package, ct)).ToString();
                }

                foreach (var child in item.Select(pair => pair.Value).OfType<JsonNode>().ToList())
                {
                    await ReplaceImageFilesAsync(adminId, slug, child, package, ct);
                }

                break;
            case JsonArray list:
                foreach (var child in list.OfType<JsonNode>().ToList())
                {
                    await ReplaceImageFilesAsync(adminId, slug, child, package, ct);
                }

                break;
        }
    }

    /// <summary>
    /// O identificador no provedor sai do conteúdo do arquivo: rodar de novo encontra a mesma imagem, no banco ou no
    /// provedor (que não sobrescreve), em vez de enviar outra.
    /// </summary>
    private async Task<Guid> UploadAsync(Guid adminId, string slug, string fileName, PromptPackage package, CancellationToken ct)
    {
        if (!package.Files.TryGetValue(fileName, out var content))
        {
            throw Errors.Validation($"A imagem {fileName} não está no pacote.");
        }

        if (content.Length is < 1 or > (int)PromptAdminService.MaxImageBytes || ImageFormat.Detect(content) is not { } mime)
        {
            throw Errors.Validation($"A imagem {fileName} precisa ser JPG, PNG ou WebP, com até 5 MB.");
        }

        var publicId = $"prompts/import/{slug}/{Convert.ToHexStringLower(SHA256.HashData(content))[..24]}";
        if (await assets.FindPromptImageIdAsync(publicId, ct) is { } existing)
        {
            return existing;
        }

        var stored = await images.UploadAsync(publicId, content, mime, ct);
        var id = Guid.CreateVersion7();
        assets.Add(Asset.ForPromptImage(id, adminId, publicId, stored.Bytes, mime, time.GetUtcNow()));
        audit.Record($"user:{adminId}", "asset.uploaded", "asset", id, new { scope = "prompt_image", stored.Bytes, file = fileName });
        await unitOfWork.SaveChangesAsync(ct);
        return id;
    }
}
