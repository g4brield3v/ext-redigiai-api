using RedigiAI.Application.Abstractions;
using RedigiAI.Application.Grading;
using RedigiAI.Domain.Catalog;

namespace RedigiAI.Application.Catalog;

/// <summary>Carga inicial do catálogo e da base de conhecimento da correção (no MVP não há painel administrativo).</summary>
public interface ICatalogSeedService
{
    /// <summary>Idempotente: só cria o que ainda não existe.</summary>
    /// <returns><c>true</c> se algo foi criado agora.</returns>
    Task<bool> SeedAsync(CancellationToken ct);
}

internal sealed class CatalogSeedService(
    IExamRepository exams,
    IEssayTypeRepository essayTypes,
    IRubricRepository rubrics,
    IThemeRepository themes,
    GradingSeedService grading,
    IUnitOfWork unitOfWork,
    TimeProvider time) : ICatalogSeedService
{
    public async Task<bool> SeedAsync(CancellationToken ct)
    {
        var catalogCreated = await SeedCatalogAsync(ct);
        if (catalogCreated)
        {
            // A base de conhecimento lê o catálogo do banco.
            await unitOfWork.SaveChangesAsync(ct);
        }

        var gradingCreated = await grading.EnsureAsync(ct);
        if (gradingCreated)
        {
            await unitOfWork.SaveChangesAsync(ct);
        }

        return catalogCreated || gradingCreated;
    }

    private async Task<bool> SeedCatalogAsync(CancellationToken ct)
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

        var rubric = GradingSeedService.PublishRubric(fuvest.Id, version: 1, now);
        rubrics.Add(rubric);

        // TODO(conteúdo): o enunciado, a coletânea e as instruções do tema ainda não foram fornecidos.
        // Os textos entre colchetes são marcadores, substituídos direto no banco.
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

        return true;
    }
}
