using Microsoft.EntityFrameworkCore;
using RedigiAI.Domain.Catalog;

namespace RedigiAI.Infrastructure.Persistence;

/// <summary>
/// Carga inicial do catálogo (no MVP não há painel administrativo). Idempotente: não faz nada se a Fuvest já existe.
/// </summary>
public sealed class CatalogSeeder(AppDbContext db, TimeProvider time)
{
    // TODO(conteúdo): os descritores oficiais de cada nível e o enunciado e a coletânea do tema ainda não
    // foram fornecidos. Os textos entre colchetes são marcadores; substitua-os publicando a grade v2 e o tema real.
    private const string MissingDescriptor = "[Descritor oficial do nível {0}: preencher com o texto da grade do Simulado Fuvest 2025.]";

    public async Task<bool> SeedAsync(CancellationToken ct)
    {
        if (await db.Exams.AnyAsync(e => e.Slug == "fuvest", ct))
        {
            return false;
        }

        var now = time.GetUtcNow();

        var fuvest = Exam.Create("fuvest", "Fuvest", ExamStatus.Active, sheetLines: 30, position: 1);
        db.Exams.AddRange(
            fuvest,
            Exam.Create("unicamp", "Unicamp", ExamStatus.ComingSoon, sheetLines: 30, position: 2),
            Exam.Create("enem", "Enem", ExamStatus.ComingSoon, sheetLines: 30, position: 3));

        var dissertacao = EssayType.Create(fuvest.Id, "dissertacao", "Dissertação", "Texto dissertativo-argumentativo");
        var narrativa = EssayType.Create(fuvest.Id, "narrativa", "Narrativa", "Texto narrativo com reflexão");
        db.EssayTypes.AddRange(dissertacao, narrativa);

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
        db.RubricVersions.Add(rubric);

        var theme = Theme.Draft(
            fuvest.Id,
            "Algoritmos e o debate público",
            "[Enunciado da proposta: o que o candidato deve discutir sobre o tema, como usar a coletânea e as instruções de cada tipo de redação aceito.]");
        theme.AddText(ThemeTextKind.Text, "[Título]", "[Fonte]", "[Trecho do texto 1.]");
        theme.AddText(ThemeTextKind.Text, "[Título]", "[Fonte]", "[Trecho do texto 2.]");
        theme.AddText(ThemeTextKind.Image, "[Gráfico ou charge]", "[Fonte]", null);
        theme.AddText(ThemeTextKind.Text, "[Título]", "[Fonte]", "[Trecho do texto 4.]");
        theme.AcceptType(dissertacao, rubric);
        theme.AcceptType(narrativa, rubric);
        theme.Publish(now);
        db.Themes.Add(theme);

        await db.SaveChangesAsync(ct);
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
