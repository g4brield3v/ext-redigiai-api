using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RedigiAI.Domain.Catalog;
using RedigiAI.Infrastructure.Persistence;

namespace RedigiAI.IntegrationTests;

/// <summary>
/// A migração que troca os temas pelas propostas, rodada sobre um banco que ainda está no modelo antigo e já tem
/// temas, redações e correções.
/// </summary>
[Collection(ApiCollection.Name)]
public class ThemeConversionTests(ApiFixture api)
{
    private const string LastMigrationWithThemes = "PromptCreationKey";

    [Fact]
    public async Task Existing_themes_essays_and_corrections_are_converted_to_prompts()
    {
        await using var database = await OldDatabaseAsync();
        var db = database.Context;

        var exam = Guid.CreateVersion7();
        var (dissertacao, narrativa) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var (first, revision, draft, sameTitle) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var (essayOnDissertacao, essayOnNarrativa, essayOnRevision) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var correction = Guid.CreateVersion7();
        var published = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

        await database.InsertAsync("exams", new() { ["id"] = exam, ["slug"] = "fuvest", ["name"] = "Fuvest", ["status"] = "active", ["sheet_lines"] = 30 });
        await database.InsertAsync("essay_types", new() { ["id"] = dissertacao, ["exam_id"] = exam, ["slug"] = "dissertacao", ["name"] = "Dissertação" });
        await database.InsertAsync("essay_types", new() { ["id"] = narrativa, ["exam_id"] = exam, ["slug"] = "narrativa", ["name"] = "Narrativa" });

        // Um tema com dois tipos aceitos, já na segunda versão; um rascunho sem tipo; e outro tema com o mesmo título.
        await database.InsertAsync("themes", new()
        {
            ["id"] = first, ["exam_id"] = exam, ["title"] = "A Ciência & a Arte: irmãs?", ["statement"] = "Enunciado da versão 1.",
            ["status"] = "archived", ["version"] = 1, ["published_at"] = published,
        });
        await database.InsertAsync("themes", new()
        {
            ["id"] = revision, ["exam_id"] = exam, ["title"] = "A Ciência & a Arte: irmãs?", ["statement"] = "Enunciado da versão 2.",
            ["status"] = "active", ["version"] = 2, ["supersedes_theme_id"] = first, ["published_at"] = published.AddDays(5),
        });
        await database.InsertAsync("themes", new()
        {
            ["id"] = draft, ["exam_id"] = exam, ["title"] = "Rascunho sem tipo", ["statement"] = "Ainda em rascunho.", ["status"] = "draft", ["version"] = 1,
        });
        await database.InsertAsync("themes", new()
        {
            ["id"] = sameTitle, ["exam_id"] = exam, ["title"] = "A ciência, a arte... irmãs!", ["statement"] = "Outro tema.",
            ["status"] = "active", ["version"] = 1, ["published_at"] = published,
        });
        foreach (var theme in new[] { first, revision })
        {
            await database.InsertAsync("theme_essay_types", new() { ["theme_id"] = theme, ["essay_type_id"] = narrativa, ["instruction"] = "Escreva uma narrativa." });
            await database.InsertAsync("theme_essay_types", new()
            {
                ["theme_id"] = theme, ["essay_type_id"] = dissertacao, ["instruction"] = "Instruções:\r\nEscreva, no mínimo, 20 linhas.\n\n  Dê um título.  ",
            });
        }

        await database.InsertAsync("theme_essay_types", new() { ["theme_id"] = sameTitle, ["essay_type_id"] = dissertacao, ["instruction"] = "" });
        await database.InsertAsync("theme_texts", new()
        {
            ["theme_id"] = revision, ["position"] = 1, ["kind"] = "text", ["title"] = "O papel da ciência", ["source"] = "Oscar Sala, 1974.", ["body"] = "Somente numa sociedade...",
        });
        await database.InsertAsync("theme_texts", new()
        {
            ["theme_id"] = revision, ["position"] = 2, ["kind"] = "image", ["title"] = "", ["source"] = "Verissimo.", ["body"] = null,
        });
        await database.InsertAsync("theme_grading_notes", new() { ["theme_id"] = revision, ["version"] = 1, ["notes"] = "Notas antigas." });
        await database.InsertAsync("theme_grading_notes", new() { ["theme_id"] = revision, ["version"] = 2, ["notes"] = "Recorte: ciência e arte." });

        await database.InsertAsync("essays", new() { ["id"] = essayOnDissertacao, ["theme_id"] = first, ["essay_type_id"] = dissertacao, ["status"] = "completed" });
        await database.InsertAsync("essays", new() { ["id"] = essayOnNarrativa, ["theme_id"] = first, ["essay_type_id"] = narrativa, ["status"] = "completed" });
        await database.InsertAsync("essays", new() { ["id"] = essayOnRevision, ["theme_id"] = revision, ["essay_type_id"] = narrativa, ["status"] = "completed" });
        await database.InsertAsync("corrections", new() { ["id"] = correction, ["essay_id"] = essayOnRevision, ["theme_id"] = revision, ["theme_version"] = 2 });

        await database.MigrateAsync(target: null);

        var prompts = await db.Prompts.AsNoTracking().Include(p => p.Blocks.OrderBy(b => b.Position)).Include(p => p.GradingNotes).ToListAsync();
        Assert.Equal(6, prompts.Count);

        // A primeira proposta de cada tema fica com o id do tema; a do segundo tipo ganha um id novo, no formato v7.
        var v1 = prompts.Single(p => p.Id == first);
        var v2 = prompts.Single(p => p.Id == revision);
        var v1Narrativa = prompts.Single(p => p.Version == 1 && p.EssayTypeId == narrativa);
        var v2Narrativa = prompts.Single(p => p.Version == 2 && p.EssayTypeId == narrativa);
        Assert.Equal((1, PromptStatus.Archived, dissertacao, "Dissertação"), (v1.Version, v1.Status, v1.EssayTypeId, v1.GenreLabel));
        Assert.Equal((v1.Slug, 2, PromptStatus.Published), (v2.Slug, v2.Version, v2.Status));
        Assert.Equal(($"{v1.Slug}-narrativa", PromptStatus.Archived), (v1Narrativa.Slug, v1Narrativa.Status));
        Assert.Equal(($"{v1.Slug}-narrativa", PromptStatus.Published), (v2Narrativa.Slug, v2Narrativa.Status));
        Assert.All(new[] { v1Narrativa, v2Narrativa }, p => Assert.Equal(7, p.Id.Version));
        Assert.Equal(published.AddDays(5), v2.PublishedAt);
        Assert.Equal((1, 30, false), (v2.SheetLinesMin, v2.SheetLinesMax, v2.TitleRequired));

        // Dois temas cujo título dá o mesmo endereço: um deles ganha um sufixo. O rascunho sem tipo continua rascunho.
        Assert.Equal(
            ["a-ciencia-a-arte-irmas", "a-ciencia-a-arte-irmas-2"],
            new[] { v1.Slug, prompts.Single(p => p.Id == sameTitle).Slug }.Order());
        var convertedDraft = prompts.Single(p => p.Id == draft);
        Assert.Equal((PromptStatus.Draft, (Guid?)null, "rascunho-sem-tipo"), (convertedDraft.Status, convertedDraft.EssayTypeId, convertedDraft.Slug));

        // Coletânea, enunciado e instruções viram blocos, na ordem.
        Assert.Equal([10, 20, 30, 40], v2.Blocks.Select(b => b.Position));
        Assert.Equal(
            [PromptBlockKind.CollectionItem, PromptBlockKind.CollectionItem, PromptBlockKind.Command, PromptBlockKind.Instructions],
            v2.Blocks.Select(b => b.Kind));
        Assert.Equal(
            ("Texto 1", CollectionFormat.Prose, "Oscar Sala, 1974.", "**O papel da ciência**\nSomente numa sociedade..."),
            (v2.Blocks[0].Data.Label, v2.Blocks[0].Data.Format, v2.Blocks[0].Data.Source, v2.Blocks[0].Data.Body));
        Assert.Equal("[Imagem não convertida]", v2.Blocks[1].Data.Body);
        Assert.Equal("Enunciado da versão 2.", v2.Blocks[2].Data.Body);
        Assert.Equal(["Instruções:", "Escreva, no mínimo, 20 linhas.", "Dê um título."], v2.Blocks[3].Data.Items);
        Assert.Equal(["Escreva uma narrativa."], v2Narrativa.Blocks[^1].Data.Items);
        Assert.Equal([PromptBlockKind.Command, PromptBlockKind.Instructions], v1.Blocks.Select(b => b.Kind));
        Assert.All(prompts.SelectMany(p => p.Blocks), b => Assert.Equal(7, b.Id.Version));

        Assert.Equal("Recorte: ciência e arte.", v2.GradingNotes.ExpectedFocus);
        Assert.Equal("", v1.GradingNotes.ExpectedFocus);

        // Cada redação aponta para a proposta do tema e do tipo com que foi enviada.
        var essays = await db.Essays.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.PromptId);
        Assert.Equal(first, essays[essayOnDissertacao]);
        Assert.Equal(v1Narrativa.Id, essays[essayOnNarrativa]);
        Assert.Equal(v2Narrativa.Id, essays[essayOnRevision]);
        var converted = await db.Corrections.AsNoTracking().SingleAsync(c => c.Id == correction);
        Assert.Equal((v2Narrativa.Id, v2Narrativa.GradingNotes.Id), (converted.PromptId, converted.PromptGradingNotesId));

        Assert.Empty(await db.Database
            .SqlQuery<string>($"SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_name LIKE 'theme%'")
            .ToListAsync());
    }

    [Fact]
    public async Task Database_without_themes_migrates_cleanly()
    {
        await using var database = await OldDatabaseAsync();

        await database.MigrateAsync(target: null);

        Assert.Empty(await database.Context.Prompts.ToListAsync());
    }

    private async Task<OldDatabase> OldDatabaseAsync()
    {
        var name = $"conversion_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(api.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(api.ConnectionString) { Database = name }.ConnectionString;
        var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options);
        var database = new OldDatabase(context);
        await database.MigrateAsync(LastMigrationWithThemes);
        return database;
    }

    /// <summary>Um banco parado no modelo antigo. As linhas entram por SQL, porque o código já não conhece os temas.</summary>
    private sealed class OldDatabase(AppDbContext context) : IAsyncDisposable
    {
        public AppDbContext Context { get; } = context;

        public Task MigrateAsync(string? target) => Context.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(target);

        /// <summary>
        /// Insere uma linha com as colunas informadas. As demais colunas obrigatórias recebem um valor qualquer do
        /// tipo delas, e as chaves estrangeiras não são conferidas: aqui só interessa o que a conversão lê.
        /// </summary>
        public async Task InsertAsync(string table, Dictionary<string, object?> values)
        {
            var connection = (NpgsqlConnection)Context.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync();
                await using var relax = new NpgsqlCommand("SET session_replication_role = replica", connection);
                await relax.ExecuteNonQueryAsync();
            }

            await using (var columns = new NpgsqlCommand(
                "SELECT column_name, data_type FROM information_schema.columns WHERE table_name = @table AND is_nullable = 'NO' AND column_default IS NULL",
                connection))
            {
                columns.Parameters.AddWithValue("table", table);
                await using var reader = await columns.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    values.TryAdd(reader.GetString(0), reader.GetString(1) switch
                    {
                        "uuid" => Guid.CreateVersion7(),
                        "integer" or "bigint" or "smallint" => 0,
                        "boolean" => false,
                        "timestamp with time zone" => DateTimeOffset.UtcNow,
                        "date" => new DateOnly(2000, 1, 1),
                        "jsonb" => "[]",
                        "ARRAY" => Array.Empty<int>(),
                        _ => Guid.NewGuid().ToString("N"),
                    });
                }
            }

            var names = values.Keys.ToList();
            await using var insert = new NpgsqlCommand(
                $"INSERT INTO {table} ({string.Join(", ", names)}) VALUES ({string.Join(", ", names.Select((name, index) => values[name] is string text && text == "[]" ? $"@p{index}::jsonb" : $"@p{index}"))})",
                connection);
            for (var index = 0; index < names.Count; index++)
            {
                insert.Parameters.AddWithValue($"p{index}", values[names[index]] ?? DBNull.Value);
            }

            await insert.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync() => await Context.DisposeAsync();
    }
}
