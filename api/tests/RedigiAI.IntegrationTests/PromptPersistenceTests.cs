using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Catalog;
using RedigiAI.Domain.Common;
using RedigiAI.Domain.Essays;
using RedigiAI.Infrastructure.Persistence;

namespace RedigiAI.IntegrationTests;

/// <summary>As propostas de redação no banco (docs/07-propostas-de-redacao.md, A2).</summary>
[Collection(ApiCollection.Name)]
public class PromptPersistenceTests(ApiFixture api)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Draft_is_saved_and_read_back_with_the_blocks_in_order()
    {
        var (examId, essayTypeId) = await FuvestAsync();
        var imageId = Guid.CreateVersion7();
        var content = Content(examId, essayTypeId, NewSlug(),
        [
            new(null, 30, PromptBlockKind.Instructions, new PromptBlockData { Title = "Instruções", Items = ["Dê um título a sua redação."] }),
            new(null, 10, PromptBlockKind.CollectionItem, new PromptBlockData
            {
                Label = "Texto 1",
                Format = CollectionFormat.Image,
                Source = "Luis Fernando Verissimo.",
                AssetId = imageId,
                Alt = "Tirinha com duas cobras.",
                AiDescription = "Tirinha em três quadros, com as falas das duas cobras.",
            }),
            new(null, 15, PromptBlockKind.CollectionItem, new PromptBlockData
            {
                Label = "Texto 2",
                Format = CollectionFormat.Verse,
                Source = "Gilberto Gil, *Quanta*. 1997.",
                Columns = [["Quanta do latim", "Plural de quantum"], ["Cântico dos cânticos"]],
            }),
            new(null, 20, PromptBlockKind.Command, new PromptBlockData { Body = "Redija uma dissertação sobre **o papel da ciência**." }),
            new(null, 25, PromptBlockKind.Divider, PromptBlockData.None),
        ]);
        var prompt = Prompt.Draft(content, null, Now);

        await SaveAsync(prompts => prompts.Add(prompt));
        var saved = await ReadAsync(prompt.Id);

        Assert.Equal((PromptStatus.Draft, 1), (saved.Status, saved.Version));
        Assert.Equal(essayTypeId, saved.EssayTypeId);
        Assert.Equal([10, 15, 20, 25, 30], saved.Blocks.Select(block => block.Position));
        Assert.Equal(
            [PromptBlockKind.CollectionItem, PromptBlockKind.CollectionItem, PromptBlockKind.Command, PromptBlockKind.Divider, PromptBlockKind.Instructions],
            saved.Blocks.Select(block => block.Kind));
        Assert.Equal(imageId, saved.Blocks[0].Data.AssetId);
        Assert.Equal("Cântico dos cânticos", saved.Blocks[1].Data.Columns![1][0]);
        Assert.Equal(["Dê um título a sua redação."], saved.Blocks[4].Data.Items);
        Assert.Equal("Recorte.", saved.GradingNotes.ExpectedFocus);
        Assert.Equal(["Texto 1: dado."], saved.GradingNotes.KeyData);
        Assert.Equal(
            ("collection_item", "draft"),
            await api.QueryAsync(async db => (
                await db.Database.SqlQuery<string>($"SELECT kind AS \"Value\" FROM prompt_blocks WHERE id = {saved.Blocks[0].Id}").SingleAsync(),
                await db.Database.SqlQuery<string>($"SELECT status AS \"Value\" FROM prompts WHERE id = {saved.Id}").SingleAsync())));
    }

    [Fact]
    public async Task Two_blocks_swap_positions_in_one_save()
    {
        var prompt = await SavedDraftAsync();
        var ids = prompt.Blocks.Select(block => block.Id).ToList();

        await SaveAsync(async prompts =>
        {
            var draft = (await prompts.FindForUpdateAsync(prompt.Id, CancellationToken.None))!;
            draft.Reorder([new(ids[0], 20), new(ids[1], 10), new(ids[2], 30)], Now.AddMinutes(1));
        });

        var saved = await ReadAsync(prompt.Id);
        Assert.Equal([ids[1], ids[0], ids[2]], saved.Blocks.Select(block => block.Id));
        Assert.Equal([10, 20, 30], saved.Blocks.Select(block => block.Position));
    }

    [Fact]
    public async Task Replacing_the_draft_updates_removes_and_adds_blocks()
    {
        var prompt = await SavedDraftAsync();
        var (examId, essayTypeId) = await FuvestAsync();
        var command = prompt.Blocks[1];

        await SaveAsync(async prompts =>
        {
            var draft = (await prompts.FindForUpdateAsync(prompt.Id, CancellationToken.None))!;
            draft.Replace(
                Content(examId, essayTypeId, prompt.Slug,
                [
                    new(command.Id, 10, PromptBlockKind.Command, new PromptBlockData { Body = "Enunciado novo." }),
                    new(null, 20, PromptBlockKind.Paragraph, new PromptBlockData { Body = "Parágrafo novo." }),
                ]),
                Now.AddMinutes(2));
        });

        var saved = await ReadAsync(prompt.Id);
        Assert.Equal([PromptBlockKind.Command, PromptBlockKind.Paragraph], saved.Blocks.Select(block => block.Kind));
        Assert.Equal(command.Id, saved.Blocks[0].Id);
        Assert.Equal("Enunciado novo.", saved.Blocks[0].Data.Body);
        Assert.Equal(2, await api.QueryAsync(db => db.Set<PromptBlock>().CountAsync(block => block.PromptId == prompt.Id)));
        Assert.Equal(1, await api.QueryAsync(db => db.Set<PromptGradingNotes>().CountAsync(notes => notes.PromptId == prompt.Id)));
    }

    [Fact]
    public async Task Database_refuses_two_blocks_in_the_same_position()
    {
        var prompt = await SavedDraftAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => api.QueryAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO prompt_blocks (id, prompt_id, position, kind, data) VALUES ({Guid.CreateVersion7()}, {prompt.Id}, 10, 'divider', '{{}}'::jsonb)")));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Equal("ux_prompt_blocks_position", error.ConstraintName);
    }

    [Fact]
    public async Task Database_keeps_one_published_version_per_slug()
    {
        var first = await SavedDraftAsync(publish: true);
        var second = first.NewVersion(2, null, Now.AddDays(1));
        await SaveAsync(prompts => prompts.Add(second));

        // Publicar a versão 2 sem arquivar a 1 deixaria duas versões publicadas no mesmo endereço.
        await Assert.ThrowsAsync<UniqueConflictException>(() => SaveAsync(async prompts =>
        {
            var draft = (await prompts.FindForUpdateAsync(second.Id, CancellationToken.None))!;
            draft.Publish(Context(draft), null, Now.AddDays(2));
        }));

        await SaveAsync(async prompts =>
        {
            (await prompts.FindForUpdateAsync(first.Id, CancellationToken.None))!.Archive(Now.AddDays(2));
        });
        await SaveAsync(async prompts =>
        {
            var draft = (await prompts.FindForUpdateAsync(second.Id, CancellationToken.None))!;
            draft.Publish(Context(draft), null, Now.AddDays(2));
        });

        var published = await WithRepositoryAsync(prompts => prompts.FindPublishedAsync(first.Slug, CancellationToken.None));
        Assert.Equal((second.Id, 2), (published!.Id, published.Version));
        Assert.Equal(PromptStatus.Archived, (await ReadAsync(first.Id)).Status);
    }

    [Fact]
    public async Task Database_refuses_the_same_version_of_a_slug_twice()
    {
        var first = await SavedDraftAsync();
        var (examId, essayTypeId) = await FuvestAsync();

        await Assert.ThrowsAsync<UniqueConflictException>(
            () => SaveAsync(prompts => prompts.Add(Prompt.Draft(Content(examId, essayTypeId, first.Slug, []), null, Now))));
    }

    [Fact]
    public void Prompt_image_is_never_accepted_as_an_essay_photo()
    {
        var image = Asset.ForPromptImage(Guid.CreateVersion7(), Guid.CreateVersion7(), "prompts/abc", 2048, "image/png", Now);

        Assert.Equal(AssetScope.PromptImage, image.Scope);
        Assert.Equal("INVALID_FILE", Assert.Throws<AppError>(() => image.Confirm(2048, "jpg")).Code);
        Assert.Equal(AssetScope.EssayPhoto, Asset.Reserve(Guid.CreateVersion7(), Now).Scope);
    }

    private static string NewSlug() => $"proposta-{Guid.NewGuid():N}";

    private static PublicationContext Context(Prompt prompt) => new(
        true, prompt.Blocks.SelectMany(block => block.Data.AssetIds()).ToHashSet());

    private static PromptContent Content(Guid examId, Guid essayTypeId, string slug, IReadOnlyList<PromptBlockContent> blocks) => new(
        examId,
        slug,
        "O papel da ciência no mundo contemporâneo",
        "Coletânea com tirinha e poema.",
        "Fuvest (prova anterior)",
        essayTypeId,
        blocks,
        new PromptNotesContent("Recorte.", "Tangente.", null, ["Texto 1: dado."], null));

    private async Task<Prompt> SavedDraftAsync(bool publish = false)
    {
        var (examId, essayTypeId) = await FuvestAsync();
        var prompt = Prompt.Draft(
            Content(examId, essayTypeId, NewSlug(),
            [
                new(null, 10, PromptBlockKind.CollectionItem, new PromptBlockData
                {
                    Label = "Texto 1", Format = CollectionFormat.Prose, Source = "Carl Sagan, 1996.", Body = "Nós criamos uma civilização global.",
                }),
                new(null, 20, PromptBlockKind.Command, new PromptBlockData { Body = "Redija uma dissertação." }),
                new(null, 30, PromptBlockKind.Instructions, new PromptBlockData { Title = "Instruções", Items = ["Dê um título."] }),
            ]),
            null,
            Now);
        if (publish)
        {
            prompt.Publish(Context(prompt), null, Now);
        }

        await SaveAsync(prompts => prompts.Add(prompt));
        return prompt;
    }

    private Task<(Guid ExamId, Guid EssayTypeId)> FuvestAsync() => api.QueryAsync(async db =>
    {
        var exam = await db.Exams.SingleAsync(e => e.Slug == "fuvest");
        var essayType = await db.EssayTypes.SingleAsync(t => t.ExamId == exam.Id && t.Slug == "dissertacao-em-prosa");
        return (exam.Id, essayType.Id);
    });

    private async Task<Prompt> ReadAsync(Guid promptId) =>
        (await WithRepositoryAsync(prompts => prompts.FindAsync(promptId, CancellationToken.None)))!;

    private Task SaveAsync(Action<IPromptRepository> change) => SaveAsync(prompts =>
    {
        change(prompts);
        return Task.CompletedTask;
    });

    private async Task SaveAsync(Func<IPromptRepository, Task> change)
    {
        await using var scope = api.Factory.Services.CreateAsyncScope();
        await change(scope.ServiceProvider.GetRequiredService<IPromptRepository>());
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(CancellationToken.None);
    }

    private async Task<T> WithRepositoryAsync<T>(Func<IPromptRepository, Task<T>> read)
    {
        await using var scope = api.Factory.Services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<IPromptRepository>());
    }
}
