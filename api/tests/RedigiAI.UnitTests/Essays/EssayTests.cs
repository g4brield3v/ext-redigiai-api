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
    private readonly Guid _rubricVersionId = Guid.CreateVersion7();
    private readonly Prompt _prompt;

    public EssayTests()
    {
        _dissertacao = EssayType.Create(_exam.Id, "dissertacao-em-prosa", "Dissertação em prosa", "Texto dissertativo-argumentativo");
        _narrativa = EssayType.Create(_exam.Id, "narrativa", "Narrativa", "Texto narrativo com reflexão");
        _prompt = Published("algoritmos");
    }

    private static readonly PublicationContext Context = new(true, new HashSet<Guid>());

    private Prompt Draft(string slug) => Prompt.Draft(
        new PromptContent(
            _exam.Id,
            slug,
            "Algoritmos e o debate público",
            null,
            null,
            _dissertacao.Id,
            [
                new(null, 10, PromptBlockKind.CollectionItem, new PromptBlockData { Label = "Texto 1", Format = CollectionFormat.Prose, Source = "Fonte", Body = "Trecho." }),
                new(null, 20, PromptBlockKind.Command, new PromptBlockData { Body = "Redija uma dissertação." }),
                new(null, 30, PromptBlockKind.Instructions, new PromptBlockData { Title = "Instruções" }),
            ],
            new PromptNotesContent("Recorte.", "Tangente.", null, null, null)),
        createdBy: null,
        Now);

    private Prompt Published(string slug)
    {
        var prompt = Draft(slug);
        prompt.Publish(Context, publishedBy: null, Now);
        return prompt;
    }

    private Essay Submit(Guid userId, Prompt prompt, Asset photo, Essay? rewriteOf = null, Exam? exam = null, EssayType? essayType = null) =>
        Essay.Submit(userId, Guid.CreateVersion7(), exam ?? _exam, prompt, essayType ?? _dissertacao, _rubricVersionId, photo, rewriteOf, Now);

    [Fact]
    public void Submitted_essay_starts_uploaded_with_the_title_and_the_type_of_the_prompt()
    {
        var photo = Asset.Reserve(UserId, Now);

        var essay = Submit(UserId, _prompt, photo);

        Assert.Equal(EssayStatus.Uploaded, essay.Status);
        // Vestibular, nome da categoria de correção (o tipo de redação) e título da proposta.
        Assert.Equal("Fuvest – Dissertação em prosa – Algoritmos e o debate público", essay.Title);
        Assert.Equal((_prompt.Id, _dissertacao.Id, _rubricVersionId), (essay.PromptId, essay.EssayTypeId, essay.RubricVersionId));
        Assert.Equal((1, (Guid?)null, photo.Id), (essay.Version, essay.ParentEssayId, essay.PhotoAssetId));
    }

    [Fact]
    public void Type_that_is_not_the_one_of_the_prompt_is_refused()
    {
        var error = Assert.Throws<AppError>(() => Submit(UserId, _prompt, Asset.Reserve(UserId, Now), essayType: _narrativa));

        Assert.Equal("PROMPT_NOT_AVAILABLE", error.Code);
    }

    [Fact]
    public void Draft_or_archived_prompt_or_inactive_exam_is_not_available()
    {
        var archived = Published("arquivada");
        archived.Archive(Now);
        var comingSoon = Exam.Create("enem", "Enem", ExamStatus.ComingSoon, sheetLines: 30, position: 2);

        Assert.Equal("PROMPT_NOT_AVAILABLE", Assert.Throws<AppError>(() => Submit(UserId, Draft("rascunho"), Asset.Reserve(UserId, Now))).Code);
        Assert.Equal("PROMPT_NOT_AVAILABLE", Assert.Throws<AppError>(() => Submit(UserId, archived, Asset.Reserve(UserId, Now))).Code);
        Assert.Equal(
            "PROMPT_NOT_AVAILABLE",
            Assert.Throws<AppError>(() => Submit(UserId, _prompt, Asset.Reserve(UserId, Now), exam: comingSoon)).Code);
    }

    [Fact]
    public void Photo_of_another_student_is_refused()
    {
        var error = Assert.Throws<AppError>(() => Submit(UserId, _prompt, Asset.Reserve(Guid.CreateVersion7(), Now)));

        Assert.Equal("INVALID_FILE", error.Code);
    }

    [Fact]
    public void Rewrite_is_the_next_version_and_only_of_the_students_own_essay()
    {
        var first = Submit(UserId, _prompt, Asset.Reserve(UserId, Now));
        var stranger = Guid.CreateVersion7();
        var foreign = Submit(stranger, _prompt, Asset.Reserve(stranger, Now));

        var rewrite = Submit(UserId, _prompt, Asset.Reserve(UserId, Now), first);

        Assert.Equal((2, (Guid?)first.Id), (rewrite.Version, rewrite.ParentEssayId));
        Assert.Equal("ESSAY_NOT_FOUND", Assert.Throws<AppError>(() => Submit(UserId, _prompt, Asset.Reserve(UserId, Now), foreign)).Code);
    }

    [Fact]
    public void Rewrite_uses_the_prompt_version_of_the_original_even_after_it_is_archived()
    {
        var first = Submit(UserId, _prompt, Asset.Reserve(UserId, Now));
        _prompt.Archive(Now.AddDays(1));
        var newer = _prompt.NewVersion(2, createdBy: null, Now.AddDays(1));
        newer.Publish(Context, publishedBy: null, Now.AddDays(1));

        var rewrite = Submit(UserId, _prompt, Asset.Reserve(UserId, Now), first);

        Assert.Equal((2, _prompt.Id), (rewrite.Version, rewrite.PromptId));
        // Com outra versão da proposta, não é uma reescrita daquela redação.
        Assert.Equal("ESSAY_NOT_FOUND", Assert.Throws<AppError>(() => Submit(UserId, newer, Asset.Reserve(UserId, Now), first)).Code);
        // E uma redação nova não entra numa versão arquivada.
        Assert.Equal("PROMPT_NOT_AVAILABLE", Assert.Throws<AppError>(() => Submit(UserId, _prompt, Asset.Reserve(UserId, Now))).Code);
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
        var planLater = CreditBucket.Grant(UserId, CreditSource.Plan, 3, Now.AddDays(20), Now.AddMinutes(2));
        var planSooner = CreditBucket.Grant(UserId, CreditSource.Plan, 3, Now.AddDays(5), Now.AddMinutes(3));

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
        return RubricVersion.Publish(examId, 1, 3, Now, [], [new CriterionSpec("Tema, coletânea e autoria", 3, levels)]);
    }
}
