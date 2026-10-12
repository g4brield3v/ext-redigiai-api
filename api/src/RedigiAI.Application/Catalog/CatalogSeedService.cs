using Microsoft.Extensions.Options;
using RedigiAI.Application.Abstractions;
using RedigiAI.Application.Grading;
using RedigiAI.Domain.Catalog;

namespace RedigiAI.Application.Catalog;

/// <summary>
/// Carga inicial do catálogo e da base de conhecimento da correção. As propostas de redação são cadastradas pela
/// área de administração; só em desenvolvimento entra uma proposta de exemplo, em banco novo.
/// </summary>
public interface ICatalogSeedService
{
    /// <summary>Idempotente: só cria o que ainda não existe.</summary>
    /// <returns><c>true</c> se algo foi criado agora.</returns>
    Task<bool> SeedAsync(CancellationToken ct);
}

/// <summary>O que a carga inicial cria além do vestibular, da grade e dos perfis de correção.</summary>
public sealed class SeedOptions
{
    /// <summary>
    /// Cria a proposta de exemplo, com textos entre colchetes. Quem liga é o programa, e só no ambiente
    /// <c>Development</c>: não é lido de configuração. Em staging e produção as propostas vêm só do admin.
    /// </summary>
    public bool ExamplePrompt { get; set; }
}

internal sealed class CatalogSeedService(
    IExamRepository exams,
    IEssayTypeRepository essayTypes,
    IRubricRepository rubrics,
    IPromptRepository prompts,
    GradingSeedService grading,
    IUnitOfWork unitOfWork,
    IOptions<SeedOptions> options,
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

        // Depois da base de conhecimento: a proposta só é publicada com perfil de correção ativo.
        var promptCreated = options.Value.ExamplePrompt && await EnsureExamplePromptAsync(ct);
        if (promptCreated)
        {
            await unitOfWork.SaveChangesAsync(ct);
        }

        return catalogCreated || gradingCreated || promptCreated;
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

        return true;
    }

    /// <summary>Uma proposta publicada de exemplo, só em desenvolvimento e quando o vestibular ainda não tem nenhuma.</summary>
    private async Task<bool> EnsureExamplePromptAsync(CancellationToken ct)
    {
        if (await exams.FindBySlugAsync("fuvest", ct) is not { } fuvest
            || await prompts.AnyAsync(fuvest.Id, ct)
            || (await essayTypes.ListByExamAsync(fuvest.Id, ct)).FirstOrDefault(type => type.Slug == "dissertacao") is not { } dissertacao)
        {
            return false;
        }

        // TODO(conteúdo): os textos entre colchetes são marcadores. A proposta de verdade é cadastrada pelo admin,
        // e esta é arquivada por lá.
        static PromptBlockContent Text(int position, string label, string body) => new(
            null,
            position,
            PromptBlockKind.CollectionItem,
            new PromptBlockData { Label = label, Format = CollectionFormat.Prose, Source = $"[Fonte do {label.ToLowerInvariant()}]", Body = body });

        var now = time.GetUtcNow();
        var prompt = Prompt.Draft(
            new PromptContent(
                fuvest.Id,
                "algoritmos-e-o-debate-publico",
                "Algoritmos e o debate público",
                "[Resumo da proposta, para o cartão da lista.]",
                "Exemplo",
                dissertacao.Id,
                "Dissertação em prosa",
                MinLines: 20,
                MaxLines: fuvest.SheetLines,
                TitleRequired: true,
                [
                    Text(10, "Texto 1", "[Trecho do texto 1, como aparece na proposta da prova.]"),
                    Text(20, "Texto 2", "[Trecho do texto 2.]"),
                    new(null, 30, PromptBlockKind.CollectionItem, new PromptBlockData
                    {
                        Label = "Texto 3",
                        Format = CollectionFormat.Verse,
                        Source = "[Fonte do texto 3]",
                        Columns = [["[verso]", "[verso]"], ["[verso]", "[verso]"]],
                    }),
                    Text(40, "Texto 4", "[Trecho do texto 4.]"),
                    new(null, 50, PromptBlockKind.Command, new PromptBlockData
                    {
                        Body = "[Enunciado da proposta: o que o candidato deve discutir sobre o tema e como usar a coletânea.]",
                    }),
                    new(null, 60, PromptBlockKind.Instructions, new PromptBlockData
                    {
                        Title = "Instruções",
                        Items = ["[Instrução da proposta para o texto dissertativo-argumentativo.]"],
                    }),
                ],
                new PromptNotesContent("[Recorte que a banca espera do tema.]", "[O que caracteriza tangenciar o tema.]", null, null, null)),
            createdBy: null,
            now);
        prompt.Publish(new PublicationContext(true, fuvest.SheetLines, new HashSet<Guid>()), publishedBy: null, now);
        prompts.Add(prompt);

        return true;
    }
}
