using System.Text;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Catalog;
using RedigiAI.Domain.Essays;

namespace RedigiAI.Application.Catalog;

/// <summary>Catálogo público: vestibulares, propostas de redação e grades de correção.</summary>
public interface ICatalogService
{
    Task<IReadOnlyList<ExamDto>> ListExamsAsync(CancellationToken ct);

    /// <summary>As propostas publicadas do vestibular.</summary>
    /// <returns><c>null</c> se o vestibular não existe ou não está ativo.</returns>
    Task<IReadOnlyList<PromptSummaryDto>?> ListPromptsAsync(string examSlug, string? search, CancellationToken ct);

    /// <summary>
    /// A versão publicada da proposta. Com <paramref name="version"/>, essa versão, se já foi publicada: é como uma
    /// redação antiga continua mostrando a proposta que o aluno usou.
    /// </summary>
    /// <returns><c>null</c> se não há versão disponível.</returns>
    Task<PromptDetailDto?> GetPromptAsync(string slug, int? version, CancellationToken ct);

    Task<RubricDto?> GetRubricAsync(Guid rubricVersionId, CancellationToken ct);
}

internal sealed class CatalogService(
    IExamRepository exams,
    IEssayTypeRepository essayTypes,
    IPromptRepository prompts,
    IAssetRepository assets,
    IPromptImageStorage images,
    IRubricRepository rubrics) : ICatalogService
{
    private const int MaxSearchLength = 80;

    public async Task<IReadOnlyList<ExamDto>> ListExamsAsync(CancellationToken ct)
    {
        var latestRubrics = await rubrics.LatestVersionIdsAsync(ct);

        return [.. (await exams.ListAsync(ct)).Select(e => new ExamDto(
            e.Slug, e.Name, e.Status, e.SheetLines, latestRubrics.TryGetValue(e.Id, out var rubricId) ? rubricId : null))];
    }

    public async Task<IReadOnlyList<PromptSummaryDto>?> ListPromptsAsync(string examSlug, string? search, CancellationToken ct)
    {
        var exam = await exams.FindBySlugAsync(examSlug, ct);
        if (exam is not { Status: ExamStatus.Active })
        {
            return null;
        }

        var types = await ListTypesAsync(exam.Id, ct);
        var found = await prompts.ListPublishedAsync(exam.Id, CleanSearch(search), ct);

        // Proposta publicada sempre tem tipo; a que não tiver (dado antigo) fica fora da lista.
        return [.. found
            .Where(p => p.EssayTypeId is { } typeId && types.ContainsKey(typeId))
            .Select(p => new PromptSummaryDto(
                p.Id,
                p.Slug,
                p.Title,
                p.Summary,
                p.GenreLabel ?? types[p.EssayTypeId!.Value].Name,
                types[p.EssayTypeId!.Value].Name,
                p.TextCount,
                p.SheetLinesMin,
                p.SheetLinesMax,
                p.TitleRequired,
                exam.Slug,
                exam.Name))];
    }

    public async Task<PromptDetailDto?> GetPromptAsync(string slug, int? version, CancellationToken ct)
    {
        var prompt = version is { } wanted
            ? await prompts.FindReleasedAsync(slug, wanted, ct)
            : await prompts.FindPublishedAsync(slug, ct);
        if (prompt?.EssayTypeId is not { } essayTypeId
            || await exams.FindByIdAsync(prompt.ExamId, ct) is not { Status: ExamStatus.Active } exam
            || !(await ListTypesAsync(exam.Id, ct)).TryGetValue(essayTypeId, out var essayType))
        {
            return null;
        }

        var imageIds = await assets.PromptImagePublicIdsAsync(
            [.. prompt.Blocks.Select(block => block.Data.AssetId).OfType<Guid>().Distinct()], ct);

        return new PromptDetailDto(
            prompt.Id,
            prompt.Slug,
            prompt.Version,
            prompt.Status,
            prompt.Title,
            prompt.Summary,
            prompt.GenreLabel ?? essayType.Name,
            essayType.Id,
            essayType.Name,
            EssayTitle.Compose(exam.Name, essayType.Name, prompt.Title),
            exam.Slug,
            exam.Name,
            exam.SheetLines,
            prompt.SheetLinesMin,
            prompt.SheetLinesMax,
            prompt.TitleRequired,
            prompt.Blocks.Count(block => block.Kind == PromptBlockKind.CollectionItem),
            [.. prompt.Blocks.Select(block => ToDto(block, imageIds))]);
    }

    public async Task<RubricDto?> GetRubricAsync(Guid rubricVersionId, CancellationToken ct)
    {
        var rubric = await rubrics.FindAsync(rubricVersionId, ct);
        if (rubric is null || await exams.FindByIdAsync(rubric.ExamId, ct) is not { } exam)
        {
            return null;
        }

        return new RubricDto(
            rubric.Id,
            exam.Slug,
            exam.Name,
            rubric.Version,
            rubric.MaxLevel,
            rubric.MaxScore,
            [.. rubric.Criteria.OrderBy(c => c.Position).Select(c => new RubricCriterionDto(
                c.Position,
                c.Name,
                c.Weight,
                c.MaxPoints,
                [.. c.LevelDescriptors.OrderBy(l => l.Key).Select(l => new RubricLevelDto(l.Key, l.Value))]))],
            [.. rubric.ZeroRules.Select(z => new ZeroRuleDto(z.Code, z.Description))]);
    }

    private async Task<Dictionary<Guid, EssayType>> ListTypesAsync(Guid examId, CancellationToken ct) =>
        (await essayTypes.ListByExamAsync(examId, ct)).ToDictionary(t => t.Id);

    /// <summary>O bloco como o aluno vê: sem o id do arquivo nem a descrição da imagem feita para a IA.</summary>
    private PromptBlockPublicDto ToDto(PromptBlock block, IReadOnlyDictionary<Guid, string> imageIds)
    {
        var data = block.Data;
        return new PromptBlockPublicDto(
            block.Position,
            PromptBlockKinds.NameOf(block.Kind),
            new PromptBlockPublicDataDto
            {
                Label = data.Label,
                Format = data.Format,
                Source = data.Source,
                Body = data.Body,
                Columns = data.Columns,
                ImageUrl = data.AssetId is { } assetId && imageIds.TryGetValue(assetId, out var publicId) ? images.PublicUrl(publicId) : null,
                Alt = data.Alt,
                Title = data.Title,
                Items = data.Items,
                Text = data.Text,
            });
    }

    /// <summary>Tira caracteres de controle (o PostgreSQL recusa NUL) e corta sem partir um par substituto.</summary>
    private static string? CleanSearch(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return null;
        }

        var clean = new StringBuilder(MaxSearchLength);
        foreach (var rune in search.EnumerateRunes())
        {
            if (clean.Length + rune.Utf16SequenceLength > MaxSearchLength)
            {
                break;
            }

            if (!Rune.IsControl(rune) && rune != Rune.ReplacementChar)
            {
                clean.Append(rune.ToString());
            }
        }

        var trimmed = clean.ToString().Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
