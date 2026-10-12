using RedigiAI.Domain.Catalog;
using RedigiAI.Domain.Grading;

namespace RedigiAI.UnitTests.Grading;

public class KnowledgeBaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid ExamId = Guid.CreateVersion7();

    private static readonly PromptTemplate Transcription = PromptTemplate.Create(PromptKind.Transcription, 1, "transcreva", null, Now);
    private static readonly PromptTemplate Grading = PromptTemplate.Create(PromptKind.Grading, 1, "corrija", "<redacao>", Now);

    [Fact]
    public void Profile_starts_as_draft_and_is_activated_and_retired()
    {
        var profile = Draft(Transcription, Grading);
        Assert.Equal((GradingProfileStatus.Draft, (DateTimeOffset?)null), (profile.Status, profile.ActivatedAt));
        Assert.Throws<InvalidOperationException>(profile.Retire);

        profile.Activate(Now);
        Assert.Equal((GradingProfileStatus.Active, (DateTimeOffset?)Now), (profile.Status, profile.ActivatedAt));

        profile.Retire();
        Assert.Equal(GradingProfileStatus.Retired, profile.Status);

        // Voltar atrás é reativar o perfil aposentado.
        profile.Activate(Now.AddDays(1));
        Assert.Equal(GradingProfileStatus.Active, profile.Status);
    }

    [Fact]
    public void Profile_refuses_instructions_of_the_wrong_kind()
    {
        Assert.Throws<ArgumentException>(() => Draft(Grading, Transcription));
    }

    [Fact]
    public void Writing_rule_codes_and_guideline_criteria_are_unique()
    {
        var rule = new WritingRuleSpec("ACENTO", "Acentuação gráfica", "Regras vigentes.", "Saude", "Saúde");

        Assert.Throws<ArgumentException>(() => WritingRulesVersion.Create(1, [rule, rule], null, Now));
        Assert.Throws<ArgumentException>(() => GuidelinesVersion.Create(
            ExamId, 1, "regras", "orientação", [new CriterionGuideline(1, "a"), new CriterionGuideline(1, "b")], Now));

        var catalog = WritingRulesVersion.Create(1, [rule, rule with { Code = WritingRulesVersion.OtherCode }], null, Now);
        Assert.Equal([1, 2], catalog.Rules.Select(r => r.Position));
    }

    private static GradingProfile Draft(PromptTemplate transcription, PromptTemplate grading) =>
        GradingProfile.Draft(
            ExamId,
            Guid.CreateVersion7(),
            1,
            transcription,
            grading,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "claude-sonnet-5-5",
            "claude-opus-5-5",
            Now);
}
