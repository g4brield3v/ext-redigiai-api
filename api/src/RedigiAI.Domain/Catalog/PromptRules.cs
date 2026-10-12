using System.Text.RegularExpressions;
using RedigiAI.Domain.Common;

namespace RedigiAI.Domain.Catalog;

/// <summary>
/// Validação das propostas (docs/07-propostas-de-redacao.md, A4), em duas partes: o que impede de guardar até um
/// rascunho (<see cref="Storage"/>: limites de tamanho, posições, marcações proibidas) e o que falta para publicar
/// (<see cref="Publication"/>). Os caminhos (<c>blocks[2].data.source</c>) seguem o JSON do cadastro, com os blocos
/// na ordem de posição.
/// </summary>
public static partial class PromptRules
{
    public const int MaxBlocks = 40;
    public const int MaxTextLength = 6_000;
    public const int MaxTotalLength = 30_000;
    public const int MinTitleLength = 3;
    public const int MaxTitleLength = 120;
    public const int MaxSlugLength = 80;
    public const int MaxSummaryLength = 300;
    public const int MaxSourceLabelLength = 200;
    public const int MinGenreLength = 3;
    public const int MaxGenreLength = 80;
    public const int MaxColumns = 4;
    public const int MaxAltLength = 300;
    public const int MinAiDescriptionLength = 40;
    public const int MaxAiDescriptionLength = 3_000;

    /// <summary>Texto como é guardado: quebras de linha uniformes, sem espaço nas pontas; vazio vira ausente.</summary>
    public static string? Clean(string? text)
    {
        var cleaned = text?.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        return string.IsNullOrEmpty(cleaned) ? null : cleaned;
    }

    /// <summary>O que impede de guardar o conteúdo, mesmo como rascunho.</summary>
    public static IReadOnlyList<ValidationIssue> Storage(PromptContent content)
    {
        var issues = new List<ValidationIssue>();

        if (string.IsNullOrWhiteSpace(content.Slug))
        {
            issues.Add(new ValidationIssue("slug", "Informe o endereço da proposta."));
        }
        else
        {
            CheckText(issues, "slug", content.Slug, MaxSlugLength);
        }

        CheckText(issues, "title", content.Title, MaxTitleLength);
        CheckText(issues, "summary", content.Summary, MaxSummaryLength);
        CheckText(issues, "sourceLabel", content.SourceLabel, MaxSourceLabelLength);
        CheckText(issues, "genreLabel", content.GenreLabel, MaxGenreLength);
        if (content.MinLines is < 0 || content.MaxLines is < 0)
        {
            issues.Add(new ValidationIssue("sheet", "O número de linhas não pode ser negativo."));
        }

        if (content.Blocks.Count > MaxBlocks)
        {
            issues.Add(new ValidationIssue("blocks", $"Uma proposta pode ter até {MaxBlocks} blocos."));
        }

        issues.AddRange(Positions([.. content.Blocks.Select(block => block.Position)]));

        var ordered = content.Blocks.OrderBy(block => block.Position).ToList();
        var total = (Clean(content.Title)?.Length ?? 0) + (Clean(content.Summary)?.Length ?? 0);
        for (var index = 0; index < ordered.Count; index++)
        {
            var block = ordered[index];
            if (!Enum.IsDefined(block.Kind))
            {
                issues.Add(new ValidationIssue($"blocks[{index}].kind", "Tipo de bloco desconhecido."));
                continue;
            }

            var data = PromptBlockData.For(block.Kind, block.Data);
            if (data.Columns is { Count: > MaxColumns })
            {
                issues.Add(new ValidationIssue($"blocks[{index}].data.columns", $"Um texto em verso pode ter até {MaxColumns} colunas."));
            }

            foreach (var text in data.Texts())
            {
                total += text.Length;
            }

            var tooLong = data.Texts().Any(text => text.Length > MaxTextLength);
            var hasMarkup = data.Texts().Any(HasForbiddenMarkup);
            if (tooLong)
            {
                issues.Add(new ValidationIssue($"blocks[{index}].data", $"Cada texto pode ter até {Thousands(MaxTextLength)} caracteres."));
            }

            if (hasMarkup)
            {
                issues.Add(new ValidationIssue($"blocks[{index}].data", ForbiddenMarkupMessage));
            }
        }

        if (total > MaxTotalLength)
        {
            issues.Add(new ValidationIssue("blocks", $"A proposta inteira pode ter até {Thousands(MaxTotalLength)} caracteres."));
        }

        string?[] notes = [content.Notes.ExpectedFocus, content.Notes.TangentSignals, content.Notes.OffTopicSignals, content.Notes.Extra];
        string[] paths = ["expectedFocus", "tangentSignals", "offTopicSignals", "extra"];
        for (var index = 0; index < notes.Length; index++)
        {
            CheckText(issues, $"gradingNotes.{paths[index]}", notes[index], MaxTextLength);
        }

        var keyData = content.Notes.KeyData ?? [];
        if (keyData.Count > MaxBlocks || keyData.Any(item => item is { Length: > MaxTextLength } || HasForbiddenMarkup(item)))
        {
            issues.Add(new ValidationIssue(
                "gradingNotes.keyData", $"Até {MaxBlocks} dados-chave, cada um com até {Thousands(MaxTextLength)} caracteres, sem os sinais < e >."));
        }

        return issues;
    }

    /// <summary>Posições inteiras, a partir de zero, sem repetição.</summary>
    public static IReadOnlyList<ValidationIssue> Positions(IReadOnlyList<int> positions)
    {
        var issues = new List<ValidationIssue>();
        if (positions.Any(position => position < 0))
        {
            issues.Add(new ValidationIssue("blocks", "A posição de um bloco não pode ser negativa."));
        }

        if (positions.Distinct().Count() != positions.Count)
        {
            issues.Add(new ValidationIssue("blocks", "Dois blocos não podem ter a mesma posição."));
        }

        return issues;
    }

    /// <summary>O que falta para publicar.</summary>
    public static IReadOnlyList<ValidationIssue> Publication(Prompt prompt, PublicationContext context)
    {
        var issues = new List<ValidationIssue>();

        if (prompt.Title.Length is < MinTitleLength or > MaxTitleLength)
        {
            issues.Add(new ValidationIssue("title", $"Título da proposta: de {MinTitleLength} a {MaxTitleLength} caracteres."));
        }

        if (!SlugPattern().IsMatch(prompt.Slug))
        {
            issues.Add(new ValidationIssue("slug", "Endereço: só letras minúsculas, números e hífen."));
        }

        if (prompt.EssayTypeId is null)
        {
            issues.Add(new ValidationIssue("essayType", "Instruções: escolha a categoria de correção."));
        }
        else if (!context.EssayTypeHasActiveProfile)
        {
            issues.Add(new ValidationIssue("essayType", "Instruções: essa categoria não tem perfil de correção ativo neste vestibular."));
        }

        if (prompt.GenreLabel is not { Length: >= MinGenreLength and <= MaxGenreLength })
        {
            issues.Add(new ValidationIssue(
                "genreLabel", $"Instruções: escreva o tipo de redação como na prova, com {MinGenreLength} a {MaxGenreLength} caracteres."));
        }

        CheckSheet(issues, prompt, context);
        CheckBlocks(issues, prompt, context);

        if (prompt.GradingNotes.ExpectedFocus.Length == 0)
        {
            issues.Add(new ValidationIssue("gradingNotes.expectedFocus", "Abordagens esperadas: \"Recorte esperado\" obrigatório."));
        }

        if (prompt.GradingNotes.TangentSignals.Length == 0)
        {
            issues.Add(new ValidationIssue("gradingNotes.tangentSignals", "Abordagens esperadas: \"Sinais de tangenciamento\" obrigatório."));
        }

        return issues;
    }

    private static void CheckSheet(List<ValidationIssue> issues, Prompt prompt, PublicationContext context)
    {
        if (prompt.SheetLinesMin is not { } min || prompt.SheetLinesMax is not { } max)
        {
            issues.Add(new ValidationIssue("sheet", "Instruções: informe o mínimo e o máximo de linhas."));
        }
        else if (min < 1 || min > max)
        {
            issues.Add(new ValidationIssue("sheet.minLines", "Instruções: o mínimo de linhas vai de 1 até o máximo."));
        }
        else if (max > context.ExamSheetLines)
        {
            issues.Add(new ValidationIssue(
                "sheet.maxLines", $"Instruções: o máximo de linhas não passa das {context.ExamSheetLines} linhas da folha do vestibular."));
        }
    }

    private static void CheckBlocks(List<ValidationIssue> issues, Prompt prompt, PublicationContext context)
    {
        var blocks = prompt.Blocks;
        if (blocks.All(block => block.Kind != PromptBlockKind.CollectionItem))
        {
            issues.Add(new ValidationIssue("blocks", "Adicione ao menos um texto da coletânea."));
        }

        if (blocks.All(block => block.Kind != PromptBlockKind.Command))
        {
            issues.Add(new ValidationIssue("blocks", "Adicione o comando (enunciado) da proposta."));
        }

        var instructions = blocks.Count(block => block.Kind == PromptBlockKind.Instructions);
        if (instructions != 1)
        {
            issues.Add(new ValidationIssue(
                "blocks", instructions == 0 ? "Adicione o bloco de instruções." : "A proposta tem exatamente um bloco de instruções."));
        }

        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < blocks.Count; index++)
        {
            var (block, path) = (blocks[index], $"blocks[{index}].data");
            var data = block.Data;
            switch (block.Kind)
            {
                case PromptBlockKind.CollectionItem:
                    CheckCollectionItem(issues, data, path, labels, context);
                    break;
                case PromptBlockKind.Command when data.Body is null:
                    issues.Add(new ValidationIssue($"{path}.body", "Comando: escreva o enunciado."));
                    break;
                case PromptBlockKind.Paragraph when data.Body is null:
                    issues.Add(new ValidationIssue($"{path}.body", "Parágrafo: escreva o texto ou remova o bloco."));
                    break;
                case PromptBlockKind.Heading when data.Text is null:
                    issues.Add(new ValidationIssue($"{path}.text", "Título de seção: escreva o título ou remova o bloco."));
                    break;
                case PromptBlockKind.Instructions when data.Title is null:
                    issues.Add(new ValidationIssue($"{path}.title", "Instruções: escreva o título da seção."));
                    break;
            }
        }
    }

    private static void CheckCollectionItem(
        List<ValidationIssue> issues, PromptBlockData data, string path, HashSet<string> labels, PublicationContext context)
    {
        var name = data.Label ?? "Texto da coletânea";
        if (data.Label is null)
        {
            issues.Add(new ValidationIssue($"{path}.label", "Texto da coletânea: rótulo obrigatório (\"Texto 1\")."));
        }
        else if (!labels.Add(data.Label))
        {
            issues.Add(new ValidationIssue($"{path}.label", $"{name}: já existe outro texto com esse rótulo."));
        }

        if (data.Source is null)
        {
            issues.Add(new ValidationIssue($"{path}.source", $"{name}: fonte obrigatória."));
        }

        switch (data.Format)
        {
            case CollectionFormat.Prose when data.Body is null:
                issues.Add(new ValidationIssue($"{path}.body", $"{name}: cole o texto."));
                break;
            case CollectionFormat.Verse when data.Columns is not { Count: >= 1 and <= MaxColumns }
                || data.Columns.Any(column => column.All(string.IsNullOrWhiteSpace)):
                issues.Add(new ValidationIssue($"{path}.columns", $"{name}: de 1 a {MaxColumns} colunas, cada uma com ao menos um verso."));
                break;
            case CollectionFormat.Image:
                if (data.AssetId is not { } assetId || !context.PromptImageAssetIds.Contains(assetId))
                {
                    issues.Add(new ValidationIssue($"{path}.assetId", $"{name}: envie a imagem."));
                }

                if (data.Alt is not { Length: <= MaxAltLength })
                {
                    issues.Add(new ValidationIssue($"{path}.alt", $"{name}: texto alternativo obrigatório, com até {MaxAltLength} caracteres."));
                }

                if (data.AiDescription is not { Length: >= MinAiDescriptionLength and <= MaxAiDescriptionLength })
                {
                    issues.Add(new ValidationIssue(
                        $"{path}.aiDescription",
                        $"{name}: descrição para a IA obrigatória (mínimo de {MinAiDescriptionLength} caracteres, máximo de {Thousands(MaxAiDescriptionLength)})."));
                }

                break;
            case not null when !CollectionFormat.IsKnown(data.Format):
            case null:
                issues.Add(new ValidationIssue($"{path}.format", $"{name}: escolha o formato (texto, verso ou imagem)."));
                break;
        }
    }

    private const string ForbiddenMarkupMessage =
        "Os sinais < e > não são aceitos. Use só **negrito**, *itálico* e quebra de linha.";

    private static bool HasForbiddenMarkup(string? text) => text is not null && text.AsSpan().IndexOfAny('<', '>') >= 0;

    private static void CheckText(List<ValidationIssue> issues, string path, string? text, int maxLength)
    {
        if (Clean(text) is not { } cleaned)
        {
            return;
        }

        if (cleaned.Length > maxLength)
        {
            issues.Add(new ValidationIssue(path, $"Até {Thousands(maxLength)} caracteres."));
        }

        if (HasForbiddenMarkup(cleaned))
        {
            issues.Add(new ValidationIssue(path, ForbiddenMarkupMessage));
        }
    }

    /// <summary>Número com ponto de milhar, como se escreve em português, em qualquer configuração do servidor.</summary>
    private static string Thousands(int value) => value.ToString("#,0", PtBr);

    private static readonly System.Globalization.NumberFormatInfo PtBr = new() { NumberGroupSeparator = "." };

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();
}
