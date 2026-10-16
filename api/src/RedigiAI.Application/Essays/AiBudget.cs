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

    /// <summary>
    /// Desligado, os jobs que chamam a IA (transcrição e correção) ficam parados na fila. É o padrão em
    /// desenvolvimento, para rodar a aplicação local sem gastar com a API.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Teto de referência por redação; passar dele só gera alerta.</summary>
    public decimal ReferenceCapUsd { get; set; } = 0.15m;

    /// <summary>Limite rígido: uma nova tentativa que não cabe nele não é feita.</summary>
    public decimal HardCapUsd { get; set; } = 0.33m;

    /// <summary>Preço por modelo (<c>Ai__Pricing__&lt;modelo&gt;__Input</c> etc.). Sem preço, o custo fica sem registro.</summary>
    public Dictionary<string, ModelPriceOptions> Pricing { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tamanho máximo da resposta da correção (<c>Ai__Grading__MaxTokens</c>).</summary>
    public AiCallOptions Grading { get; set; } = new() { MaxTokens = 32_000 };

    /// <summary>Tamanho máximo da resposta da transcrição (<c>Ai__Transcription__MaxTokens</c>).</summary>
    public AiCallOptions Transcription { get; set; } = new() { MaxTokens = 8_000 };

    /// <summary>
    /// O maior <c>max_tokens</c> que os modelos aceitam (128 mil no Opus 5.5 e no Sonnet 5.5): a nova tentativa
    /// depois de uma resposta cortada dobra o tamanho até aqui.
    /// </summary>
    public int MaxOutputTokens { get; set; } = 128_000;
}

/// <summary>
/// Teto de segurança da resposta de uma chamada, não meta: o uso típico da correção fica entre 3 e 6 mil tokens
/// (docs/08-ajustes-correcao.md, item 1).
/// </summary>
public sealed class AiCallOptions
{
    public int MaxTokens { get; set; }
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

    public int GradingMaxTokens => options.Value.Grading.MaxTokens;

    public int TranscriptionMaxTokens => options.Value.Transcription.MaxTokens;

    /// <summary>A nova tentativa depois de uma resposta cortada: o dobro, sem passar do que o modelo aceita.</summary>
    public int Doubled(int maxTokens) => (int)Math.Min((long)maxTokens * 2, options.Value.MaxOutputTokens);

    /// <summary>Uma nova chamada, estimada pela mais cara já feita para a redação, ainda cabe no limite rígido.</summary>
    /// <remarks>Só para modelo sem preço configurado, em que o pior caso da chamada não tem como ser calculado.</remarks>
    public bool AllowsAnotherCall(IReadOnlyList<long> costsSoFar) =>
        costsSoFar.Count == 0 || costsSoFar.Sum() + costsSoFar.Max() <= (long)(options.Value.HardCapUsd * MicroUsd);

    /// <summary>
    /// Quantos tokens de saída ainda cabem no limite rígido, contando a próxima chamada pelo pior caso: toda a
    /// entrada pelo preço mais alto de entrada (escrita no cache) e a saída inteira até o <c>max_tokens</c>.
    /// </summary>
    /// <returns><c>null</c> quando o modelo não tem preço configurado.</returns>
    public int? OutputTokensWithinCap(IReadOnlyList<long> costsSoFar, string model, int estimatedInputTokens)
    {
        if (!options.Value.Pricing.TryGetValue(model, out var price) || price.Output <= 0)
        {
            return null;
        }

        var left = (long)(options.Value.HardCapUsd * MicroUsd) - costsSoFar.Sum()
            - (long)Math.Ceiling(estimatedInputTokens * Math.Max(price.Input, price.CacheWrite));
        return left <= 0 ? 0 : (int)Math.Min(int.MaxValue, Math.Floor(left / price.Output));
    }
}
