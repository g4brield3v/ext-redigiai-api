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
    IThemeRepository themes,
    IPromptTemplateRepository prompts,
    IGuidelinesRepository guidelines,
    IWritingRulesRepository writingRules,
    IGradingProfileRepository profiles,
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

        // Banco criado antes da grade oficial: ela entra como a versão seguinte e os temas passam a usá-la.
        var rubric = await rubrics.FindLatestAsync(fuvest.Id, ct);
        if (rubric?.Source != FuvestKnowledgeBaseV1.RubricSource)
        {
            rubric = PublishRubric(fuvest.Id, (rubric?.Version ?? 0) + 1, now);
            rubrics.Add(rubric);
            foreach (var theme in await themes.ListForUpdateAsync(fuvest.Id, ct))
            {
                theme.UseRubric(rubric);
            }

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

        // Só nesta carga inicial o perfil já nasce ativo; depois disso, perfil novo passa pelos testes antes.
        foreach (var essayType in await essayTypes.ListByExamAsync(fuvest.Id, ct))
        {
            if (await profiles.FindActiveAsync(fuvest.Id, essayType.Id, ct) is not null)
            {
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
