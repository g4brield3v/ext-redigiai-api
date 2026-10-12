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
    TimeProvider time) : IJobHandler
{
    /// <summary>Resposta fora do formato: pede mais uma vez; repetida, a falha é definitiva.</summary>
    private const int InvalidResponseAttempts = 2;
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
            profile.TranscriptionModel);

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

    /// <returns><c>null</c> quando a IA diz que a foto não dá para ler.</returns>
    private async Task<Transcription?> ReadAsync(Essay essay, TranscriptionRequest request, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= InvalidResponseAttempts; attempt++)
        {
            await spending.EnsureRoomForAnotherCallAsync(essay.Id, ct);

            var call = await transcriber.TranscribeAsync(request, ct);
            await spending.RecordAsync(essay.Id, AiUsage.Transcription, call.Model, call.Tokens, ct);

            if (call.Reading is not { } reading)
            {
                continue;
            }

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

        throw Errors.AiFailed("AI_INVALID_RESPONSE");
    }
}
