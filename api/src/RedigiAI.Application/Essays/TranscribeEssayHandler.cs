using System.Text.Json;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Catalog;
using RedigiAI.Domain.Common;
using RedigiAI.Domain.Essays;
using RedigiAI.Domain.Grading;
using RedigiAI.Domain.Jobs;

namespace RedigiAI.Application.Essays;

/// <summary>
/// Job <c>transcribe</c>: lê a foto com a IA e decide o caminho da redação — fila de correção (nenhuma palavra
/// ilegível), revisão do aluno (1 a 10) ou falha com devolução do crédito (mais de 10 ou foto ilegível).
/// </summary>
internal sealed class TranscribeEssayHandler(
    IEssayRepository essays,
    IAssetRepository assets,
    IExamRepository exams,
    IGradingProfileRepository profiles,
    IPromptTemplateRepository prompts,
    ITranscriptionRepository transcriptions,
    IPhotoStorage storage,
    IEssayTranscriber transcriber,
    IJobQueue jobs,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    EssayOutcomes outcomes,
    AiSpending spending,
    AiBudget budget,
    TimeProvider time) : IJobHandler
{
    /// <summary>Resposta fora do formato: pede mais uma vez; repetida, a falha é definitiva.</summary>
    private const int InvalidResponseAttempts = 2;

    /// <summary>Resposta cortada no <c>max_tokens</c>: pede de novo com o dobro; cortada de novo, a falha é definitiva.</summary>
    private const int TruncatedResponseAttempts = 2;

    /// <summary>A foto da folha na entrada, estimada por cima (imagens grandes chegam perto de 5 mil tokens).</summary>
    private const int PhotoTokens = 5_000;
    private const int GradeAttempts = 4;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Type => JobTypes.Transcribe;

    public async Task HandleAsync(string payload, CancellationToken ct)
    {
        var essayId = JsonSerializer.Deserialize<EssayJobPayload>(payload, Json)!.EssayId;
        var essay = await essays.FindByIdAsync(essayId, ct);
        if (essay is not { Status: EssayStatus.Uploaded })
        {
            // Outra execução já transcreveu ou encerrou esta redação.
            return;
        }

        var photo = await assets.FindByIdAsync(essay.PhotoAssetId, ct) ?? throw Errors.AiFailed("PHOTO_MISSING");
        var exam = await exams.FindByIdAsync(essay.ExamId, ct) ?? throw Errors.AiFailed("EXAM_MISSING");

        // As instruções e o modelo vêm do perfil de correção ativo do vestibular e tipo, nunca do código.
        var profile = await profiles.FindActiveAsync(essay.ExamId, essay.EssayTypeId, ct)
            ?? throw Errors.AiFailed("GRADING_PROFILE_MISSING");
        var prompt = await prompts.FindAsync(profile.TranscriptionPromptId, ct) ?? throw Errors.AiFailed("PROMPT_MISSING");
        var request = new TranscriptionRequest(
            await storage.DownloadForReadingAsync(photo.PublicId, ct),
            exam.SheetLines,
            prompt.SystemText.Replace("{{sheet_lines}}", exam.SheetLines.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            profile.TranscriptionModel,
            budget.TranscriptionMaxTokens);

        var transcription = await ReadAsync(essay, request, ct);
        if (transcription is null || transcription.IsUnreadable)
        {
            await outcomes.EndWithRefundAsync(essay.Id, EssayTransition.PhotoUnreadable(), ct);
            return;
        }

        await unitOfWork.InTransactionAsync(
            async () =>
            {
                var transition = transcription.IllegibleCount == 0
                    ? EssayTransition.TranscribedClean()
                    : EssayTransition.NeedsReview(time.GetUtcNow());
                if (!await essays.TryMoveAsync(essay.Id, transition, ct))
                {
                    return false;
                }

                transcriptions.Add(transcription);
                if (transition.To == EssayStatus.Queued)
                {
                    jobs.Enqueue(JobTypes.Grade, new EssayJobPayload(essay.Id), GradeAttempts);
                }

                audit.Record(
                    "system",
                    "essay.status_changed",
                    "essay",
                    essay.Id,
                    new { from = transition.From.ToString(), to = transition.To.ToString(), illegible = transcription.IllegibleCount });
                return true;
            },
            ct);
    }

    public Task OnDeadAsync(string payload, CancellationToken ct)
    {
        var essayId = JsonSerializer.Deserialize<EssayJobPayload>(payload, Json)!.EssayId;
        return outcomes.EndWithRefundAsync(essayId, EssayTransition.TranscriptionFailed(), ct);
    }

    /// <summary>
    /// Chama a IA até ter uma leitura válida. Resposta cortada no <c>max_tokens</c> ganha uma nova chamada com o
    /// dobro do tamanho e, cortada de novo, <c>AI_OUTPUT_TRUNCATED</c>; resposta fora do formato ganha mais uma
    /// chamada e, repetida, <c>AI_INVALID_RESPONSE</c>.
    /// </summary>
    /// <returns><c>null</c> quando a IA diz que a foto não dá para ler.</returns>
    private async Task<Transcription?> ReadAsync(Essay essay, TranscriptionRequest request, CancellationToken ct)
    {
        var (invalidResponses, truncatedResponses) = (0, 0);
        var inputTokens = AiSpending.EstimateInputTokens(request.Instructions) + PhotoTokens;
        while (true)
        {
            // O tamanho pedido diminui se a chamada, no pior caso, não couber no que falta do limite rígido.
            var sent = request with
            {
                MaxTokens = await spending.MaxTokensWithinCapAsync(essay.Id, request.Model, inputTokens, request.MaxTokens, ct),
            };

            var call = await spending.CallAsync(
                essay.Id, AiUsage.Transcription, sent.Model, () => transcriber.TranscribeAsync(sent, ct), ct);
            await spending.RecordAsync(essay.Id, AiUsage.Transcription, call.Model, call.Tokens, ct);

            if (call.Truncated)
            {
                truncatedResponses++;
                audit.Record(
                    "system", "transcription.response_truncated", "essay", essay.Id, new { attempt = truncatedResponses, maxTokens = sent.MaxTokens });
                await unitOfWork.SaveChangesAsync(ct);
                if (truncatedResponses >= TruncatedResponseAttempts)
                {
                    throw Errors.AiFailed("AI_OUTPUT_TRUNCATED");
                }

                request = request with { MaxTokens = budget.Doubled(sent.MaxTokens) };
                continue;
            }

            if (call.Reading is { } reading)
            {
                if (!reading.Readable)
                {
                    return null;
                }

                try
                {
                    return Transcription.Create(
                        essay.Id, request.SheetLines, reading.Lines, reading.IdentificationFound, call.Model, time.GetUtcNow());
                }
                catch (InvalidTranscriptionException)
                {
                    // Linha fora da folha ou trecho fora do texto: vale pedir de novo.
                }
            }

            if (++invalidResponses >= InvalidResponseAttempts)
            {
                throw Errors.AiFailed("AI_INVALID_RESPONSE");
            }
        }
    }
}
