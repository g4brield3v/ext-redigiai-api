using System.Text.Json;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Catalog;
using RedigiAI.Domain.Common;
using RedigiAI.Domain.Essays;
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
    ITranscriptionRepository transcriptions,
    IAiUsageRepository usages,
    IPhotoStorage storage,
    IEssayTranscriber transcriber,
    IJobQueue jobs,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    EssayOutcomes outcomes,
    AiBudget budget,
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
        var jpeg = await storage.DownloadForReadingAsync(photo.PublicId, ct);

        var transcription = await ReadAsync(essay, jpeg, exam.SheetLines, ct);
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
    private async Task<Transcription?> ReadAsync(Essay essay, byte[] jpeg, int sheetLines, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= InvalidResponseAttempts; attempt++)
        {
            if (!budget.AllowsAnotherCall(await usages.ListCostsAsync(essay.Id, ct)))
            {
                throw Errors.AiFailed("AI_BUDGET_EXCEEDED");
            }

            var call = await transcriber.TranscribeAsync(jpeg, sheetLines, ct);
            await RecordUsageAsync(essay.Id, call, ct);

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
                    essay.Id, sheetLines, reading.Lines, reading.IdentificationFound, call.Model, time.GetUtcNow());
            }
            catch (InvalidTranscriptionException)
            {
                // Linha fora da folha ou trecho fora do texto: vale pedir de novo.
            }
        }

        throw Errors.AiFailed("AI_INVALID_RESPONSE");
    }

    /// <summary>O consumo é gravado logo depois da chamada, mesmo que o resto do job falhe.</summary>
    private async Task RecordUsageAsync(Guid essayId, TranscriptionCall call, CancellationToken ct)
    {
        var cost = budget.CostOf(call.Model, call.Tokens);
        usages.Add(AiUsage.Record(
            essayId,
            AiUsage.Transcription,
            call.Model,
            call.Tokens.Input,
            call.Tokens.Output,
            call.Tokens.CacheRead,
            call.Tokens.CacheWrite,
            cost,
            time.GetUtcNow()));

        var spent = (await usages.ListCostsAsync(essayId, ct)).Sum() + (cost ?? 0);
        if (spent > budget.ReferenceCapMicroUsd)
        {
            audit.Record("system", "ai.cost_above_reference", "essay", essayId, new { spentMicroUsd = spent });
        }

        await unitOfWork.SaveChangesAsync(ct);
    }
}
