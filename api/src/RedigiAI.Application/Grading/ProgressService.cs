using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Common;
using RedigiAI.Domain.Grading;

namespace RedigiAI.Application.Grading;

/// <summary>Uma correção no gráfico "Sua evolução".</summary>
/// <param name="Date">Data da correção.</param>
public sealed record ProgressPointDto(Guid EssayId, DateTimeOffset Date, int Score, int MaxScore);

/// <summary>"Seu foco agora": o critério que mais tirou pontos nas últimas correções sem nota zero.</summary>
/// <param name="CriterionName">O nome curto do critério na grade.</param>
/// <param name="Guidance">A orientação desse critério na correção mais recente em que ele perdeu pontos.</param>
/// <param name="EssayId">A redação dessa correção.</param>
public sealed record ProgressFocusDto(int CriterionPosition, string CriterionName, string Guidance, Guid EssayId);

/// <summary>O painel de progresso, já calculado: o front só exibe.</summary>
/// <param name="Points">As correções mais recentes (até 10), da mais antiga para a mais recente.</param>
/// <param name="LastDelta">Última nota menos a penúltima; nulo com menos de duas correções.</param>
/// <param name="Focus">Nulo sem correção sem nota zero ou se nenhum critério perdeu pontos.</param>
public sealed record ProgressDto(IReadOnlyList<ProgressPointDto> Points, int? LastDelta, ProgressFocusDto? Focus);

public interface IProgressService
{
    /// <summary>A evolução da nota e o foco de melhoria do aluno (especificação, seção 6, "Painel de progresso").</summary>
    /// <exception cref="AppError"><c>PROFILE_NOT_FOUND</c> antes do primeiro acesso.</exception>
    Task<ProgressDto> GetAsync(string clerkUserId, CancellationToken ct);
}

internal sealed class ProgressService(IUserRepository users, ICorrectionRepository corrections) : IProgressService
{
    public async Task<ProgressDto> GetAsync(string clerkUserId, CancellationToken ct)
    {
        var user = await users.FindByClerkIdAsync(clerkUserId, ct) ?? throw Errors.ProfileNotFound();

        var scores = (await corrections.RecentScoresAsync(user.Id, Progress.ChartPoints, ct)).Reverse().ToList();
        var focus = Progress.Focus(await corrections.RecentGradedAsync(user.Id, Progress.FocusWindow, ct));

        return new ProgressDto(
            [.. scores.Select(score => new ProgressPointDto(score.EssayId, score.GradedAt, score.Score, score.MaxScore))],
            Progress.LastDelta(scores),
            focus is null ? null : new ProgressFocusDto(focus.CriterionPosition, focus.CriterionName, focus.Guidance, focus.EssayId));
    }
}
