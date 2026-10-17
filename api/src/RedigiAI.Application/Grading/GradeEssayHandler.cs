using System.Text.Json;
using Microsoft.Extensions.Options;
using RedigiAI.Application.Abstractions;
using RedigiAI.Application.Essays;
using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Catalog;
using RedigiAI.Domain.Common;
using RedigiAI.Domain.Essays;
using RedigiAI.Domain.Grading;
using RedigiAI.Domain.Jobs;

namespace RedigiAI.Application.Grading;

/// <summary>Payload do job <c>send_email</c>: o modelo do e-mail e a que ele se refere. Nunca o conteúdo.</summary>
public sealed record EmailJobPayload(string Template, Guid RefId);

/// <summary>
/// Job <c>grade</c>: corrige a redação com as camadas do perfil de correção, valida a resposta da IA, calcula a
/// nota no backend e conclui a redação. Na mesma transação ficam a correção, o status <c>completed</c>, o
/// consumo do crédito e o job do e-mail.
/// </summary>
internal sealed class GradeEssayHandler(
    IEssayRepository essays,
    IExamRepository exams,
    IEssayTypeRepository essayTypes,
    IPromptRepository proposals,
    IRubricRepository rubrics,
    IGradingProfileRepository profiles,
    IPromptTemplateRepository prompts,
    IGuidelinesRepository guidelines,
    IWritingRulesRepository writingRules,
    ITranscriptionRepository transcriptions,
    ICorrectionRepository corrections,
    ICreditRepository credits,
    IEssayGrader grader,
    IJobQueue jobs,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    EssayOutcomes outcomes,
    AiSpending spending,
    AiBudget budget,
    IOptions<CollectionCopyOptions> copyOptions,
    TimeProvider time) : IJobHandler
{
    public const string CorrectionReadyTemplate = "correction_ready";

    /// <summary>Resposta que não passa na validação: pede mais uma vez; repetida, a falha é definitiva.</summary>
    private const int InvalidResponseAttempts = 2;

    /// <summary>Resposta cortada no <c>max_tokens</c>: pede de novo com o dobro; cortada de novo, a falha é definitiva.</summary>
    private const int TruncatedResponseAttempts = 2;
    private const int EmailAttempts = 6;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Type => JobTypes.Grade;

    public async Task HandleAsync(string payload, CancellationToken ct)
    {
        var essayId = JsonSerializer.Deserialize<EssayJobPayload>(payload, Json)!.EssayId;
        var essay = await essays.FindByIdAsync(essayId, ct);
        var profile = essay?.Status switch
        {
            EssayStatus.Queued => await StartAsync(essay, ct),
            // Nova tentativa do job: a redação termina com o perfil com que começou, mesmo que outro tenha sido ativado.
            EssayStatus.Processing when essay.GradingProfileId is { } profileId =>
                await profiles.FindAsync(profileId, ct) ?? throw Errors.AiFailed("GRADING_PROFILE_MISSING"),
            _ => null,
        };
        if (essay is null || profile is null)
        {
            // Outra execução já começou ou encerrou esta redação.
            return;
        }

        var layers = await LoadLayersAsync(essay, profile, ct);
        var transcription = await transcriptions.FindByEssayAsync(essay.Id, ct) ?? throw Errors.AiFailed("TRANSCRIPTION_MISSING");
        var lines = transcription.FinalLines();

        // Antes da chamada: o que o aluno copiou da coletânea vai para a IA e fica gravado na correção.
        var copy = CollectionCopyDetector.Find(layers.Proposal, lines, copyOptions.Value.MinWords);
        var context = new GradingContext(
            essay.Id,
            layers.Rubric,
            profile.Id,
            layers.Proposal.Id,
            layers.Proposal.GradingNotes.Id,
            [.. lines.Select(line => new EssayLine(line.Number, line.Text, line.Indent))],
            transcription.IdentificationFound,
            layers.WritingRules.Rules.Select(rule => rule.Code).ToHashSet(StringComparer.Ordinal),
            copy.Passages,
            CopyRequiresZero: copy.CopiedWords > 0 && copy.Share >= copyOptions.Value.ZeroShare);

        var request = GradingPromptBuilder.Build(
            layers, lines, transcription.IdentificationFound, copy.Passages, profile.GradingModel, budget.GradingMaxTokens);
        var correction = await GradeAsync(essay.Id, context, request, ct);

        await unitOfWork.InTransactionAsync(
            async () =>
            {
                if (!await essays.TryMoveAsync(essay.Id, EssayTransition.Graded(), ct))
                {
                    return false;
                }

                corrections.Add(correction);
                jobs.Enqueue(JobTypes.SendEmail, new EmailJobPayload(CorrectionReadyTemplate, essay.Id), EmailAttempts);
                audit.Record(
                    "system",
                    "essay.status_changed",
                    "essay",
                    essay.Id,
                    new { from = "Processing", to = "Completed", correctionId = correction.Id, gradingProfileId = profile.Id });

                // A correção reservada no envio já saiu do saldo; aqui ela passa a ser definitiva.
                var bucket = await credits.FindReservedBucketAsync(essay.Id, ct);
                audit.Record("system", "credit.consumed", "essay", essay.Id, new { bucketId = bucket?.Id });
                return true;
            },
            ct);
    }

    public async Task OnDeadAsync(string payload, CancellationToken ct)
    {
        var essayId = JsonSerializer.Deserialize<EssayJobPayload>(payload, Json)!.EssayId;
        if (!await outcomes.EndWithRefundAsync(essayId, EssayTransition.GradingFailed(started: true, time.GetUtcNow()), ct))
        {
            await outcomes.EndWithRefundAsync(essayId, EssayTransition.GradingFailed(started: false, time.GetUtcNow()), ct);
        }
    }

    /// <summary>Fixa na redação o perfil ativo do vestibular e tipo e passa para <c>processing</c>.</summary>
    /// <returns><c>null</c> se outra execução pegou a redação antes.</returns>
    private async Task<GradingProfile?> StartAsync(Essay essay, CancellationToken ct)
    {
        var profile = await profiles.FindActiveAsync(essay.ExamId, essay.EssayTypeId, ct)
            ?? throw Errors.AiFailed("GRADING_PROFILE_MISSING");

        var started = await unitOfWork.InTransactionAsync(
            async () =>
            {
                if (!await essays.TryMoveAsync(essay.Id, EssayTransition.GradingStarted(profile.Id), ct))
                {
                    return false;
                }

                audit.Record(
                    "system",
                    "essay.status_changed",
                    "essay",
                    essay.Id,
                    new { from = "Queued", to = "Processing", gradingProfileId = profile.Id });
                return true;
            },
            ct);

        return started ? profile : null;
    }

    /// <summary>Só as versões apontadas pelo perfil; a proposta é a versão com que a redação foi enviada.</summary>
    private async Task<GradingLayers> LoadLayersAsync(Essay essay, GradingProfile profile, CancellationToken ct)
    {
        return new GradingLayers(
            await prompts.FindAsync(profile.GradingPromptId, ct) ?? throw Errors.AiFailed("PROMPT_MISSING"),
            await rubrics.FindAsync(profile.RubricVersionId, ct) ?? throw Errors.AiFailed("RUBRIC_MISSING"),
            await guidelines.FindAsync(profile.GuidelinesVersionId, ct) ?? throw Errors.AiFailed("GUIDELINES_MISSING"),
            await writingRules.FindAsync(profile.WritingRulesVersionId, ct) ?? throw Errors.AiFailed("WRITING_RULES_MISSING"),
            await exams.FindByIdAsync(essay.ExamId, ct) ?? throw Errors.AiFailed("EXAM_MISSING"),
            (await essayTypes.ListByExamAsync(essay.ExamId, ct)).FirstOrDefault(type => type.Id == essay.EssayTypeId)
                ?? throw Errors.AiFailed("ESSAY_TYPE_MISSING"),
            await proposals.FindAsync(essay.PromptId, ct) ?? throw Errors.AiFailed("PROMPT_VERSION_MISSING"));
    }

    /// <summary>
    /// Chama a IA até ter uma correção válida. Resposta cortada no <c>max_tokens</c> nunca é completada: a nova
    /// chamada vai com o dobro do tamanho e, cortada de novo, <c>AI_OUTPUT_TRUNCATED</c>. Resposta fora das regras
    /// ganha mais uma chamada e, repetida, <c>AI_INVALID_RESPONSE</c>. Toda chamada respeita o limite rígido de gasto.
    /// </summary>
    private async Task<Correction> GradeAsync(Guid essayId, GradingContext context, GradingRequest request, CancellationToken ct)
    {
        var (invalidResponses, truncatedResponses) = (0, 0);
        var inputTokens = AiSpending.EstimateInputTokens(request.System, request.Proposal, request.Essay);
        while (true)
        {
            // O tamanho pedido diminui se a chamada, no pior caso, não couber no que falta do limite rígido.
            var sent = request with
            {
                MaxTokens = await spending.MaxTokensWithinCapAsync(essayId, request.Model, inputTokens, request.MaxTokens, ct),
            };

            var call = await spending.CallAsync(essayId, AiUsage.Grading, sent.Model, () => grader.GradeAsync(sent, ct), ct);
            await spending.RecordAsync(essayId, AiUsage.Grading, call.Model, call.Tokens, ct);

            if (call.Truncated)
            {
                truncatedResponses++;
                audit.Record(
                    "system", "grading.response_truncated", "essay", essayId, new { attempt = truncatedResponses, maxTokens = sent.MaxTokens });
                await unitOfWork.SaveChangesAsync(ct);
                if (truncatedResponses >= TruncatedResponseAttempts)
                {
                    throw Errors.AiFailed("AI_OUTPUT_TRUNCATED");
                }

                request = request with { MaxTokens = budget.Doubled(sent.MaxTokens) };
                continue;
            }

            int rule;
            if (GradingResponseParser.Parse(call.ToolInput) is not { } output)
            {
                rule = 1;
            }
            else
            {
                try
                {
                    var correction = Correction.Grade(context, output, call.Model, call.ToolInput!, time.GetUtcNow());
                    if (correction.Marks.Count < output.Marks.Count)
                    {
                        // Marcas repetidas ou acima do teto: a correção vale, mas o corte fica registrado.
                        audit.Record(
                            "system",
                            "grading.marks_trimmed",
                            "essay",
                            essayId,
                            new { received = output.Marks.Count, kept = correction.Marks.Count });
                    }

                    return correction;
                }
                catch (InvalidGradingException invalid)
                {
                    rule = invalid.Rule;
                }
            }

            // Só o número da regra: o motivo pode citar o texto da correção.
            invalidResponses++;
            audit.Record("system", "grading.response_rejected", "essay", essayId, new { attempt = invalidResponses, rule });
            await unitOfWork.SaveChangesAsync(ct);
            if (invalidResponses >= InvalidResponseAttempts)
            {
                throw Errors.AiFailed("AI_INVALID_RESPONSE");
            }
        }
    }
}
