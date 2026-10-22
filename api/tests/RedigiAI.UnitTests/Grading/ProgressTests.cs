using RedigiAI.Domain.Grading;

namespace RedigiAI.UnitTests.Grading;

/// <summary>Painel de progresso (especificação, seção 6, "Painel de progresso").</summary>
public class ProgressTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static ProgressScore Score(int score, int daysAgo = 0) => new(Guid.NewGuid(), Now.AddDays(-daysAgo), score, 40);

    /// <summary>Uma correção com os pontos perdidos por critério, na grade da Fuvest (pesos 3, 2, 3 e 2).</summary>
    private static ProgressCorrection Correction(int lost1, int lost2, int lost3, int lost4, Guid? essayId = null) => new(
        essayId ?? Guid.NewGuid(),
        [
            new(1, "Tema e autoria", 3, lost1, $"Orientação do critério 1 ({lost1})"),
            new(2, "Tipo e gênero", 2, lost2, $"Orientação do critério 2 ({lost2})"),
            new(3, "Coesão e progressão", 3, lost3, $"Orientação do critério 3 ({lost3})"),
            new(4, "Escrita e vocabulário", 2, lost4, $"Orientação do critério 4 ({lost4})"),
        ]);

    [Fact]
    public void Last_delta_is_the_last_score_minus_the_one_before()
    {
        Assert.Equal(2, Progress.LastDelta([Score(17, 3), Score(21, 2), Score(23, 1)]));
        Assert.Equal(-6, Progress.LastDelta([Score(29, 2), Score(23, 1)]));
        Assert.Equal(-23, Progress.LastDelta([Score(23, 2), Score(0, 1)]));
    }

    [Fact]
    public void Last_delta_needs_two_corrections()
    {
        Assert.Null(Progress.LastDelta([]));
        Assert.Null(Progress.LastDelta([Score(23)]));
    }

    [Fact]
    public void Focus_is_the_criterion_that_lost_most_points_in_the_last_three()
    {
        // Somas: critério 1 = 3 + 0 + 3 = 6; critério 3 = 3 + 6 + 0 = 9; critério 4 = 2 + 2 + 2 = 6.
        var newest = Correction(3, 0, 3, 2);
        var focus = Progress.Focus([newest, Correction(0, 0, 6, 2), Correction(3, 0, 0, 2)]);

        Assert.NotNull(focus);
        Assert.Equal((3, "Coesão e progressão", "Orientação do critério 3 (3)", newest.EssayId), (focus.CriterionPosition, focus.CriterionName, focus.Guidance, focus.EssayId));
    }

    [Fact]
    public void Only_the_three_most_recent_corrections_count()
    {
        // A quarta, mais antiga, perdeu muito no critério 4 e não entra.
        var focus = Progress.Focus([Correction(3, 0, 0, 0), Correction(3, 0, 0, 0), Correction(0, 0, 3, 0), Correction(0, 0, 0, 8)]);

        Assert.Equal(1, focus!.CriterionPosition);
    }

    [Fact]
    public void Guidance_comes_from_the_most_recent_correction_where_the_criterion_lost_points()
    {
        // O critério 2 não perdeu nada na mais recente: a orientação vem da seguinte.
        var middle = Correction(0, 4, 0, 0);
        var focus = Progress.Focus([Correction(0, 0, 0, 0), middle, Correction(0, 2, 0, 0)]);

        Assert.Equal((2, "Orientação do critério 2 (4)", middle.EssayId), (focus!.CriterionPosition, focus.Guidance, focus.EssayId));
    }

    [Fact]
    public void Tie_goes_to_the_heavier_criterion_then_to_the_lower_position()
    {
        // Critério 3 (peso 3) e critério 4 (peso 2) perderam 6 cada: ganha o de maior peso.
        Assert.Equal(3, Progress.Focus([Correction(0, 0, 6, 6)])!.CriterionPosition);

        // Critérios 1 e 3 (os dois de peso 3) perderam 3 cada: ganha a menor posição.
        Assert.Equal(1, Progress.Focus([Correction(3, 0, 3, 0)])!.CriterionPosition);
    }

    [Fact]
    public void Fewer_than_three_corrections_use_what_there_is()
    {
        Assert.Equal(4, Progress.Focus([Correction(0, 0, 0, 2)])!.CriterionPosition);
    }

    [Fact]
    public void No_focus_without_corrections_or_without_lost_points()
    {
        Assert.Null(Progress.Focus([]));
        Assert.Null(Progress.Focus([Correction(0, 0, 0, 0), Correction(0, 0, 0, 0)]));
    }
}
