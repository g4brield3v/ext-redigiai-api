namespace RedigiAI.Domain.Grading;

/// <summary>A nota de uma correção, para o gráfico do painel.</summary>
public sealed record ProgressScore(Guid EssayId, DateTimeOffset GradedAt, int Score, int MaxScore);

/// <summary>Um critério de uma correção sem nota zero, com os pontos que ele perdeu.</summary>
/// <param name="PointsLost">(nível máximo da grade − nível) × peso.</param>
/// <param name="Guidance">A orientação do critério; sem ela, a justificativa.</param>
public sealed record ProgressCriterion(int Position, string ShortName, int Weight, int PointsLost, string Guidance);

/// <summary>Uma correção sem nota zero, com os seus critérios.</summary>
public sealed record ProgressCorrection(Guid EssayId, IReadOnlyList<ProgressCriterion> Criteria);

/// <summary>O critério em que o aluno mais precisa melhorar, com a orientação e a redação de onde ela veio.</summary>
public sealed record ProgressFocus(int CriterionPosition, string CriterionName, string Guidance, Guid EssayId);

/// <summary>Regras do painel de progresso (especificação, seção 6, "Painel de progresso").</summary>
public static class Progress
{
    /// <summary>Quantas correções o gráfico mostra: as mais recentes.</summary>
    public const int ChartPoints = 10;

    /// <summary>Quantas correções sem nota zero o foco considera: as mais recentes.</summary>
    public const int FocusWindow = 3;

    /// <summary>Última nota menos a penúltima; <c>null</c> com menos de duas.</summary>
    /// <param name="chronological">As notas da mais antiga para a mais recente.</param>
    public static int? LastDelta(IReadOnlyList<ProgressScore> chronological) =>
        chronological.Count < 2 ? null : chronological[^1].Score - chronological[^2].Score;

    /// <summary>
    /// O critério com a maior soma de pontos perdidos nas correções dadas (empate: maior peso, depois menor posição),
    /// com a orientação da correção mais recente em que ele perdeu pontos. <c>null</c> sem correção ou sem perda.
    /// </summary>
    /// <param name="newestFirst">As correções sem nota zero, da mais recente para a mais antiga.</param>
    public static ProgressFocus? Focus(IReadOnlyList<ProgressCorrection> newestFirst)
    {
        var window = newestFirst.Take(FocusWindow).ToList();
        var chosen = window
            .SelectMany(correction => correction.Criteria)
            .GroupBy(criterion => criterion.Position)
            .Select(group => new { Position = group.Key, Lost = group.Sum(c => c.PointsLost), Weight = group.Max(c => c.Weight) })
            .Where(total => total.Lost > 0)
            .OrderByDescending(total => total.Lost)
            .ThenByDescending(total => total.Weight)
            .ThenBy(total => total.Position)
            .FirstOrDefault();
        if (chosen is null)
        {
            return null;
        }

        // A mais recente em que o critério perdeu pontos: existe, porque a soma dele é positiva.
        var source = window.First(correction => correction.Criteria.Any(c => c.Position == chosen.Position && c.PointsLost > 0));
        var criterion = source.Criteria.First(c => c.Position == chosen.Position);
        return new ProgressFocus(criterion.Position, criterion.ShortName, criterion.Guidance, source.EssayId);
    }
}
