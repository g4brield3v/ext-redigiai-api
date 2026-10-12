using Microsoft.Extensions.Options;
using RedigiAI.Application.Abstractions;

namespace RedigiAI.Application.Essays;

/// <summary>Preço de um modelo, em dólares por milhão de tokens.</summary>
public sealed class ModelPriceOptions
{
    public decimal Input { get; set; }
    public decimal Output { get; set; }
    public decimal CacheRead { get; set; }
    public decimal CacheWrite { get; set; }
}

public sealed class AiBudgetOptions
{
    public const string Section = "Ai";

    /// <summary>Teto de referência por redação; passar dele só gera alerta.</summary>
    public decimal ReferenceCapUsd { get; set; } = 0.15m;

    /// <summary>Limite rígido: uma nova tentativa que não cabe nele não é feita.</summary>
    public decimal HardCapUsd { get; set; } = 0.225m;

    /// <summary>Preço por modelo (<c>Ai__Pricing__&lt;modelo&gt;__Input</c> etc.). Sem preço, o custo fica sem registro.</summary>
    public Dictionary<string, ModelPriceOptions> Pricing { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Custo de cada chamada à IA e o teto de gasto por redação (especificação, seção 8).</summary>
internal sealed class AiBudget(IOptions<AiBudgetOptions> options)
{
    private const decimal MicroUsd = 1_000_000m;

    public long ReferenceCapMicroUsd => (long)(options.Value.ReferenceCapUsd * MicroUsd);

    /// <returns>Milionésimos de dólar; <c>null</c> quando o preço do modelo não está configurado.</returns>
    public long? CostOf(string model, AiTokens tokens)
    {
        if (!options.Value.Pricing.TryGetValue(model, out var price))
        {
            return null;
        }

        // Preço por milhão de tokens × tokens = milionésimos de dólar.
        return (long)Math.Ceiling(
            (tokens.Input * price.Input)
            + (tokens.Output * price.Output)
            + (tokens.CacheRead * price.CacheRead)
            + (tokens.CacheWrite * price.CacheWrite));
    }

    /// <summary>Uma nova chamada, estimada pela mais cara já feita para a redação, ainda cabe no limite rígido.</summary>
    public bool AllowsAnotherCall(IReadOnlyList<long> costsSoFar) =>
        costsSoFar.Count == 0 || costsSoFar.Sum() + costsSoFar.Max() <= (long)(options.Value.HardCapUsd * MicroUsd);
}
