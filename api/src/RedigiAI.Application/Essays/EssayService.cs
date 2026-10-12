using System.Text.Json;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Catalog;
using RedigiAI.Domain.Common;
using RedigiAI.Domain.Essays;
using RedigiAI.Domain.Grading;
using RedigiAI.Domain.Jobs;

namespace RedigiAI.Application.Essays;

public interface IEssayService
{
    /// <summary>
    /// Confirma o envio de uma ou várias redações: tudo ou nada. Reserva 1 correção por redação e cria os jobs
    /// de transcrição na mesma transação. Repetir a mesma chave devolve as redações já criadas.
    /// </summary>
    Task<BatchEssayResponseDto> SubmitBatchAsync(SubmitEssaysCommand command, CancellationToken ct);

    /// <summary>Redações do aluno para o painel, da mais recente para a mais antiga.</summary>
    Task<EssayListDto> ListAsync(string clerkUserId, string? status, string? cursor, CancellationToken ct);

    Task<EssayDetailDto> GetAsync(string clerkUserId, Guid essayId, CancellationToken ct);

    /// <summary>
    /// Recebe as palavras ilegíveis digitadas pelo aluno. Só vale em <c>awaiting_review</c>, antes do prazo e com
    /// todas preenchidas; a redação vai para a fila de correção.
    /// </summary>
    Task FillIllegibleAsync(
        string clerkUserId, Guid essayId, IReadOnlyList<IllegibleAnswer>? answers, CancellationToken ct);

    /// <summary>Endereço temporário (5 min) para o aluno ver a foto que enviou.</summary>
    Task<PhotoUrlDto> GetPhotoUrlAsync(string clerkUserId, Guid essayId, CancellationToken ct);

    /// <summary>Cancela as redações cuja revisão passou do prazo e devolve o crédito de cada uma.</summary>
    /// <returns>Quantas foram canceladas agora.</returns>
    Task<int> CancelExpiredReviewsAsync(CancellationToken ct);
}

internal sealed class EssayService(
    IUserRepository users,
    IEssayRepository essays,
    IEssayBatchRepository batches,
    IAssetRepository assets,
    ITranscriptionRepository transcriptions,
    ICorrectionRepository corrections,
    IPromptRepository prompts,
    IExamRepository exams,
    IEssayTypeRepository essayTypes,
    IRubricRepository rubrics,
    IGradingProfileRepository profiles,
    ICreditRepository credits,
    IPhotoStorage storage,
    IJobQueue jobs,
    IAuditLog audit,
    IAccessLog accessLog,
    IUnitOfWork unitOfWork,
    EssayOutcomes outcomes,
    TimeProvider time) : IEssayService
{
    private const int PageSize = 20;
    private const int TranscribeAttempts = 4;
    private const int GradeAttempts = 4;
    private const int ExpiredReviewsPerSweep = 100;
    private const int ContextLength = 30;
    private static readonly TimeSpan PhotoUrlLifetime = TimeSpan.FromMinutes(5);

    private static readonly Dictionary<string, EssayStatus> StatusNames = Enum.GetValues<EssayStatus>()
        .ToDictionary(status => JsonNamingPolicy.SnakeCaseLower.ConvertName(status.ToString()));

    public async Task<BatchEssayResponseDto> SubmitBatchAsync(SubmitEssaysCommand command, CancellationToken ct)
    {
        var user = await users.FindByClerkIdAsync(command.ClerkUserId, ct) ?? throw Errors.ProfileNotFound();

        var key = command.IdempotencyKey?.Trim();
        if (string.IsNullOrEmpty(key) || key.Length > EssayBatch.MaxKeyLength || key.Any(char.IsControl))
        {
            throw Errors.Validation("Não foi possível confirmar o envio. Atualize a página e tente de novo.");
        }

        if (await batches.FindEssayIdsAsync(user.Id, key, ct) is { } alreadySent)
        {
            return new BatchEssayResponseDto(alreadySent);
        }

        var items = command.Essays ?? [];
        if (items.Count is < 1 or > EssayBatch.MaxEssays)
        {
            throw Errors.Validation($"Envie de 1 a {EssayBatch.MaxEssays} redações por vez.");
        }

        if (user.AiNoticeAcceptedAt is null && !command.AiNoticeAccepted)
        {
            throw Errors.Validation("Confirme que você leu o aviso sobre a correção por IA para enviar.");
        }

        // Tudo o que depende de leitura ou de serviço externo vem antes da transação.
        var prepared = new List<PreparedEssay>(items.Count);
        foreach (var item in items)
        {
            prepared.Add(await PrepareAsync(user.Id, item, prepared, ct));
        }

        try
        {
            return await unitOfWork.InTransactionAsync(() => ReserveAndCreateAsync(user, key, prepared, command.Origin, ct), ct);
        }
        catch (UniqueConflictException)
        {
            // Outra requisição com a mesma chave chegou primeiro: vale o envio dela.
            var winner = await batches.FindEssayIdsAsync(user.Id, key, ct) ?? throw Errors.InvalidFile();
            return new BatchEssayResponseDto(winner);
        }
    }

    public async Task<EssayListDto> ListAsync(string clerkUserId, string? status, string? cursor, CancellationToken ct)
    {
        var user = await users.FindByClerkIdAsync(clerkUserId, ct) ?? throw Errors.ProfileNotFound();

        EssayStatus? filter = null;
        if (!string.IsNullOrEmpty(status))
        {
            filter = StatusNames.TryGetValue(status, out var parsed)
                ? parsed
                : throw Errors.Validation("Filtro de situação inválido.");
        }

        Guid? before = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            before = Guid.TryParseExact(cursor, "N", out var id)
                ? id
                : throw Errors.Validation("Não foi possível continuar a lista. Atualize a página.");
        }

        // Uma a mais do que a página, só para saber se existe a próxima.
        var found = await essays.ListByUserAsync(user.Id, filter, before, PageSize + 1, ct);
        var page = found.Take(PageSize).ToList();
        var ids = page.Select(e => e.Id).ToList();
        var rewritten = await essays.RewrittenAsync(ids, ct);
        var illegible = await transcriptions.IllegibleCountsAsync(ids, ct);
        var scores = await corrections.ScoresAsync(ids, ct);
        var references = await prompts.ReferencesAsync([.. page.Select(e => e.PromptId).Distinct()], ct);

        return new EssayListDto(
            [.. page.Select(e => ToSummary(e, references, rewritten.Contains(e.Id), illegible.GetValueOrDefault(e.Id), scores))],
            found.Count > PageSize ? page[^1].Id.ToString("N") : null);
    }

    public async Task<EssayDetailDto> GetAsync(string clerkUserId, Guid essayId, CancellationToken ct)
    {
        var user = await users.FindByClerkIdAsync(clerkUserId, ct) ?? throw Errors.ProfileNotFound();
        var essay = await essays.FindAsync(essayId, user.Id, ct) ?? throw Errors.EssayNotFound();
        var exam = await exams.FindByIdAsync(essay.ExamId, ct) ?? throw Errors.Internal();
        var rewritten = await essays.RewrittenAsync([essay.Id], ct);
        var transcription = await transcriptions.FindByEssayAsync(essay.Id, ct);
        var summary = ToSummary(
            essay,
            await prompts.ReferencesAsync([essay.PromptId], ct),
            rewritten.Contains(essay.Id),
            transcription?.IllegibleCount ?? 0,
            await corrections.ScoresAsync([essay.Id], ct));
        var lines = transcription?.Lines ?? [];
        var textByLine = lines.ToDictionary(line => line.Number, line => line.Text);

        return new EssayDetailDto(
            summary.Id,
            summary.Title,
            summary.PromptId,
            summary.PromptSlug,
            summary.PromptVersion,
            summary.EssayTypeId,
            summary.Status,
            summary.Version,
            summary.HasVersions,
            summary.CreatedAt,
            summary.ReviewDeadlineAt,
            summary.IllegibleCount,
            summary.Score,
            summary.MaxScore,
            summary.FailureCode,
            exam.SheetLines,
            [.. lines.Select(line => new TranscriptionLineDto(line.Number, line.Text, line.Indent))],
            [.. (transcription?.Words ?? [])
                .OrderBy(word => word.LineNumber)
                .ThenBy(word => word.CharStart)
                .Select(word => ToDto(word, textByLine[word.LineNumber]))]);
    }

    public async Task FillIllegibleAsync(
        string clerkUserId, Guid essayId, IReadOnlyList<IllegibleAnswer>? answers, CancellationToken ct)
    {
        var user = await users.FindByClerkIdAsync(clerkUserId, ct) ?? throw Errors.ProfileNotFound();
        var essay = await essays.FindAsync(essayId, user.Id, ct) ?? throw Errors.EssayNotFound();
        if (essay.Status != EssayStatus.AwaitingReview)
        {
            throw Errors.InvalidState();
        }

        var now = time.GetUtcNow();
        if (essay.ReviewDeadlineAt <= now)
        {
            // O prazo acabou antes de a varredura passar: cancela e devolve o crédito já.
            await outcomes.EndWithRefundAsync(essay.Id, EssayTransition.ReviewExpired(), ct);
            throw Errors.ReviewExpired();
        }

        var transcription = await transcriptions.FindByEssayAsync(essay.Id, ct) ?? throw Errors.Internal();
        var typed = (answers ?? []).GroupBy(answer => answer.WordId).ToDictionary(group => group.Key, group => group.Last().Value);
        if (typed.Count != transcription.Words.Count || transcription.Words.Any(word => !typed.ContainsKey(word.Id)))
        {
            throw Errors.ReviewIncomplete();
        }

        await unitOfWork.InTransactionAsync(
            async () =>
            {
                foreach (var word in transcription.Words)
                {
                    word.Fill(typed[word.Id], now);
                }

                if (!await essays.TryMoveAsync(essay.Id, EssayTransition.ReviewCompleted(), ct))
                {
                    throw Errors.InvalidState();
                }

                jobs.Enqueue(JobTypes.Grade, new EssayJobPayload(essay.Id), GradeAttempts);
                audit.Record($"user:{user.Id}", "essay.status_changed", "essay", essay.Id, new { from = "AwaitingReview", to = "Queued" });
                return true;
            },
            ct);
    }

    public async Task<PhotoUrlDto> GetPhotoUrlAsync(string clerkUserId, Guid essayId, CancellationToken ct)
    {
        var user = await users.FindByClerkIdAsync(clerkUserId, ct) ?? throw Errors.ProfileNotFound();
        var essay = await essays.FindAsync(essayId, user.Id, ct) ?? throw Errors.EssayNotFound();
        var photo = await assets.FindAsync(essay.PhotoAssetId, user.Id, ct) ?? throw Errors.EssayNotFound();

        return new PhotoUrlDto(storage.CreateDownloadUrl(photo.PublicId, time.GetUtcNow(), PhotoUrlLifetime));
    }

    public async Task<int> CancelExpiredReviewsAsync(CancellationToken ct)
    {
        var cancelled = 0;
        foreach (var essayId in await essays.ListExpiredReviewsAsync(time.GetUtcNow(), ExpiredReviewsPerSweep, ct))
        {
            if (await outcomes.EndWithRefundAsync(essayId, EssayTransition.ReviewExpired(), ct))
            {
                cancelled++;
            }
        }

        return cancelled;
    }

    private async Task<PreparedEssay> PrepareAsync(
        Guid userId, BatchEssayItem item, List<PreparedEssay> previous, CancellationToken ct)
    {
        var prompt = await prompts.FindAsync(item.PromptId, ct) ?? throw Errors.PromptNotAvailable();

        Essay? rewriteOf = null;
        if (item.ParentEssayId is { } parentId)
        {
            rewriteOf = await essays.FindAsync(parentId, userId, ct) ?? throw Errors.EssayNotFound();

            // A reescrita usa a versão da proposta da redação original, mesmo que já exista uma versão mais nova.
            var original = await prompts.FindAsync(rewriteOf.PromptId, ct) ?? throw Errors.Internal();
            prompt = original.Slug == prompt.Slug ? original : throw Errors.EssayNotFound();
        }

        // O tipo e a grade vêm da proposta e do perfil de correção; o aluno não escolhe nenhum dos dois.
        var exam = await exams.FindByIdAsync(prompt.ExamId, ct) ?? throw Errors.PromptNotAvailable();
        var essayType = (await essayTypes.ListByExamAsync(exam.Id, ct)).FirstOrDefault(t => t.Id == prompt.EssayTypeId)
            ?? throw Errors.PromptNotAvailable();
        var rubricVersionId = (await profiles.FindActiveAsync(exam.Id, essayType.Id, ct))?.RubricVersionId
            ?? (await rubrics.FindLatestAsync(exam.Id, ct))?.Id
            ?? throw Errors.PromptNotAvailable();

        // Cada foto é uma redação: a mesma não entra duas vezes no envio nem em outro envio.
        var asset = await assets.FindAsync(item.AssetId, userId, ct);
        if (asset is null || asset.IsConfirmed || previous.Any(p => p.Photo.Id == asset.Id))
        {
            throw Errors.InvalidFile();
        }

        var stored = await storage.FindAsync(asset.PublicId, ct) ?? throw Errors.InvalidFile();

        return new PreparedEssay(exam, prompt, essayType, rubricVersionId, asset, stored, rewriteOf);
    }

    private async Task<BatchEssayResponseDto> ReserveAndCreateAsync(
        User user, string key, List<PreparedEssay> prepared, RequestOrigin origin, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var batch = EssayBatch.Create(user.Id, key, now);
        batches.Add(batch);

        var available = CreditBucket.InConsumptionOrder(await credits.ListAvailableAsync(user.Id, now, ct))
            .Select(bucket => new BucketBalance(bucket))
            .ToList();

        var ids = new List<Guid>(prepared.Count);
        foreach (var item in prepared)
        {
            var essay = Essay.Submit(
                user.Id, batch.Id, item.Exam, item.Prompt, item.EssayType, item.RubricVersionId, item.Photo, item.RewriteOf, now);
            item.Photo.Confirm(item.Stored.Bytes, item.Stored.Format);

            var bucket = await ReserveAsync(available, now, ct);

            essays.Add(essay);
            credits.AddEntry(CreditLedgerEntry.ForReservation(bucket, essay.Id, now));
            jobs.Enqueue(JobTypes.Transcribe, new EssayJobPayload(essay.Id), TranscribeAttempts);
            audit.Record($"user:{user.Id}", "essay.submitted", "essay", essay.Id, new { status = "uploaded", bucketId = bucket.Id });
            ids.Add(essay.Id);
        }

        user.AcceptAiNotice(now);
        // O registro de acesso é um por sessão de login; o envio em si fica na trilha de auditoria.
        await accessLog.RecordSessionOnceAsync(user.Id, origin, ct);

        return new BatchEssayResponseDto(ids);
    }

    /// <summary>Debita 1 correção do primeiro bucket que ainda tem saldo, na ordem de consumo.</summary>
    private async Task<CreditBucket> ReserveAsync(List<BucketBalance> available, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var balance in available)
        {
            if (balance.Remaining < 1)
            {
                continue;
            }

            if (await credits.TryDebitAsync(balance.Bucket.Id, now, ct))
            {
                balance.Remaining--;
                return balance.Bucket;
            }

            // Outro envio gastou esse saldo agora mesmo.
            balance.Remaining = 0;
        }

        throw Errors.QuotaExceeded();
    }

    private static IllegibleWordDto ToDto(IllegibleWord word, string lineText) =>
        new(
            word.Id,
            word.LineNumber,
            word.CharStart,
            word.CharEnd,
            lineText[Math.Max(0, word.CharStart - ContextLength)..word.CharStart],
            lineText[word.CharEnd..Math.Min(lineText.Length, word.CharEnd + ContextLength)]);

    private static EssaySummaryDto ToSummary(
        Essay essay,
        IReadOnlyDictionary<Guid, PromptReference> references,
        bool rewritten,
        int illegibleCount,
        IReadOnlyDictionary<Guid, (int Score, int MaxScore)> scores) =>
        new(
            essay.Id,
            essay.Title,
            essay.PromptId,
            references[essay.PromptId].Slug,
            references[essay.PromptId].Version,
            essay.EssayTypeId,
            essay.Status,
            essay.Version,
            HasVersions: essay.ParentEssayId is not null || rewritten,
            essay.CreatedAt,
            essay.ReviewDeadlineAt,
            illegibleCount,
            Score: scores.TryGetValue(essay.Id, out var graded) ? graded.Score : null,
            MaxScore: scores.TryGetValue(essay.Id, out graded) ? graded.MaxScore : null,
            essay.FailureCode);

    private sealed record PreparedEssay(
        Exam Exam, Prompt Prompt, EssayType EssayType, Guid RubricVersionId, Asset Photo, StoredPhoto Stored, Essay? RewriteOf);

    private sealed class BucketBalance(CreditBucket bucket)
    {
        public CreditBucket Bucket { get; } = bucket;

        public int Remaining { get; set; } = bucket.Remaining;
    }
}
