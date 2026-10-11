using System.Text.Json;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Catalog;
using RedigiAI.Domain.Common;
using RedigiAI.Domain.Essays;
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
}

internal sealed class EssayService(
    IUserRepository users,
    IEssayRepository essays,
    IEssayBatchRepository batches,
    IAssetRepository assets,
    IThemeRepository themes,
    IExamRepository exams,
    IEssayTypeRepository essayTypes,
    ICreditRepository credits,
    IPhotoStorage storage,
    IJobQueue jobs,
    IAuditLog audit,
    IAccessLog accessLog,
    IUnitOfWork unitOfWork,
    TimeProvider time) : IEssayService
{
    private const int PageSize = 20;
    private const int TranscribeAttempts = 4;

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
        var rewritten = await essays.RewrittenAsync([.. page.Select(e => e.Id)], ct);

        return new EssayListDto(
            [.. page.Select(e => ToSummary(e, rewritten.Contains(e.Id)))],
            found.Count > PageSize ? page[^1].Id.ToString("N") : null);
    }

    public async Task<EssayDetailDto> GetAsync(string clerkUserId, Guid essayId, CancellationToken ct)
    {
        var user = await users.FindByClerkIdAsync(clerkUserId, ct) ?? throw Errors.ProfileNotFound();
        var essay = await essays.FindAsync(essayId, user.Id, ct) ?? throw Errors.EssayNotFound();
        var exam = await exams.FindByIdAsync(essay.ExamId, ct) ?? throw Errors.Internal();
        var rewritten = await essays.RewrittenAsync([essay.Id], ct);
        var summary = ToSummary(essay, rewritten.Contains(essay.Id));

        // A transcrição e as palavras ilegíveis entram com o job transcribe.
        return new EssayDetailDto(
            summary.Id,
            summary.Title,
            summary.ThemeId,
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
            Lines: [],
            IllegibleWords: []);
    }

    private async Task<PreparedEssay> PrepareAsync(
        Guid userId, BatchEssayItem item, List<PreparedEssay> previous, CancellationToken ct)
    {
        var theme = await themes.FindActiveAsync(item.ThemeId, ct) ?? throw Errors.ThemeNotAvailable();
        var exam = await exams.FindByIdAsync(theme.ExamId, ct) ?? throw Errors.ThemeNotAvailable();
        var essayType = (await essayTypes.ListByExamAsync(exam.Id, ct)).FirstOrDefault(t => t.Id == item.EssayTypeId)
            ?? throw Errors.EssayTypeNotAccepted();

        // Cada foto é uma redação: a mesma não entra duas vezes no envio nem em outro envio.
        var asset = await assets.FindAsync(item.AssetId, userId, ct);
        if (asset is null || asset.IsConfirmed || previous.Any(p => p.Photo.Id == asset.Id))
        {
            throw Errors.InvalidFile();
        }

        var stored = await storage.FindAsync(asset.PublicId, ct) ?? throw Errors.InvalidFile();

        Essay? rewriteOf = null;
        if (item.ParentEssayId is { } parentId)
        {
            rewriteOf = await essays.FindAsync(parentId, userId, ct) ?? throw Errors.EssayNotFound();
        }

        return new PreparedEssay(exam, theme, essayType, asset, stored, rewriteOf);
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
            var essay = Essay.Submit(user.Id, batch.Id, item.Exam, item.Theme, item.EssayType, item.Photo, item.RewriteOf, now);
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

    private static EssaySummaryDto ToSummary(Essay essay, bool rewritten) =>
        new(
            essay.Id,
            essay.Title,
            essay.ThemeId,
            essay.EssayTypeId,
            essay.Status,
            essay.Version,
            HasVersions: essay.ParentEssayId is not null || rewritten,
            essay.CreatedAt,
            essay.ReviewDeadlineAt,
            IllegibleCount: 0,
            Score: null,
            MaxScore: null,
            essay.FailureCode);

    private sealed record PreparedEssay(
        Exam Exam, Theme Theme, EssayType EssayType, Asset Photo, StoredPhoto Stored, Essay? RewriteOf);

    private sealed class BucketBalance(CreditBucket bucket)
    {
        public CreditBucket Bucket { get; } = bucket;

        public int Remaining { get; set; } = bucket.Remaining;
    }
}
