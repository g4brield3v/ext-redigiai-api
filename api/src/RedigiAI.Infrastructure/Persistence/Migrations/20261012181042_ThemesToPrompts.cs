using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedigiAI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ThemesToPrompts : Migration
    {
        /// <summary>
        /// Troca o modelo de temas pelo de propostas (docs/07-propostas-de-redacao.md). Os temas que existem viram
        /// propostas antes de as tabelas antigas saírem: cada tema e tipo aceito vira uma proposta (a primeira fica com o
        /// id do tema), a coletânea vira blocos em prosa, o enunciado vira o comando e a instrução do tipo vira as
        /// instruções. Redações e correções passam a apontar para a proposta do tema e do tipo com que foram enviadas.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_corrections_theme_grading_notes_theme_grading_notes_id",
                table: "corrections");

            migrationBuilder.DropForeignKey(
                name: "fk_corrections_themes_theme_id",
                table: "corrections");

            migrationBuilder.DropForeignKey(
                name: "fk_essays_themes_theme_id",
                table: "essays");

            migrationBuilder.DropForeignKey(
                name: "fk_reference_essays_themes_theme_id",
                table: "reference_essays");

            migrationBuilder.Sql(ConvertThemes);

            migrationBuilder.RenameColumn(
                name: "theme_id",
                table: "reference_essays",
                newName: "prompt_id");

            migrationBuilder.RenameIndex(
                name: "ix_reference_essays_theme_id",
                table: "reference_essays",
                newName: "ix_reference_essays_prompt_id");

            migrationBuilder.RenameColumn(
                name: "theme_id",
                table: "essays",
                newName: "prompt_id");

            migrationBuilder.RenameIndex(
                name: "ix_essays_theme_id",
                table: "essays",
                newName: "ix_essays_prompt_id");

            migrationBuilder.RenameColumn(
                name: "theme_id",
                table: "corrections",
                newName: "prompt_id");

            migrationBuilder.RenameIndex(
                name: "ix_corrections_theme_id",
                table: "corrections",
                newName: "ix_corrections_prompt_id");

            // Nasce sem valor, é preenchida pela conversão e só então passa a obrigatória.
            migrationBuilder.AddColumn<Guid>(
                name: "prompt_grading_notes_id",
                table: "corrections",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql(RepointEssaysAndCorrections);

            migrationBuilder.AlterColumn<Guid>(
                name: "prompt_grading_notes_id",
                table: "corrections",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.DropIndex(
                name: "ix_corrections_theme_grading_notes_id",
                table: "corrections");

            migrationBuilder.DropColumn(
                name: "theme_grading_notes_id",
                table: "corrections");

            migrationBuilder.DropColumn(
                name: "theme_version",
                table: "corrections");

            migrationBuilder.DropTable(
                name: "theme_essay_types");

            migrationBuilder.DropTable(
                name: "theme_grading_notes");

            migrationBuilder.DropTable(
                name: "theme_texts");

            migrationBuilder.DropTable(
                name: "themes");

            migrationBuilder.Sql("DROP TABLE theme_prompt_map;");

            migrationBuilder.CreateIndex(
                name: "ix_corrections_prompt_grading_notes_id",
                table: "corrections",
                column: "prompt_grading_notes_id");

            migrationBuilder.AddForeignKey(
                name: "fk_corrections_prompt_grading_notes_prompt_grading_notes_id",
                table: "corrections",
                column: "prompt_grading_notes_id",
                principalTable: "prompt_grading_notes",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_corrections_prompts_prompt_id",
                table: "corrections",
                column: "prompt_id",
                principalTable: "prompts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_essays_prompts_prompt_id",
                table: "essays",
                column: "prompt_id",
                principalTable: "prompts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_reference_essays_prompts_prompt_id",
                table: "reference_essays",
                column: "prompt_id",
                principalTable: "prompts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        private const string ConvertThemes =
            """
            -- Id no formato do UUID v7 (tempo nos primeiros 48 bits), como os que o domínio gera.
            CREATE FUNCTION pg_temp.uuid_v7() RETURNS uuid AS $$
                SELECT (lpad(to_hex((extract(epoch FROM clock_timestamp()) * 1000)::bigint), 12, '0')
                    || '7' || substr(md5(random()::text || clock_timestamp()::text), 1, 3)
                    || substr('89ab', 1 + floor(random() * 4)::int, 1)
                    || substr(md5(random()::text || clock_timestamp()::text), 1, 15))::uuid
            $$ LANGUAGE sql VOLATILE;

            -- Uma proposta por tema e tipo aceito. A primeira (ou a única) fica com o id do tema.
            CREATE TABLE theme_prompt_map (
                theme_id uuid NOT NULL,
                essay_type_id uuid NULL,
                prompt_id uuid NOT NULL,
                notes_id uuid NOT NULL,
                is_first boolean NOT NULL);

            INSERT INTO theme_prompt_map (theme_id, essay_type_id, prompt_id, notes_id, is_first)
            SELECT t.id,
                   x.essay_type_id,
                   CASE WHEN COALESCE(x.rn, 1) = 1 THEN t.id ELSE pg_temp.uuid_v7() END,
                   pg_temp.uuid_v7(),
                   COALESCE(x.rn, 1) = 1
            FROM themes t
            LEFT JOIN LATERAL (
                SELECT tet.essay_type_id, row_number() OVER (ORDER BY et.slug) AS rn
                FROM theme_essay_types tet
                JOIN essay_types et ON et.id = tet.essay_type_id
                WHERE tet.theme_id = t.id
            ) x ON true;

            -- O endereço sai do título da primeira versão do tema e vale para todas as versões dele.
            WITH RECURSIVE chain AS (
                SELECT id, id AS root_id FROM themes WHERE supersedes_theme_id IS NULL
                UNION ALL
                SELECT t.id, c.root_id FROM themes t JOIN chain c ON t.supersedes_theme_id = c.id
            ),
            roots AS (
                SELECT r.id AS root_id,
                       left(COALESCE(NULLIF(trim(BOTH '-' FROM regexp_replace(lower(translate(
                           r.title,
                           'ÁÀÂÃÄáàâãäÉÈÊËéèêëÍÌÎÏíìîïÓÒÔÕÖóòôõöÚÙÛÜúùûüÇçÑñ',
                           'AAAAAaaaaaEEEEeeeeIIIIiiiiOOOOOoooooUUUUuuuuCcNn')), '[^a-z0-9]+', '-', 'g')), ''), 'proposta'), 60) AS base
                FROM themes r
                WHERE r.supersedes_theme_id IS NULL
            ),
            slugs AS (
                SELECT root_id,
                       base || CASE WHEN row_number() OVER same_base > 1 THEN '-' || row_number() OVER same_base ELSE '' END AS slug
                FROM roots
                WINDOW same_base AS (PARTITION BY base ORDER BY root_id)
            )
            INSERT INTO prompts (
                id, exam_id, slug, version, status, title, summary, source_label, essay_type_id, genre_label,
                sheet_lines_min, sheet_lines_max, title_required, created_at, updated_at, published_at,
                created_by, published_by, creation_key)
            SELECT m.prompt_id,
                   t.exam_id,
                   left(s.slug || CASE WHEN m.is_first THEN '' ELSE '-' || et.slug END, 80),
                   t.version,
                   CASE t.status WHEN 'active' THEN 'published' WHEN 'archived' THEN 'archived' ELSE 'draft' END,
                   left(t.title, 120),
                   NULL,
                   'Convertida do modelo de temas',
                   m.essay_type_id,
                   et.name,
                   1,
                   e.sheet_lines,
                   false,
                   COALESCE(t.published_at, now()),
                   COALESCE(t.published_at, now()),
                   t.published_at,
                   NULL,
                   NULL,
                   NULL
            FROM theme_prompt_map m
            JOIN themes t ON t.id = m.theme_id
            JOIN chain c ON c.id = t.id
            JOIN slugs s ON s.root_id = c.root_id
            JOIN exams e ON e.id = t.exam_id
            LEFT JOIN essay_types et ON et.id = m.essay_type_id;

            -- A coletânea: cada texto vira um bloco em prosa, com o título antigo em negrito na primeira linha.
            INSERT INTO prompt_blocks (id, prompt_id, position, kind, data)
            SELECT pg_temp.uuid_v7(),
                   m.prompt_id,
                   tt.position * 10,
                   'collection_item',
                   jsonb_build_object(
                       'label', 'Texto ' || tt.position,
                       'format', 'prose',
                       'source', NULLIF(tt.source, ''),
                       'body', NULLIF(concat_ws(E'\n',
                           CASE WHEN tt.title <> '' THEN '**' || tt.title || '**' END,
                           NULLIF(tt.body, ''),
                           CASE WHEN tt.kind = 'image' AND COALESCE(tt.body, '') = '' THEN '[Imagem não convertida]' END), ''))
            FROM theme_prompt_map m
            JOIN theme_texts tt ON tt.theme_id = m.theme_id;

            -- O enunciado vira o comando, logo depois da coletânea.
            INSERT INTO prompt_blocks (id, prompt_id, position, kind, data)
            SELECT pg_temp.uuid_v7(),
                   m.prompt_id,
                   (COALESCE((SELECT max(tt.position) FROM theme_texts tt WHERE tt.theme_id = m.theme_id), 0) + 1) * 10,
                   'command',
                   jsonb_build_object('body', t.statement)
            FROM theme_prompt_map m
            JOIN themes t ON t.id = m.theme_id;

            -- A instrução do tipo vira os itens das instruções, um por linha.
            INSERT INTO prompt_blocks (id, prompt_id, position, kind, data)
            SELECT pg_temp.uuid_v7(),
                   m.prompt_id,
                   (COALESCE((SELECT max(tt.position) FROM theme_texts tt WHERE tt.theme_id = m.theme_id), 0) + 2) * 10,
                   'instructions',
                   jsonb_build_object(
                       'title', 'Instruções',
                       'items', COALESCE((
                           SELECT jsonb_agg(trim(item.line) ORDER BY item.ord)
                           FROM unnest(string_to_array(replace(COALESCE(tet.instruction, ''), E'\r', ''), E'\n'))
                               WITH ORDINALITY AS item(line, ord)
                           WHERE trim(item.line) <> ''), '[]'::jsonb))
            FROM theme_prompt_map m
            LEFT JOIN theme_essay_types tet ON tet.theme_id = m.theme_id AND tet.essay_type_id = m.essay_type_id;

            -- As notas do tema, na versão mais recente, viram o recorte esperado das abordagens.
            INSERT INTO prompt_grading_notes (id, prompt_id, expected_focus, tangent_signals, off_topic_signals, key_data, extra)
            SELECT m.notes_id,
                   m.prompt_id,
                   COALESCE((SELECT n.notes FROM theme_grading_notes n WHERE n.theme_id = m.theme_id ORDER BY n.version DESC LIMIT 1), ''),
                   '',
                   '',
                   '[]'::jsonb,
                   ''
            FROM theme_prompt_map m;
            """;

        private const string RepointEssaysAndCorrections =
            """
            -- A coluna ainda traz o id do tema, que é também o id da primeira proposta dele.
            UPDATE essays e
            SET prompt_id = m.prompt_id
            FROM theme_prompt_map m
            WHERE m.theme_id = e.prompt_id AND m.essay_type_id = e.essay_type_id;

            UPDATE corrections c
            SET prompt_id = e.prompt_id
            FROM essays e
            WHERE e.id = c.essay_id;

            UPDATE corrections c
            SET prompt_grading_notes_id = n.id
            FROM prompt_grading_notes n
            WHERE n.prompt_id = c.prompt_id;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_corrections_prompt_grading_notes_prompt_grading_notes_id",
                table: "corrections");

            migrationBuilder.DropForeignKey(
                name: "fk_corrections_prompts_prompt_id",
                table: "corrections");

            migrationBuilder.DropForeignKey(
                name: "fk_essays_prompts_prompt_id",
                table: "essays");

            migrationBuilder.DropForeignKey(
                name: "fk_reference_essays_prompts_prompt_id",
                table: "reference_essays");

            migrationBuilder.DropIndex(
                name: "ix_corrections_prompt_grading_notes_id",
                table: "corrections");

            migrationBuilder.DropColumn(
                name: "prompt_grading_notes_id",
                table: "corrections");

            migrationBuilder.RenameColumn(
                name: "prompt_id",
                table: "reference_essays",
                newName: "theme_id");

            migrationBuilder.RenameIndex(
                name: "ix_reference_essays_prompt_id",
                table: "reference_essays",
                newName: "ix_reference_essays_theme_id");

            migrationBuilder.RenameColumn(
                name: "prompt_id",
                table: "essays",
                newName: "theme_id");

            migrationBuilder.RenameIndex(
                name: "ix_essays_prompt_id",
                table: "essays",
                newName: "ix_essays_theme_id");

            migrationBuilder.RenameColumn(
                name: "prompt_id",
                table: "corrections",
                newName: "theme_id");

            migrationBuilder.RenameIndex(
                name: "ix_corrections_prompt_id",
                table: "corrections",
                newName: "ix_corrections_theme_id");

            migrationBuilder.AddColumn<Guid>(
                name: "theme_grading_notes_id",
                table: "corrections",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "theme_version",
                table: "corrections",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "themes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    exam_id = table.Column<Guid>(type: "uuid", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    statement = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    supersedes_theme_id = table.Column<Guid>(type: "uuid", nullable: true),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_themes", x => x.id);
                    table.ForeignKey(
                        name: "fk_themes_exams_exam_id",
                        column: x => x.exam_id,
                        principalTable: "exams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_themes_themes_supersedes_theme_id",
                        column: x => x.supersedes_theme_id,
                        principalTable: "themes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "theme_essay_types",
                columns: table => new
                {
                    theme_id = table.Column<Guid>(type: "uuid", nullable: false),
                    essay_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instruction = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false, defaultValue: ""),
                    rubric_version_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_theme_essay_types", x => new { x.theme_id, x.essay_type_id });
                    table.ForeignKey(
                        name: "fk_theme_essay_types_essay_types_essay_type_id",
                        column: x => x.essay_type_id,
                        principalTable: "essay_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_theme_essay_types_rubric_versions_rubric_version_id",
                        column: x => x.rubric_version_id,
                        principalTable: "rubric_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_theme_essay_types_themes_theme_id",
                        column: x => x.theme_id,
                        principalTable: "themes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "theme_grading_notes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    theme_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_theme_grading_notes", x => x.id);
                    table.ForeignKey(
                        name: "fk_theme_grading_notes_themes_theme_id",
                        column: x => x.theme_id,
                        principalTable: "themes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "theme_texts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    body = table.Column<string>(type: "text", nullable: true),
                    image_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    theme_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_theme_texts", x => x.id);
                    table.ForeignKey(
                        name: "fk_theme_texts_themes_theme_id",
                        column: x => x.theme_id,
                        principalTable: "themes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_corrections_theme_grading_notes_id",
                table: "corrections",
                column: "theme_grading_notes_id");

            migrationBuilder.CreateIndex(
                name: "ix_theme_essay_types_essay_type_id",
                table: "theme_essay_types",
                column: "essay_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_theme_essay_types_rubric_version_id",
                table: "theme_essay_types",
                column: "rubric_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_theme_grading_notes_theme_id_version",
                table: "theme_grading_notes",
                columns: new[] { "theme_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_theme_texts_theme_id_position",
                table: "theme_texts",
                columns: new[] { "theme_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_themes_exam_id_status",
                table: "themes",
                columns: new[] { "exam_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_themes_supersedes_theme_id",
                table: "themes",
                column: "supersedes_theme_id",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_corrections_theme_grading_notes_theme_grading_notes_id",
                table: "corrections",
                column: "theme_grading_notes_id",
                principalTable: "theme_grading_notes",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_corrections_themes_theme_id",
                table: "corrections",
                column: "theme_id",
                principalTable: "themes",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_essays_themes_theme_id",
                table: "essays",
                column: "theme_id",
                principalTable: "themes",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_reference_essays_themes_theme_id",
                table: "reference_essays",
                column: "theme_id",
                principalTable: "themes",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
