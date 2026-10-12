namespace RedigiAI.Application.Abstractions;

/// <summary>
/// O que a ferramenta de saída aceita, tirado só do perfil e do vestibular. Nada aqui depende da redação: a
/// definição da ferramenta vem antes de tudo na chamada e precisa ser igual para o cache valer.
/// </summary>
/// <param name="CriteriaCount">Critérios da grade, numerados de 1 até aqui.</param>
/// <param name="ZeroReasons">Códigos das hipóteses de nota zero da grade.</param>
/// <param name="SheetLines">Linhas da folha do vestibular.</param>
/// <param name="RuleCodes">Códigos do catálogo de regras de escrita.</param>
public sealed record GradingSchema(
    int CriteriaCount, int MaxLevel, IReadOnlyList<string> ZeroReasons, int SheetLines, IReadOnlyList<string> RuleCodes);

/// <summary>
/// As partes da chamada na ordem de cache: o que é igual para todo o vestibular e tipo, o que é igual para o
/// tema e, por último, a redação.
/// </summary>
/// <param name="System">Instruções, grade, manual do corretor e regras de escrita (camadas 1 a 4).</param>
/// <param name="Proposal">Enunciado, coletânea e notas do tema (camada 6).</param>
/// <param name="Essay">A redação, delimitada como dado (camada 7).</param>
/// <param name="Model">Modelo definido pelo perfil de correção.</param>
public sealed record GradingRequest(string System, string Proposal, string Essay, GradingSchema Schema, string Model);

/// <param name="ToolInput">
/// O JSON que a IA passou à ferramenta, como veio; nulo quando ela não usou a ferramenta ou a resposta foi
/// cortada. O consumo é cobrado mesmo assim.
/// </param>
public sealed record GradingCall(string? ToolInput, string Model, AiTokens Tokens);

/// <summary>Corrige a redação com a IA. Devolve só o que a IA respondeu; validar e calcular a nota é do backend.</summary>
public interface IEssayGrader
{
    /// <exception cref="Domain.Common.AppError">
    /// <c>AI_UNAVAILABLE</c> (retentável) para 429, 5xx, sobrecarga e tempo esgotado; erro não retentável quando a
    /// IA recusa o pedido.
    /// </exception>
    Task<GradingCall> GradeAsync(GradingRequest request, CancellationToken ct);
}
