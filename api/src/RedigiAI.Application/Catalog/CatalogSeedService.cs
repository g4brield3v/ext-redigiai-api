using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Catalog;

namespace RedigiAI.Application.Catalog;

/// <summary>Carga inicial do catálogo (no MVP não há painel administrativo).</summary>
public interface ICatalogSeedService
{
    /// <summary>Idempotente: não faz nada se a Fuvest já existe.</summary>
    /// <returns><c>true</c> se o catálogo foi criado agora.</returns>
    Task<bool> SeedAsync(CancellationToken ct);
}

internal sealed class CatalogSeedService(
    IExamRepository exams,
    IEssayTypeRepository essayTypes,
    IRubricRepository rubrics,
    IThemeRepository themes,
    IUnitOfWork unitOfWork,
    TimeProvider time) : ICatalogSeedService
{
    // TODO(conteúdo): os descritores oficiais de cada nível e o enunciado e a coletânea do tema ainda não
    // foram fornecidos. Os textos entre colchetes são marcadores; substitua-os publicando a grade v2 e o tema real.
    private const string MissingDescriptor = "[Descritor oficial do nível {0}: preencher com o texto da grade do Simulado Fuvest 2025.]";

    public async Task<bool> SeedAsync(CancellationToken ct)
    {
        if (await exams.FindBySlugAsync("fuvest", ct) is not null)
        {
            return false;
        }

        var now = time.GetUtcNow();

        var fuvest = Exam.Create("fuvest", "Fuvest", ExamStatus.Active, sheetLines: 30, position: 1);
        exams.Add(fuvest);
        exams.Add(Exam.Create("unicamp", "Unicamp", ExamStatus.ComingSoon, sheetLines: 30, position: 2));
        exams.Add(Exam.Create("enem", "Enem", ExamStatus.ComingSoon, sheetLines: 30, position: 3));

        var dissertacao = EssayType.Create(fuvest.Id, "dissertacao", "Dissertação", "Texto dissertativo-argumentativo");
        var narrativa = EssayType.Create(fuvest.Id, "narrativa", "Narrativa", "Texto narrativo com reflexão");
        essayTypes.Add(dissertacao);
        essayTypes.Add(narrativa);

        var rubric = RubricVersion.Publish(
            fuvest.Id,
            version: 1,
            publishedAt: now,
            zeroRules:
            [
                new ZeroRule("theme_not_developed", "Tema não desenvolvido"),
                new ZeroRule("type_not_met", "Tipo de texto não atendido"),
                new ZeroRule("full_copy", "Cópia integral da coletânea"),
                new ZeroRule("candidate_identified", "Identificação do candidato, como nome ou assinatura na folha"),
            ],
            criteria:
            [
                new CriterionSpec("Tema, coletânea e autoria", 3, Descriptors()),
                new CriterionSpec("Gênero e tipo de texto", 2, Descriptors()),
                new CriterionSpec("Coesão, coerência e progressão", 3, Descriptors(level2: "Boa articulação, com composição adequada.")),
                new CriterionSpec("Convenções da escrita e vocabulário", 2, Descriptors()),
            ]);
        rubrics.Add(rubric);

        var theme = Theme.Draft(
            fuvest.Id,
            "Algoritmos e o debate público",
            "[Enunciado da proposta: o que o candidato deve discutir sobre o tema e como usar a coletânea.]");
        theme.AddText(ThemeTextKind.Text, "[Título do texto 1]", "[Fonte do texto 1]", "[Trecho do texto 1, como aparece na proposta da prova.]");
        theme.AddText(ThemeTextKind.Text, "[Título do texto 2]", "[Fonte do texto 2]", "[Trecho do texto 2.]");
        theme.AddText(ThemeTextKind.Image, "[Gráfico ou charge]", "[Fonte do texto 3]", null);
        theme.AddText(ThemeTextKind.Text, "[Título do texto 4]", "[Fonte do texto 4]", "[Trecho do texto 4.]");
        theme.AcceptType(dissertacao, rubric, "[Instrução da proposta para o texto dissertativo-argumentativo.]");
        theme.AcceptType(narrativa, rubric, "[Instrução da proposta para o texto narrativo com reflexão.]");
        theme.Publish(now);
        themes.Add(theme);

        await unitOfWork.SaveChangesAsync(ct);
        return true;
    }

    private static Dictionary<int, string> Descriptors(string? level2 = null) =>
        new()
        {
            [1] = string.Format(MissingDescriptor, 1),
            [2] = level2 ?? string.Format(MissingDescriptor, 2),
            [3] = string.Format(MissingDescriptor, 3),
        };
}
