using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RedigiAI.Application.Catalog;
using RedigiAI.Domain.Catalog;
using RedigiAI.Domain.Grading;
using RedigiAI.Infrastructure.Persistence;

namespace RedigiAI.IntegrationTests;

/// <summary>A carga inicial em banco novo, em cada ambiente.</summary>
[Collection(ApiCollection.Name)]
public class SeedTests(ApiFixture api)
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public async Task Outside_development_the_seed_creates_no_prompt(string environment)
    {
        await using var factory = await SeededAsync(environment);

        // O vestibular, a grade e os perfis de correção entram; as propostas vêm só do admin.
        Assert.Equal(0, await CountAsync(factory, db => db.Prompts.CountAsync()));
        Assert.Equal(["fuvest", "unicamp", "enem"], await CountAsync(factory, db => db.Exams.OrderBy(e => e.Position).Select(e => e.Slug).ToListAsync()));
        Assert.Equal(1, await CountAsync(factory, db => db.RubricVersions.CountAsync()));
        Assert.Equal(3, await CountAsync(factory, db => db.GradingProfiles.CountAsync(p => p.Status == GradingProfileStatus.Active)));
        Assert.Empty((await api.RegisteredClient(factory).GetFromJsonAsync<JsonElement>("/v1/exams/fuvest/prompts")).EnumerateArray());

        // Rodar de novo continua sem criar proposta.
        Assert.False(await SeedAsync(factory));
        Assert.Equal(0, await CountAsync(factory, db => db.Prompts.CountAsync()));
    }

    /// <summary>Nem em desenvolvimento: as propostas vêm do comando import-prompts (decisão de 21/10/2026).</summary>
    [Fact]
    public async Task In_development_the_seed_creates_no_prompt_either()
    {
        await using var factory = await SeededAsync("Development");

        Assert.Equal(0, await CountAsync(factory, db => db.Prompts.CountAsync()));
        Assert.Equal(["fuvest", "unicamp", "enem"], await CountAsync(factory, db => db.Exams.OrderBy(e => e.Position).Select(e => e.Slug).ToListAsync()));
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Production", false)]
    public async Task Outdated_grading_instructions_are_replaced_only_in_development(string environment, bool replaced)
    {
        await using var factory = await SeededAsync(environment);
        var before = await CountAsync(factory, db => db.GradingProfiles.Where(p => p.Status == GradingProfileStatus.Active).Select(p => p.Id).ToListAsync());

        // O banco ficou com instruções de uma versão anterior do programa.
        await CountAsync(factory, db => db.Database.ExecuteSqlRawAsync("UPDATE prompt_templates SET user_text = 'instruções antigas com redacao' WHERE kind = 'grading'"));

        Assert.Equal(replaced, await SeedAsync(factory));

        var templates = await CountAsync(factory, db => db.PromptTemplates.Where(t => t.Kind == PromptKind.Grading).OrderBy(t => t.Version).ToListAsync());
        var active = await CountAsync(factory, db => db.GradingProfiles.Where(p => p.Status == GradingProfileStatus.Active).ToListAsync());
        if (!replaced)
        {
            // Fora de desenvolvimento a carga não troca o que está valendo: perfil novo passa pelos testes antes.
            Assert.Equal("instruções antigas com redacao", Assert.Single(templates).UserText);
            Assert.Equal(before.Order(), active.Select(p => p.Id).Order());
            return;
        }

        // A versão antiga continua gravada; a nova entra como versão 2, com perfis novos apontando para ela.
        Assert.Equal([1, 2], templates.Select(t => t.Version));
        Assert.Equal("instruções antigas com redacao", templates[0].UserText);
        Assert.Contains("<abordagens_esperadas>", templates[1].UserText);
        Assert.Equal(3, active.Count);
        Assert.All(active, profile => Assert.Equal((2, templates[1].Id), (profile.Version, profile.GradingPromptId)));
        Assert.Empty(active.Select(p => p.Id).Intersect(before));
        Assert.Equal(3, await CountAsync(factory, db => db.GradingProfiles.CountAsync(p => p.Status == GradingProfileStatus.Retired && before.Contains(p.Id))));

        // Com as instruções em dia, rodar de novo não cria outra versão.
        Assert.False(await SeedAsync(factory));
        Assert.Equal(2, await CountAsync(factory, db => db.PromptTemplates.CountAsync(t => t.Kind == PromptKind.Grading)));
    }

    [Fact]
    public async Task Outdated_grader_manual_is_replaced_in_development_keeping_the_grading_instructions()
    {
        await using var factory = await SeededAsync("Development");
        var before = await CountAsync(factory, db => db.GradingProfiles.Where(p => p.Status == GradingProfileStatus.Active).ToListAsync());

        // O banco ficou com o manual do corretor de uma versão anterior do programa; as instruções estão em dia.
        await CountAsync(factory, db => db.Database.ExecuteSqlRawAsync("UPDATE rubric_guideline_versions SET general_rules = 'Justificativa de até 60 palavras.'"));

        Assert.True(await SeedAsync(factory));

        // O manual antigo continua gravado; o novo entra como versão 2 e só ele muda nos perfis novos.
        var manuals = await CountAsync(factory, db => db.GuidelinesVersions.Include(g => g.Criteria).OrderBy(g => g.Version).ToListAsync());
        Assert.Equal([1, 2], manuals.Select(g => g.Version));
        Assert.Equal("Justificativa de até 60 palavras.", manuals[0].GeneralRules);
        Assert.DoesNotContain("60 palavras", manuals[1].GeneralRules);
        Assert.Equal(manuals[0].Criteria.Count, manuals[1].Criteria.Count);
        var active = await CountAsync(factory, db => db.GradingProfiles.Where(p => p.Status == GradingProfileStatus.Active).ToListAsync());
        Assert.Equal(3, active.Count);
        Assert.All(active, profile => Assert.Equal((2, manuals[1].Id), (profile.Version, profile.GuidelinesVersionId)));
        Assert.Equal(before.Select(p => p.GradingPromptId).Order(), active.Select(p => p.GradingPromptId).Order());
        Assert.Equal(1, await CountAsync(factory, db => db.PromptTemplates.CountAsync(t => t.Kind == PromptKind.Grading)));

        // Com o manual em dia, rodar de novo não cria outra versão.
        Assert.False(await SeedAsync(factory));
        Assert.Equal(2, await CountAsync(factory, db => db.GuidelinesVersions.CountAsync()));
    }

    /// <summary>
    /// "Dissertação" (o que a prova de 2025 pede) entra também em banco que já existe, com um perfil ativo igual ao de
    /// "Dissertação em prosa" (decisão de 21/10/2026).
    /// </summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task Existing_database_gets_the_dissertation_category_with_a_copy_of_the_prose_profile(string environment)
    {
        await using var factory = await SeededAsync(environment);

        // Um banco de antes da categoria nova: só "Dissertação em prosa" e "Narrativa".
        await CountAsync(factory, db => db.GradingProfiles.Where(p => db.EssayTypes.Any(t => t.Id == p.EssayTypeId && t.Slug == "dissertacao")).ExecuteDeleteAsync());
        await CountAsync(factory, db => db.EssayTypes.Where(t => t.Slug == "dissertacao").ExecuteDeleteAsync());

        Assert.True(await SeedAsync(factory));

        var types = await CountAsync(factory, db => db.EssayTypes.ToDictionaryAsync(t => t.Slug));
        Assert.Equal("Dissertação", types["dissertacao"].Name);
        var active = await CountAsync(factory, db => db.GradingProfiles.Where(p => p.Status == GradingProfileStatus.Active).ToListAsync());
        var prose = active.Single(p => p.EssayTypeId == types["dissertacao-em-prosa"].Id);
        var copy = active.Single(p => p.EssayTypeId == types["dissertacao"].Id);
        Assert.Equal(
            (prose.TranscriptionPromptId, prose.GradingPromptId, prose.RubricVersionId, prose.GuidelinesVersionId, prose.WritingRulesVersionId, prose.TranscriptionModel, prose.GradingModel),
            (copy.TranscriptionPromptId, copy.GradingPromptId, copy.RubricVersionId, copy.GuidelinesVersionId, copy.WritingRulesVersionId, copy.TranscriptionModel, copy.GradingModel));
        Assert.NotEqual(prose.Id, copy.Id);

        // Rodar de novo não cria outra categoria nem outro perfil.
        Assert.False(await SeedAsync(factory));
        Assert.Equal(3, await CountAsync(factory, db => db.EssayTypes.CountAsync()));
        Assert.Equal(3, await CountAsync(factory, db => db.GradingProfiles.CountAsync()));
    }

    /// <summary>A aplicação nesse ambiente, sobre um banco novo, já migrado e com a carga inicial.</summary>
    private async Task<WebApplicationFactory<Program>> SeededAsync(string environment)
    {
        var name = $"seed_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(api.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(api.ConnectionString) { Database = name }.ConnectionString;

        // Sem a proposta de exemplo forçada pelos testes: vale o que o programa decide pelo ambiente.
        var factory = api.CreateFactory(
            builder =>
            {
                builder.UseEnvironment(environment);
                builder.UseSetting("ConnectionStrings:Default", connectionString);
            },
            examplePrompt: false);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        }

        Assert.True(await SeedAsync(factory));
        return factory;
    }

    private static async Task<bool> SeedAsync(WebApplicationFactory<Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICatalogSeedService>().SeedAsync(CancellationToken.None);
    }

    private static async Task<T> CountAsync<T>(WebApplicationFactory<Program> factory, Func<AppDbContext, Task<T>> query)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}
