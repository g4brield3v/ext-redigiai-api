using System.Globalization;
using System.Text;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Catalog;
using RedigiAI.Domain.Common;
using RedigiAI.Domain.Essays;
using RedigiAI.Domain.Grading;

namespace RedigiAI.Application.Catalog;

/// <summary>
/// Área de administração das propostas de redação (docs/07-propostas-de-redacao.md, A5). Quem chama já conferiu que
/// o usuário é administrador; <c>adminId</c> é o id dele, para o registro de quem alterou o quê.
/// </summary>
public interface IPromptAdminService
{
    /// <summary>Cria um rascunho, vazio ou a partir do JSON completo. Repetir a chave devolve o mesmo rascunho.</summary>
    Task<CreatedPrompt> CreateAsync(Guid adminId, string? idempotencyKey, PromptDocument? document, CancellationToken ct);

    /// <param name="status"><c>draft</c>, <c>published</c> ou <c>archived</c>; ausente, todas.</param>
    Task<PromptListDto> ListAsync(string? status, string? examSlug, string? search, CancellationToken ct);

    Task<PromptAdminDto> GetAsync(Guid promptId, CancellationToken ct);

    /// <summary>Substitui o rascunho inteiro.</summary>
    Task<PromptSavedDto> SaveAsync(Guid adminId, Guid promptId, PromptDocument document, CancellationToken ct);

    Task<PromptSavedDto> ReorderAsync(Guid adminId, Guid promptId, IReadOnlyList<BlockPosition>? order, CancellationToken ct);

    /// <summary>A validação de publicação, sem publicar.</summary>
    Task<PromptValidationDto> ValidateAsync(Guid promptId, CancellationToken ct);

    /// <summary>Valida e publica; a versão publicada anterior do mesmo endereço é arquivada.</summary>
    Task<PromptSavedDto> PublishAsync(Guid adminId, Guid promptId, CancellationToken ct);

    /// <summary>Rascunho da versão seguinte, a partir de uma versão publicada ou arquivada.</summary>
    Task<PromptSavedDto> NewVersionAsync(Guid adminId, Guid promptId, CancellationToken ct);

    Task<PromptSavedDto> ArchiveAsync(Guid adminId, Guid promptId, CancellationToken ct);

    /// <summary>Apaga só rascunho.</summary>
    Task DeleteAsync(Guid adminId, Guid promptId, CancellationToken ct);

    /// <summary>Guarda uma imagem de proposta (JPG, PNG ou WebP, até 5 MB) na pasta pública.</summary>
    Task<PromptImageDto> UploadImageAsync(Guid adminId, PromptImageUpload upload, CancellationToken ct);

    /// <summary>As categorias de correção do vestibular, para a lista do cartão Instruções.</summary>
    Task<IReadOnlyList<EssayTypeOptionDto>> ListEssayTypesAsync(string examSlug, CancellationToken ct);
}

internal sealed class PromptAdminService(
    IPromptRepository prompts,
    IExamRepository exams,
    IEssayTypeRepository essayTypes,
    IGradingProfileRepository profiles,
    IAssetRepository assets,
    IPromptImageStorage images,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    TimeProvider time) : IPromptAdminService
{
    public const long MaxImageBytes = 5 * 1024 * 1024;
    private const int MaxSearchLength = 80;

    private static readonly Dictionary<string, PromptStatus> Statuses = new(StringComparer.Ordinal)
    {
        ["draft"] = PromptStatus.Draft,
        ["published"] = PromptStatus.Published,
        ["archived"] = PromptStatus.Archived,
    };

    public async Task<CreatedPrompt> CreateAsync(Guid adminId, string? idempotencyKey, PromptDocument? document, CancellationToken ct)
    {
        var key = idempotencyKey?.Trim();
        if (string.IsNullOrEmpty(key) || key.Length > Prompt.MaxCreationKeyLength || key.Any(char.IsControl))
        {
            throw Errors.Validation("Não foi possível criar a proposta. Atualize a página e tente de novo.");
        }

        if (await prompts.FindByCreationKeyAsync(adminId, key, ct) is { } existing)
        {
            return new CreatedPrompt(await ToSavedAsync(existing, ct), Created: false);
        }

        document ??= new PromptDocument();
        var exam = await ResolveExamAsync(document.Exam, fallback: null, ct);

        // Endereço informado vale como veio; sem ele, sai do título, com um sufixo se já estiver em uso.
        var slug = document.Slug?.Trim();
        var slugWasGiven = !string.IsNullOrEmpty(slug);
        if (!slugWasGiven)
        {
            slug = Slugify(document.Title);
        }

        if (await prompts.MaxVersionAsync(slug!, ct) > 0)
        {
            if (slugWasGiven)
            {
                throw SlugTaken();
            }

            slug = $"{slug}-{Guid.NewGuid():N}"[..Math.Min(slug!.Length + 7, PromptRules.MaxSlugLength)];
        }

        var content = await ToContentAsync(document, exam, slug!, ct);
        var prompt = Prompt.Draft(content, adminId, Now(), key);
        prompts.Add(prompt);
        audit.Record(Actor(adminId), "prompt.created", "prompt", prompt.Id, new { prompt.Slug, prompt.Version });

        try
        {
            await unitOfWork.SaveChangesAsync(ct);
        }
        catch (UniqueConflictException)
        {
            // O mesmo pedido chegou duas vezes ao mesmo tempo, ou outro rascunho ficou com o endereço.
            var winner = await prompts.FindByCreationKeyAsync(adminId, key, ct) ?? throw SlugTaken();
            return new CreatedPrompt(await ToSavedAsync(winner, ct), Created: false);
        }

        return new CreatedPrompt(await ToSavedAsync(prompt, ct), Created: true);
    }

    public async Task<PromptListDto> ListAsync(string? status, string? examSlug, string? search, CancellationToken ct)
    {
        PromptStatus? wanted = null;
        if (!string.IsNullOrEmpty(status))
        {
            wanted = Statuses.TryGetValue(status, out var known) ? known : throw Errors.Validation("Situação desconhecida.");
        }

        var allExams = await exams.ListAsync(ct);
        Guid? examId = null;
        if (!string.IsNullOrEmpty(examSlug))
        {
            examId = allExams.FirstOrDefault(exam => exam.Slug == examSlug)?.Id ?? throw Errors.Validation("Vestibular desconhecido.");
        }

        var term = search?.Trim();
        if (term is { Length: > MaxSearchLength })
        {
            term = term[..MaxSearchLength];
        }

        var all = await prompts.ListAsync(examId, term, ct);
        var typeNames = new Dictionary<Guid, string>();
        foreach (var exam in allExams.Where(exam => all.Any(item => item.ExamId == exam.Id)))
        {
            foreach (var type in await essayTypes.ListByExamAsync(exam.Id, ct))
            {
                typeNames[type.Id] = type.Name;
            }
        }

        var examsById = allExams.ToDictionary(exam => exam.Id);
        var items = all
            .Where(item => wanted is null || item.Status == wanted)
            .Select(item => new PromptListItemDto(
                item.Id,
                examsById[item.ExamId].Slug,
                examsById[item.ExamId].Name,
                item.Slug,
                item.Version,
                item.Status,
                item.Title,
                item.EssayTypeId is { } typeId && typeNames.TryGetValue(typeId, out var name) ? name : null,
                item.TextCount,
                item.UpdatedAt))
            .ToList();

        return new PromptListDto(
            items,
            new PromptCountsDto(
                all.Count,
                all.Count(item => item.Status == PromptStatus.Draft),
                all.Count(item => item.Status == PromptStatus.Published),
                all.Count(item => item.Status == PromptStatus.Archived)));
    }

    public async Task<PromptAdminDto> GetAsync(Guid promptId, CancellationToken ct)
    {
        var prompt = await prompts.FindAsync(promptId, ct) ?? throw Errors.NotFound();
        var exam = await exams.FindByIdAsync(prompt.ExamId, ct) ?? throw Errors.Internal();
        var types = await essayTypes.ListByExamAsync(exam.Id, ct);
        var imageIds = await ImageIdsAsync(prompt, ct);

        return new PromptAdminDto(
            prompt.Id,
            exam.Slug,
            prompt.Slug,
            prompt.Version,
            prompt.Status,
            prompt.Title,
            prompt.Summary,
            prompt.SourceLabel,
            types.FirstOrDefault(type => type.Id == prompt.EssayTypeId)?.Slug,
            [.. prompt.Blocks.Select(block => ToDto(block, imageIds))],
            new PromptNotesDto(
                NullIfEmpty(prompt.GradingNotes.ExpectedFocus),
                NullIfEmpty(prompt.GradingNotes.TangentSignals),
                NullIfEmpty(prompt.GradingNotes.OffTopicSignals),
                prompt.GradingNotes.KeyData,
                NullIfEmpty(prompt.GradingNotes.Extra)),
            prompt.UpdatedAt,
            prompt.PublishedAt,
            IssuesOf(prompt, await ContextAsync(prompt, exam, types, imageIds, ct)));
    }

    public Task<PromptSavedDto> SaveAsync(Guid adminId, Guid promptId, PromptDocument document, CancellationToken ct) =>
        InTransactionAsync(async () =>
        {
            await prompts.LockAsync(promptId, ct);
            var prompt = await prompts.FindForUpdateAsync(promptId, ct) ?? throw Errors.NotFound();
            if (prompt.Status != PromptStatus.Draft)
            {
                throw Errors.PromptNotDraft();
            }

            if (document.ExpectedUpdatedAt is { } expected && expected != prompt.UpdatedAt)
            {
                throw Errors.PromptChanged();
            }

            var exam = await ResolveExamAsync(document.Exam, fallback: prompt.ExamId, ct);
            var slug = string.IsNullOrWhiteSpace(document.Slug) ? prompt.Slug : document.Slug.Trim();
            if (slug != prompt.Slug && prompt.Version > 1)
            {
                throw Errors.ValidationFailed([new ValidationIssue("slug", "O endereço não muda entre as versões de uma proposta.")]);
            }

            prompt.Replace(await ToContentAsync(document, exam, slug, ct), Now());
            audit.Record(Actor(adminId), "prompt.updated", "prompt", prompt.Id);
            return prompt;
        },
        ct);

    public Task<PromptSavedDto> ReorderAsync(Guid adminId, Guid promptId, IReadOnlyList<BlockPosition>? order, CancellationToken ct) =>
        InTransactionAsync(async () =>
        {
            await prompts.LockAsync(promptId, ct);
            var prompt = await prompts.FindForUpdateAsync(promptId, ct) ?? throw Errors.NotFound();
            prompt.Reorder(order ?? [], Now());
            audit.Record(Actor(adminId), "prompt.reordered", "prompt", prompt.Id);
            return prompt;
        },
        ct);

    public async Task<PromptValidationDto> ValidateAsync(Guid promptId, CancellationToken ct)
    {
        var prompt = await prompts.FindAsync(promptId, ct) ?? throw Errors.NotFound();
        var issues = IssuesOf(prompt, await ContextAsync(prompt, ct));
        return new PromptValidationDto(issues.Count == 0, issues);
    }

    public Task<PromptSavedDto> PublishAsync(Guid adminId, Guid promptId, CancellationToken ct) =>
        InTransactionAsync(async () =>
        {
            await prompts.LockAsync(promptId, ct);
            var prompt = await prompts.FindAsync(promptId, ct) ?? throw Errors.NotFound();
            var now = Now();

            // O agregado decide se pode publicar; o banco grava a passagem só se a versão ainda é rascunho (regra 10).
            prompt.Publish(await ContextAsync(prompt, ct), adminId, now);
            var replaced = await prompts.ArchivePublishedAsync(prompt.Slug, now, ct);
            if (!await prompts.TryPublishAsync(promptId, adminId, now, ct))
            {
                throw Errors.PromptNotDraft();
            }

            audit.Record(Actor(adminId), "prompt.published", "prompt", prompt.Id, new { prompt.Slug, prompt.Version, replaced });
            return prompt;
        },
        ct);

    public async Task<PromptSavedDto> NewVersionAsync(Guid adminId, Guid promptId, CancellationToken ct)
    {
        var source = await prompts.FindAsync(promptId, ct) ?? throw Errors.NotFound();

        // Um rascunho por proposta: com dois abertos, não haveria como saber qual deles é a próxima versão.
        if (source.Status == PromptStatus.Draft || await prompts.HasDraftAsync(source.Slug, ct))
        {
            throw Errors.PromptInvalidState();
        }

        var draft = source.NewVersion(await prompts.MaxVersionAsync(source.Slug, ct) + 1, adminId, Now());
        prompts.Add(draft);
        audit.Record(Actor(adminId), "prompt.version_created", "prompt", draft.Id, new { draft.Slug, draft.Version });

        try
        {
            await unitOfWork.SaveChangesAsync(ct);
        }
        catch (UniqueConflictException)
        {
            // Outro pedido criou a mesma versão primeiro.
            throw Errors.PromptInvalidState();
        }

        return await ToSavedAsync(draft, ct);
    }

    public Task<PromptSavedDto> ArchiveAsync(Guid adminId, Guid promptId, CancellationToken ct) =>
        InTransactionAsync(async () =>
        {
            var prompt = await prompts.FindAsync(promptId, ct) ?? throw Errors.NotFound();
            var now = Now();
            prompt.Archive(now);
            if (!await prompts.TryArchiveAsync(promptId, now, ct))
            {
                throw Errors.PromptInvalidState();
            }

            audit.Record(Actor(adminId), "prompt.archived", "prompt", prompt.Id, new { prompt.Slug, prompt.Version });
            return prompt;
        },
        ct);

    public async Task DeleteAsync(Guid adminId, Guid promptId, CancellationToken ct)
    {
        var prompt = await prompts.FindAsync(promptId, ct) ?? throw Errors.NotFound();
        if (prompt.Status != PromptStatus.Draft)
        {
            throw Errors.PromptNotDraft();
        }

        await unitOfWork.InTransactionAsync(
            async () =>
            {
                if (!await prompts.TryDeleteDraftAsync(promptId, ct))
                {
                    throw Errors.PromptNotDraft();
                }

                audit.Record(Actor(adminId), "prompt.deleted", "prompt", promptId, new { prompt.Slug, prompt.Version });
                return true;
            },
            ct);
    }

    public async Task<PromptImageDto> UploadImageAsync(Guid adminId, PromptImageUpload upload, CancellationToken ct)
    {
        // O tipo vem do conteúdo do arquivo, não do nome nem do que o navegador declarou.
        if (upload.Content.Length is < 1 or > (int)MaxImageBytes || ImageFormat.Detect(upload.Content) is not { } mime)
        {
            throw Errors.Validation("Envie uma imagem JPG, PNG ou WebP, com até 5 MB.");
        }

        var id = Guid.CreateVersion7();
        var publicId = $"prompts/{id:N}";
        var stored = await images.UploadAsync(publicId, upload.Content, mime, ct);

        assets.Add(Asset.ForPromptImage(id, adminId, publicId, stored.Bytes, mime, time.GetUtcNow()));
        audit.Record(Actor(adminId), "asset.uploaded", "asset", id, new { scope = "prompt_image", stored.Bytes });
        await unitOfWork.SaveChangesAsync(ct);

        return new PromptImageDto(id, images.ViewUrl(publicId, time.GetUtcNow()), stored.Width, stored.Height);
    }

    public async Task<IReadOnlyList<EssayTypeOptionDto>> ListEssayTypesAsync(string examSlug, CancellationToken ct)
    {
        var exam = await exams.FindBySlugAsync(examSlug, ct) ?? throw Errors.NotFound();
        var options = new List<EssayTypeOptionDto>();
        foreach (var type in (await essayTypes.ListByExamAsync(exam.Id, ct)).OrderBy(type => type.Name, StringComparer.Ordinal))
        {
            options.Add(new EssayTypeOptionDto(type.Slug, type.Name, await profiles.FindActiveAsync(exam.Id, type.Id, ct) is not null));
        }

        return options;
    }

    /// <summary>Endereço a partir do título: minúsculas, sem acento, com hífen entre as palavras.</summary>
    internal static string Slugify(string? title)
    {
        var slug = new StringBuilder();
        foreach (var letter in (title ?? "").Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(letter) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var lower = char.ToLowerInvariant(letter);
            if (lower is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                slug.Append(lower);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        var result = slug.ToString().Trim('-');
        if (result.Length > 60)
        {
            result = result[..60].Trim('-');
        }

        return result.Length > 0 ? result : "proposta";
    }

    private static string Actor(Guid adminId) => $"user:{adminId}";

    private static AppError SlugTaken() => Errors.ValidationFailed(
        [new ValidationIssue("slug", "Já existe uma proposta com esse endereço. Abra a proposta e use \"Criar nova versão\".")]);

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;

    /// <summary>Milissegundos inteiros: o valor volta igual do banco e do JSON, e o <c>expectedUpdatedAt</c> compara certo.</summary>
    private DateTimeOffset Now()
    {
        var now = time.GetUtcNow();
        return new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerMillisecond), now.Offset);
    }

    private async Task<PromptSavedDto> InTransactionAsync(Func<Task<Prompt>> work, CancellationToken ct)
    {
        Prompt prompt;
        try
        {
            prompt = await unitOfWork.InTransactionAsync(work, ct);
        }
        catch (UniqueConflictException)
        {
            // Endereço e versão já em uso por outra proposta.
            throw SlugTaken();
        }

        // Relê: as passagens de situação são gravadas direto no banco, fora do objeto carregado.
        return await ToSavedAsync(await prompts.FindAsync(prompt.Id, ct) ?? throw Errors.NotFound(), ct);
    }

    private async Task<Exam> ResolveExamAsync(string? slug, Guid? fallback, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(slug))
        {
            return await exams.FindBySlugAsync(slug.Trim(), ct)
                ?? throw Errors.ValidationFailed([new ValidationIssue("exam", "Vestibular desconhecido.")]);
        }

        if (fallback is { } examId)
        {
            return await exams.FindByIdAsync(examId, ct) ?? throw Errors.Internal();
        }

        // Rascunho novo sem vestibular: o primeiro ativo, que no MVP é a Fuvest.
        return (await exams.ListAsync(ct)).FirstOrDefault(exam => exam.Status == ExamStatus.Active) ?? throw Errors.Internal();
    }

    private Task<PromptContent> ToContentAsync(PromptDocument document, Exam exam, string slug, CancellationToken ct) =>
        ToContentAsync(essayTypes, document, exam, slug, ct);

    /// <summary>O JSON do cadastro como conteúdo da proposta; o que não dá nem para guardar vira <c>VALIDATION_FAILED</c>.</summary>
    internal static async Task<PromptContent> ToContentAsync(
        IEssayTypeRepository essayTypes, PromptDocument document, Exam exam, string slug, CancellationToken ct)
    {
        var issues = new List<ValidationIssue>();

        Guid? essayTypeId = null;
        if (!string.IsNullOrWhiteSpace(document.EssayType))
        {
            essayTypeId = (await essayTypes.ListByExamAsync(exam.Id, ct)).FirstOrDefault(type => type.Slug == document.EssayType.Trim())?.Id;
            if (essayTypeId is null)
            {
                issues.Add(new ValidationIssue("essayType", "Categoria de correção desconhecida neste vestibular."));
            }
        }

        var blocks = new List<PromptBlockContent>();
        var sent = document.Blocks ?? [];
        for (var index = 0; index < sent.Count; index++)
        {
            var block = sent[index];
            if (block?.Position is not { } position)
            {
                issues.Add(new ValidationIssue($"blocks[{index}].position", "Informe a posição do bloco."));
            }
            else if (block.Kind is null || !PromptBlockKinds.TryParse(block.Kind, out var kind))
            {
                issues.Add(new ValidationIssue($"blocks[{index}].kind", "Tipo de bloco desconhecido."));
            }
            else
            {
                blocks.Add(new PromptBlockContent(block.Id, position, kind, ToData(block.Data)));
            }
        }

        if (issues.Count > 0)
        {
            throw Errors.ValidationFailed(issues);
        }

        var notes = document.GradingNotes;
        return new PromptContent(
            exam.Id,
            slug,
            document.Title,
            document.Summary,
            document.SourceLabel,
            essayTypeId,
            blocks,
            new PromptNotesContent(notes?.ExpectedFocus, notes?.TangentSignals, notes?.OffTopicSignals, notes?.KeyData, notes?.Extra));
    }

    private static PromptBlockData ToData(PromptBlockDataDto? data) => data is null
        ? PromptBlockData.None
        : new PromptBlockData
        {
            Label = data.Label,
            Format = data.Format,
            Source = data.Source,
            Body = data.Body,
            Columns = data.Columns,
            AssetId = data.AssetId,
            Alt = data.Alt,
            AiDescription = data.AiDescription,
            Title = data.Title,
            Items = data.Items,
            Text = data.Text,
            CardItems = data.CardItems?.Select(item => new PromptCardItem
            {
                Format = item?.Format,
                Body = item?.Body,
                AssetId = item?.AssetId,
                Alt = item?.Alt,
                AiDescription = item?.AiDescription,
                Caption = item?.Caption,
            }).ToList(),
            Width = data.Width,
            HideLabel = data.HideLabel,
            Notes = data.Notes,
        };

    private PromptBlockDto ToDto(PromptBlock block, IReadOnlyDictionary<Guid, string> imageIds)
    {
        var data = block.Data;
        return new PromptBlockDto(
            block.Id,
            block.Position,
            PromptBlockKinds.NameOf(block.Kind),
            new PromptBlockDataDto
            {
                Label = data.Label,
                Format = data.Format,
                Source = data.Source,
                Body = data.Body,
                Columns = data.Columns,
                AssetId = data.AssetId,
                Alt = data.Alt,
                AiDescription = data.AiDescription,
                Title = data.Title,
                Items = data.Items,
                Text = data.Text,
                CardItems = data.CardItems?.Select(item => new PromptCardItemDto
                {
                    Format = item.Format,
                    Body = item.Body,
                    AssetId = item.AssetId,
                    Alt = item.Alt,
                    AiDescription = item.AiDescription,
                    Caption = item.Caption,
                    ImageUrl = ImageUrl(item.AssetId, imageIds),
                }).ToList(),
                Width = data.Width,
                HideLabel = data.HideLabel,
                Notes = data.Notes,
                ImageUrl = ImageUrl(data.AssetId, imageIds),
            });
    }

    private string? ImageUrl(Guid? assetId, IReadOnlyDictionary<Guid, string> imageIds) =>
        assetId is { } id && imageIds.TryGetValue(id, out var publicId) ? images.ViewUrl(publicId, time.GetUtcNow()) : null;

    private Task<IReadOnlyDictionary<Guid, string>> ImageIdsAsync(Prompt prompt, CancellationToken ct) =>
        assets.PromptImagePublicIdsAsync([.. prompt.Blocks.SelectMany(block => block.Data.AssetIds()).Distinct()], ct);

    private async Task<PublicationContext> ContextAsync(Prompt prompt, CancellationToken ct)
    {
        var exam = await exams.FindByIdAsync(prompt.ExamId, ct) ?? throw Errors.Internal();
        return await ContextAsync(prompt, exam, await essayTypes.ListByExamAsync(exam.Id, ct), await ImageIdsAsync(prompt, ct), ct);
    }

    private async Task<PublicationContext> ContextAsync(
        Prompt prompt, Exam exam, IReadOnlyList<EssayType> types, IReadOnlyDictionary<Guid, string> imageIds, CancellationToken ct)
    {
        var hasProfile = prompt.EssayTypeId is { } typeId
            && types.Any(type => type.Id == typeId)
            && await profiles.FindActiveAsync(exam.Id, typeId, ct) is not null;
        return new PublicationContext(hasProfile, imageIds.Keys.ToHashSet());
    }

    private static IReadOnlyList<PromptIssueDto> IssuesOf(Prompt prompt, PublicationContext context) =>
        prompt.Status == PromptStatus.Draft
            ? [.. prompt.PublicationIssues(context).Select(issue => new PromptIssueDto(issue.Path, issue.Message))]
            : [];

    private async Task<PromptSavedDto> ToSavedAsync(Prompt prompt, CancellationToken ct) =>
        new(
            prompt.Id,
            prompt.Slug,
            prompt.Version,
            prompt.Status,
            prompt.UpdatedAt,
            [.. prompt.Blocks.Select(block => new SavedBlockDto(block.Id, block.Position))],
            IssuesOf(prompt, await ContextAsync(prompt, ct)));
}

/// <summary>Reconhece o formato de uma imagem pelos primeiros bytes do arquivo.</summary>
public static class ImageFormat
{
    /// <returns>O tipo (<c>image/jpeg</c>, <c>image/png</c> ou <c>image/webp</c>); <c>null</c> se não é nenhum dos três.</returns>
    public static string? Detect(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith<byte>([0xFF, 0xD8, 0xFF]))
        {
            return "image/jpeg";
        }

        if (content.StartsWith<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "image/png";
        }

        return content.Length >= 12 && content[..4].SequenceEqual("RIFF"u8) && content[8..12].SequenceEqual("WEBP"u8)
            ? "image/webp"
            : null;
    }
}

/// <summary>Os nomes dos tipos de bloco no JSON (<c>collection_item</c>, <c>command</c>…).</summary>
internal static class PromptBlockKinds
{
    private static readonly Dictionary<string, PromptBlockKind> Kinds = new(StringComparer.Ordinal)
    {
        ["collection_item"] = PromptBlockKind.CollectionItem,
        ["card"] = PromptBlockKind.Card,
        ["command"] = PromptBlockKind.Command,
        ["instructions"] = PromptBlockKind.Instructions,
        ["heading"] = PromptBlockKind.Heading,
        ["paragraph"] = PromptBlockKind.Paragraph,
        ["divider"] = PromptBlockKind.Divider,
    };

    private static readonly Dictionary<PromptBlockKind, string> Names = Kinds.ToDictionary(pair => pair.Value, pair => pair.Key);

    public static bool TryParse(string name, out PromptBlockKind kind) => Kinds.TryGetValue(name, out kind);

    public static string NameOf(PromptBlockKind kind) => Names[kind];
}
