using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Catalog;
using RedigiAI.Domain.Grading;

namespace RedigiAI.Application.Grading;

/// <summary>
/// Carga inicial da base de conhecimento da correção: versão 1 de cada camada e um perfil de correção ativo por
/// tipo de redação da Fuvest. Idempotente: só cria o que ainda não existe e nunca edita o que já foi gravado.
/// </summary>
internal sealed class GradingSeedService(
    IExamRepository exams,
    IEssayTypeRepository essayTypes,
    IRubricRepository rubrics,
    IPromptTemplateRepository prompts,
    IGuidelinesRepository guidelines,
    IWritingRulesRepository writingRules,
    IGradingProfileRepository profiles,
    IUnitOfWork unitOfWork,
    TimeProvider time)
{
    /// <summary>Só prepara as entidades; quem chama grava tudo junto.</summary>
    /// <returns><c>true</c> se algo foi criado.</returns>
    public async Task<bool> EnsureAsync(CancellationToken ct)
    {
        var fuvest = await exams.FindBySlugAsync("fuvest", ct);
        if (fuvest is null)
        {
            return false;
        }

        var now = time.GetUtcNow();
        var created = false;

        // Banco criado antes da grade oficial: ela entra como a versão seguinte.
        var rubric = await rubrics.FindLatestAsync(fuvest.Id, ct);
        if (rubric?.Source != FuvestKnowledgeBaseV1.RubricSource)
        {
            rubric = PublishRubric(fuvest.Id, (rubric?.Version ?? 0) + 1, now);
            rubrics.Add(rubric);
            created = true;
        }

        var transcriptionPrompt = await prompts.FindLatestAsync(PromptKind.Transcription, ct);
        if (transcriptionPrompt is null)
        {
            transcriptionPrompt = PromptTemplate.Create(
                PromptKind.Transcription, 1, FuvestKnowledgeBaseV1.TranscriptionSystem, userText: null, now);
            prompts.Add(transcriptionPrompt);
            created = true;
        }

        var gradingPrompt = await prompts.FindLatestAsync(PromptKind.Grading, ct);
        if (gradingPrompt is null)
        {
            gradingPrompt = PromptTemplate.Create(
                PromptKind.Grading, 1, FuvestKnowledgeBaseV1.GradingSystem, FuvestKnowledgeBaseV1.GradingUser, now);
            prompts.Add(gradingPrompt);
            created = true;
        }

        var manual = await guidelines.FindLatestAsync(fuvest.Id, ct);
        if (manual is null)
        {
            manual = GuidelinesVersion.Create(
                fuvest.Id,
                1,
                FuvestKnowledgeBaseV1.GeneralRules,
                FuvestKnowledgeBaseV1.GuidanceWriting,
                FuvestKnowledgeBaseV1.CriterionGuidelines,
                now);
            guidelines.Add(manual);
            created = true;
        }

        var rules = await writingRules.FindLatestAsync(ct);
        if (rules is null)
        {
            rules = WritingRulesVersion.Create(1, FuvestKnowledgeBaseV1.WritingRules, FuvestKnowledgeBaseV1.WritingRulesNotes, now);
            writingRules.Add(rules);
            created = true;
        }

        // Só nesta carga inicial o perfil já nasce ativo; depois disso, perfil novo passa pelos testes antes. A exceção
        // é categoria nova que copia o perfil ativo de outra (CopiesProfileOf): é o mesmo perfil, já em uso.
        var types = await essayTypes.ListByExamAsync(fuvest.Id, ct);
        foreach (var essayType in types)
        {
            if (await profiles.FindActiveAsync(fuvest.Id, essayType.Id, ct) is not null)
            {
                continue;
            }

            if (await CopyOfActiveProfileAsync(fuvest.Id, essayType, types, now, ct) is { } copy)
            {
                profiles.Add(copy);
                created = true;
                continue;
            }

            var profile = GradingProfile.Draft(
                fuvest.Id,
                essayType.Id,
                version: 1,
                transcriptionPrompt,
                gradingPrompt,
                rubric.Id,
                manual.Id,
                rules.Id,
                FuvestKnowledgeBaseV1.TranscriptionModel,
                FuvestKnowledgeBaseV1.GradingModel,
                now);
            profile.Activate(now);
            profiles.Add(profile);
            created = true;
        }

        return created;
    }

    /// <summary>
    /// Só em desenvolvimento: se as instruções de correção ou o manual do corretor do programa mudaram, grava a
    /// versão seguinte do que mudou e troca cada perfil ativo por um perfil novo, igual em tudo o mais, que aponta
    /// para ela. Nada publicado é editado: as correções já feitas continuam ligadas ao perfil, às instruções e ao
    /// manual com que foram feitas.
    /// </summary>
    /// <returns><c>true</c> se algo foi trocado.</returns>
    public async Task<bool> ReplaceOutdatedInstructionsAsync(CancellationToken ct)
    {
        var fuvest = await exams.FindBySlugAsync("fuvest", ct);
        var latest = await prompts.FindLatestAsync(PromptKind.Grading, ct);
        var latestManual = fuvest is null ? null : await guidelines.FindLatestAsync(fuvest.Id, ct);
        if (fuvest is null || latest is null || latestManual is null)
        {
            return false;
        }

        var promptOutdated = latest.SystemText != FuvestKnowledgeBaseV1.GradingSystem || latest.UserText != FuvestKnowledgeBaseV1.GradingUser;
        var manualOutdated = latestManual.GeneralRules != FuvestKnowledgeBaseV1.GeneralRules
            || latestManual.GuidanceWriting != FuvestKnowledgeBaseV1.GuidanceWriting;
        if (!promptOutdated && !manualOutdated)
        {
            return false;
        }

        var now = time.GetUtcNow();
        return await unitOfWork.InTransactionAsync(
            async () =>
            {
                PromptTemplate? newPrompt = null;
                if (promptOutdated)
                {
                    newPrompt = PromptTemplate.Create(
                        PromptKind.Grading, latest.Version + 1, FuvestKnowledgeBaseV1.GradingSystem, FuvestKnowledgeBaseV1.GradingUser, now);
                    prompts.Add(newPrompt);
                }

                GuidelinesVersion? newManual = null;
                if (manualOutdated)
                {
                    newManual = GuidelinesVersion.Create(
                        fuvest.Id,
                        latestManual.Version + 1,
                        FuvestKnowledgeBaseV1.GeneralRules,
                        FuvestKnowledgeBaseV1.GuidanceWriting,
                        FuvestKnowledgeBaseV1.CriterionGuidelines,
                        now);
                    guidelines.Add(newManual);
                }

                foreach (var essayType in await essayTypes.ListByExamAsync(fuvest.Id, ct))
                {
                    if (await profiles.FindActiveAsync(fuvest.Id, essayType.Id, ct) is not { } active
                        || await prompts.FindAsync(active.TranscriptionPromptId, ct) is not { } transcriptionPrompt
                        || (newPrompt ?? await prompts.FindAsync(active.GradingPromptId, ct)) is not { } gradingPrompt
                        || !await profiles.TryRetireAsync(active.Id, ct))
                    {
                        continue;
                    }

                    var replacement = GradingProfile.Draft(
                        fuvest.Id,
                        essayType.Id,
                        await profiles.MaxVersionAsync(fuvest.Id, essayType.Id, ct) + 1,
                        transcriptionPrompt,
                        gradingPrompt,
                        active.RubricVersionId,
                        newManual?.Id ?? active.GuidelinesVersionId,
                        active.WritingRulesVersionId,
                        active.TranscriptionModel,
                        active.GradingModel,
                        now);
                    replacement.Activate(now);
                    profiles.Add(replacement);
                }

                return true;
            },
            ct);
    }

    /// <summary>
    /// Categorias que corrigem exatamente como outra: "Dissertação" (o que a prova de 2025 pede) usa o perfil de
    /// "Dissertação em prosa" (decisão de 21/10/2026).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> CopiesProfileOf = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["dissertacao"] = "dissertacao-em-prosa",
    };

    /// <summary>Um perfil ativo igual em tudo ao da categoria de origem; <c>null</c> se não há de onde copiar.</summary>
    private async Task<GradingProfile?> CopyOfActiveProfileAsync(
        Guid examId, EssayType essayType, IReadOnlyList<EssayType> types, DateTimeOffset now, CancellationToken ct)
    {
        if (!CopiesProfileOf.TryGetValue(essayType.Slug, out var sourceSlug)
            || types.FirstOrDefault(type => type.Slug == sourceSlug) is not { } source
            || await profiles.FindActiveAsync(examId, source.Id, ct) is not { } active
            || await prompts.FindAsync(active.TranscriptionPromptId, ct) is not { } transcriptionPrompt
            || await prompts.FindAsync(active.GradingPromptId, ct) is not { } gradingPrompt)
        {
            return null;
        }

        var copy = GradingProfile.Draft(
            examId,
            essayType.Id,
            await profiles.MaxVersionAsync(examId, essayType.Id, ct) + 1,
            transcriptionPrompt,
            gradingPrompt,
            active.RubricVersionId,
            active.GuidelinesVersionId,
            active.WritingRulesVersionId,
            active.TranscriptionModel,
            active.GradingModel,
            now);
        copy.Activate(now);
        return copy;
    }

    public static RubricVersion PublishRubric(Guid examId, int version, DateTimeOffset now) =>
        RubricVersion.Publish(
            examId,
            version,
            FuvestKnowledgeBaseV1.MaxLevel,
            now,
            FuvestKnowledgeBaseV1.ZeroRules,
            FuvestKnowledgeBaseV1.Criteria,
            FuvestKnowledgeBaseV1.RubricSource,
            FuvestKnowledgeBaseV1.RubricLabel);
}
