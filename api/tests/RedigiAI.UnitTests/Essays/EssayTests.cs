using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Catalog;
using RedigiAI.Domain.Common;
using RedigiAI.Domain.Essays;
using RedigiAI.Domain.Jobs;

namespace RedigiAI.UnitTests.Essays;

public class EssayTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.CreateVersion7();

    private readonly Exam _exam = Exam.Create("fuvest", "Fuvest", ExamStatus.Active, sheetLines: 30, position: 1);
    private readonly EssayType _dissertacao;
    private readonly EssayType _narrativa;
    private readonly Theme _theme;

    public EssayTests()
    {
        _dissertacao = EssayType.Create(_exam.Id, "dissertacao", "Dissertação", "Texto dissertativo-argumentativo");
        _narrativa = EssayType.Create(_exam.Id, "narrativa", "Narrativa", "Texto narrativo com reflexão");
        _theme = Theme.Draft(_exam.Id, "Algoritmos e o debate público", "Enunciado");
        _theme.AcceptType(_dissertacao, Rubric(_exam.Id), "Defenda um ponto de vista.");
        _theme.Publish(Now);
    }

    [Fact]
    public void Submitted_essay_starts_uploaded_with_the_title_and_the_rubric_of_the_proposal()
    {
        var photo = Asset.Reserve(UserId, Now);

        var essay = Essay.Submit(UserId, Guid.CreateVersion7(), _exam, _theme, _dissertacao, photo, rewriteOf: null, Now);

        Assert.Equal(EssayStatus.Uploaded, essay.Status);
        Assert.Equal("Fuvest – Dissertação – Algoritmos e o debate público", essay.Title);
        Assert.Equal(_theme.AcceptedTypes[0].RubricVersionId, essay.RubricVersionId);
        Assert.Equal((1, (Guid?)null, photo.Id), (essay.Version, essay.ParentEssayId, essay.PhotoAssetId));
    }

    [Fact]
    public void Type_not_accepted_by_the_theme_is_refused()
    {
        var error = Assert.Throws<AppError>(() => Essay.Submit(
            UserId, Guid.CreateVersion7(), _exam, _theme, _narrativa, Asset.Reserve(UserId, Now), null, Now));

        Assert.Equal("ESSAY_TYPE_NOT_ACCEPTED", error.Code);
    }

    [Fact]
    public void Draft_theme_or_inactive_exam_is_not_available()
    {
        var draft = Theme.Draft(_exam.Id, "Rascunho", "Enunciado");
        var comingSoon = Exam.Create("enem", "Enem", ExamStatus.ComingSoon, sheetLines: 30, position: 2);

        Assert.Equal("THEME_NOT_AVAILABLE", Assert.Throws<AppError>(() => Essay.Submit(
            UserId, Guid.CreateVersion7(), _exam, draft, _dissertacao, Asset.Reserve(UserId, Now), null, Now)).Code);
        Assert.Equal("THEME_NOT_AVAILABLE", Assert.Throws<AppError>(() => Essay.Submit(
            UserId, Guid.CreateVersion7(), comingSoon, _theme, _dissertacao, Asset.Reserve(UserId, Now), null, Now)).Code);
    }

    [Fact]
    public void Photo_of_another_student_is_refused()
    {
        var error = Assert.Throws<AppError>(() => Essay.Submit(
            UserId, Guid.CreateVersion7(), _exam, _theme, _dissertacao, Asset.Reserve(Guid.CreateVersion7(), Now), null, Now));

        Assert.Equal("INVALID_FILE", error.Code);
    }

    [Fact]
    public void Rewrite_is_the_next_version_and_only_of_the_students_own_essay()
    {
        var first = Essay.Submit(UserId, Guid.CreateVersion7(), _exam, _theme, _dissertacao, Asset.Reserve(UserId, Now), null, Now);
        var stranger = Guid.CreateVersion7();
        var foreign = Essay.Submit(stranger, Guid.CreateVersion7(), _exam, _theme, _dissertacao, Asset.Reserve(stranger, Now), null, Now);

        var rewrite = Essay.Submit(UserId, Guid.CreateVersion7(), _exam, _theme, _dissertacao, Asset.Reserve(UserId, Now), first, Now);

        Assert.Equal((2, (Guid?)first.Id), (rewrite.Version, rewrite.ParentEssayId));
        Assert.Equal("ESSAY_NOT_FOUND", Assert.Throws<AppError>(() => Essay.Submit(
            UserId, Guid.CreateVersion7(), _exam, _theme, _dissertacao, Asset.Reserve(UserId, Now), foreign, Now)).Code);
    }

    [Theory]
    [InlineData("jpg", 1, true)]
    [InlineData("JPEG", Asset.MaxBytes, true)]
    [InlineData("jpg", Asset.MaxBytes + 1, false)]
    [InlineData("jpg", 0, false)]
    [InlineData("png", 1000, false)]
    [InlineData("pdf", 1000, false)]
    public void Photo_is_confirmed_only_as_a_jpg_of_up_to_10_mb(string format, long bytes, bool accepted)
    {
        var asset = Asset.Reserve(UserId, Now);
        Assert.Equal(Now.AddHours(24), asset.PurgeAfter);

        if (!accepted)
        {
            Assert.Equal("INVALID_FILE", Assert.Throws<AppError>(() => asset.Confirm(bytes, format)).Code);
            Assert.False(asset.IsConfirmed);
            return;
        }

        asset.Confirm(bytes, format);

        Assert.True(asset.IsConfirmed);
        Assert.Equal((bytes, "image/jpeg", (DateTimeOffset?)null), (asset.Bytes, asset.Mime, asset.PurgeAfter));
        // Uma foto, uma redação.
        Assert.Equal("INVALID_FILE", Assert.Throws<AppError>(() => asset.Confirm(bytes, format)).Code);
    }

    [Fact]
    public void Credits_are_spent_plan_first_then_trial_then_single()
    {
        var single = CreditBucket.Grant(UserId, CreditSource.Single, 2, expiresAt: null, Now);
        var trial = CreditBucket.GrantTrial(UserId, Now.AddMinutes(1));
        var planLater = CreditBucket.Grant(UserId, CreditSource.PlanWeek, 3, Now.AddDays(20), Now.AddMinutes(2));
        var planSooner = CreditBucket.Grant(UserId, CreditSource.PlanWeek, 3, Now.AddDays(5), Now.AddMinutes(3));

        var ordered = CreditBucket.InConsumptionOrder([single, trial, planLater, planSooner]);

        Assert.Equal([planSooner.Id, planLater.Id, trial.Id, single.Id], ordered.Select(b => b.Id));
    }

    [Fact]
    public void Reservation_is_a_negative_entry_for_the_essay()
    {
        var bucket = CreditBucket.GrantTrial(UserId, Now);
        var essayId = Guid.CreateVersion7();

        var entry = CreditLedgerEntry.ForReservation(bucket, essayId, Now);

        Assert.Equal((-1, bucket.Id, (Guid?)essayId, CreditSource.Trial), (entry.Delta, entry.BucketId, entry.EssayId, entry.Source));
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 120)]
    [InlineData(3, 480)]
    [InlineData(4, 1800)]
    [InlineData(9, 1800)]
    public void Job_waits_longer_after_each_failed_attempt(int attempts, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), Job.BackoffAfter(attempts));
    }

    [Fact]
    public void New_job_is_ready_to_run_now()
    {
        var job = Job.Create(JobTypes.Transcribe, """{"essayId":"x"}""", maxAttempts: 4, Now);

        Assert.Equal((JobStatus.Ready, 0, 4, Now), (job.Status, job.Attempts, job.MaxAttempts, job.RunAt));
    }

    private static RubricVersion Rubric(Guid examId)
    {
        var levels = new Dictionary<int, string> { [1] = "um", [2] = "dois", [3] = "três" };
        return RubricVersion.Publish(examId, 1, Now, [], [new CriterionSpec("Tema, coletânea e autoria", 3, levels)]);
    }
}
