using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Catalog;

namespace RedigiAI.UnitTests.Catalog;

public class CatalogTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 15, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<int, string> Levels = new() { [1] = "um", [2] = "dois", [3] = "três", [4] = "quatro" };

    [Fact]
    public void Fuvest_rubric_max_score_is_40_with_four_levels()
    {
        var rubric = FuvestRubric(Guid.CreateVersion7());

        Assert.Equal((4, 40), (rubric.MaxLevel, rubric.MaxScore));
        Assert.True(rubric.HasLevel(4));
        Assert.False(rubric.HasLevel(5));
        Assert.False(rubric.HasLevel(0));
        Assert.Equal([1, 2, 3, 4], rubric.Criteria.Select(c => c.Position));
        Assert.Equal([12, 8, 12, 8], rubric.Criteria.Select(c => c.MaxPoints));
        Assert.Equal(9, rubric.Criteria[0].PointsFor(3));
    }

    [Fact]
    public void Criterion_needs_a_descriptor_for_each_level()
    {
        var incomplete = new Dictionary<int, string> { [1] = "um", [2] = "dois", [3] = "três" };

        Assert.Throws<ArgumentException>(() => RubricVersion.Publish(
            Guid.CreateVersion7(), 1, 4, Now, [], [new CriterionSpec("Gênero", 2, incomplete)]));
    }

    [Fact]
    public void Theme_is_published_only_with_an_accepted_type()
    {
        var examId = Guid.CreateVersion7();
        var theme = Theme.Draft(examId, "Algoritmos e o debate público", "Enunciado");

        Assert.Throws<InvalidOperationException>(() => theme.Publish(Now));

        var type = EssayType.Create(examId, "dissertacao", "Dissertação", "Texto dissertativo-argumentativo");
        theme.AcceptType(type, FuvestRubric(examId), "Defenda um ponto de vista.");
        theme.AcceptType(type, FuvestRubric(examId), "Defenda um ponto de vista.");
        theme.Publish(Now);

        Assert.Equal(ThemeStatus.Active, theme.Status);
        Assert.Equal("Defenda um ponto de vista.", Assert.Single(theme.AcceptedTypes).Instruction);
    }

    [Fact]
    public void Theme_refuses_type_from_another_exam()
    {
        var examId = Guid.CreateVersion7();
        var theme = Theme.Draft(examId, "Tema", "Enunciado");
        var foreignType = EssayType.Create(Guid.CreateVersion7(), "carta", "Carta", "Carta argumentativa");

        Assert.Throws<InvalidOperationException>(() => theme.AcceptType(foreignType, FuvestRubric(examId), "Instrução"));
    }

    [Fact]
    public void Theme_texts_are_numbered_in_order()
    {
        var theme = Theme.Draft(Guid.CreateVersion7(), "Tema", "Enunciado");
        theme.AddText(ThemeTextKind.Text, "A", "Fonte", "Trecho");
        theme.AddText(ThemeTextKind.Image, "B", "Fonte", null);

        Assert.Equal([1, 2], theme.Texts.Select(t => t.Position));
    }

    [Fact]
    public void Trial_grant_is_one_credit_without_expiration()
    {
        var userId = Guid.CreateVersion7();
        var bucket = CreditBucket.GrantTrial(userId, Now);
        var entry = CreditLedgerEntry.ForGrant(bucket, Now);

        Assert.Equal((1, 1, null), (bucket.Granted, bucket.Remaining, bucket.ExpiresAt));
        Assert.Equal(CreditSource.Trial, entry.Source);
        Assert.Equal(1, entry.Delta);
        Assert.Equal(bucket.Id, entry.BucketId);
        Assert.Equal(userId, entry.UserId);
    }

    private static RubricVersion FuvestRubric(Guid examId) =>
        RubricVersion.Publish(
            examId,
            1,
            4,
            Now,
            [new ZeroRule("full_copy", "Cópia integral da coletânea")],
            [
                new CriterionSpec("Tema, coletânea e autoria", 3, Levels),
                new CriterionSpec("Gênero e tipo de texto", 2, Levels),
                new CriterionSpec("Coesão, coerência e progressão", 3, Levels),
                new CriterionSpec("Convenções da escrita e vocabulário", 2, Levels),
            ]);
}
