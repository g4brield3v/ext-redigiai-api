using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedigiAI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CorrectionScreenV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "label",
                table: "rubric_versions",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "short_name",
                table: "rubric_criteria",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "main_issue_criterion",
                table: "corrections",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fix",
                table: "correction_marks",
                type: "text",
                nullable: true);

            // Grade já gravada: sem nome curto vale o nome completo; a grade oficial da Fuvest ganha os nomes
            // curtos, o rótulo e o texto para o aluno de cada hipótese de nota zero (docs/decisoes.md, 12/10/2026).
            migrationBuilder.Sql("UPDATE rubric_criteria SET short_name = name;");
            migrationBuilder.Sql(
                """
                UPDATE rubric_criteria c
                SET short_name = v.short_name
                FROM rubric_versions r,
                     (VALUES (1, 'Tema e autoria'), (2, 'Tipo e gênero'), (3, 'Coesão e progressão'), (4, 'Escrita e vocabulário'))
                         AS v(position, short_name)
                WHERE c.rubric_version_id = r.id AND r.source = 'Simulado Fuvest 2027 – 2ª edição' AND c.position = v.position;
                """);
            migrationBuilder.Sql(
                """
                UPDATE rubric_versions r
                SET label = 'Fuvest 2027',
                    zero_rules = COALESCE((
                        SELECT jsonb_agg(z.rule || jsonb_build_object('studentNote', COALESCE(n.note, '')) ORDER BY z.ord)
                        FROM jsonb_array_elements(r.zero_rules) WITH ORDINALITY AS z(rule, ord)
                        LEFT JOIN (VALUES
                            ('theme_not_addressed', 'Texto que não aborda o tema recebe nota zero, e os quatro critérios não são pontuados. Isso é diferente de tangenciar o tema, que só reduz o nível do critério 1.'),
                            ('modality_not_met', 'Texto que não é do tipo pedido na proposta recebe nota zero, e os quatro critérios não são pontuados. Isso é diferente de misturar traços de outro tipo, que só reduz o nível do critério 2.'),
                            ('collection_copy', 'Texto copiado da coletânea, no todo ou em parte, recebe nota zero, e os quatro critérios não são pontuados.'),
                            ('candidate_identification', 'Folha com nome, assinatura ou qualquer marca que identifique quem escreveu recebe nota zero, e os quatro critérios não são pontuados.'))
                            AS n(code, note) ON n.code = z.rule->>'code'), '[]'::jsonb)
                WHERE r.source = 'Simulado Fuvest 2027 – 2ª edição';
                """);

            // Próximos passos passam de texto para {text, criterion}; os já gravados ficam sem critério.
            migrationBuilder.Sql(
                """
                UPDATE corrections c
                SET next_steps = COALESCE((
                    SELECT jsonb_agg(jsonb_build_object('text', s.step, 'criterion', 'null'::jsonb) ORDER BY s.ord)
                    FROM jsonb_array_elements(c.next_steps) WITH ORDINALITY AS s(step, ord)), '[]'::jsonb)
                WHERE jsonb_typeof(c.next_steps -> 0) = 'string';
                """);

            migrationBuilder.CreateTable(
                name: "correction_feedback",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    correction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rating = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_correction_feedback", x => x.id);
                    table.ForeignKey(
                        name: "fk_correction_feedback_corrections_correction_id",
                        column: x => x.correction_id,
                        principalTable: "corrections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_correction_feedback_correction_id",
                table: "correction_feedback",
                column: "correction_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "correction_feedback");

            migrationBuilder.Sql(
                """
                UPDATE corrections c
                SET next_steps = COALESCE((
                    SELECT jsonb_agg(s.step -> 'text' ORDER BY s.ord)
                    FROM jsonb_array_elements(c.next_steps) WITH ORDINALITY AS s(step, ord)), '[]'::jsonb)
                WHERE jsonb_typeof(c.next_steps -> 0) = 'object';
                """);
            migrationBuilder.Sql(
                """
                UPDATE rubric_versions r
                SET zero_rules = COALESCE((
                    SELECT jsonb_agg(z.rule - 'studentNote' ORDER BY z.ord)
                    FROM jsonb_array_elements(r.zero_rules) WITH ORDINALITY AS z(rule, ord)), '[]'::jsonb);
                """);

            migrationBuilder.DropColumn(
                name: "label",
                table: "rubric_versions");

            migrationBuilder.DropColumn(
                name: "short_name",
                table: "rubric_criteria");

            migrationBuilder.DropColumn(
                name: "main_issue_criterion",
                table: "corrections");

            migrationBuilder.DropColumn(
                name: "fix",
                table: "correction_marks");
        }
    }
}
