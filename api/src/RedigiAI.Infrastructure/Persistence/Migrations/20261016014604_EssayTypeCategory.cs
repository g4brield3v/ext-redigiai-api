using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedigiAI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EssayTypeCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // O tipo de redação passa a ser o nome da categoria de correção. A categoria de dissertação é renomeada
            // na mesma linha: o id não muda, então perfis de correção, propostas e redações continuam apontando para
            // ela. O título das redações já enviadas fica como foi gravado.
            // Se as duas categorias já existem no mesmo vestibular, não há como juntar sem decidir qual perfil vale:
            // a migração para, em vez de deixar a antiga com o nome velho sem aviso.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM essay_types AS t
                        JOIN essay_types AS other ON other.exam_id = t.exam_id AND other.slug = 'dissertacao-em-prosa'
                        WHERE t.slug = 'dissertacao') THEN
                        RAISE EXCEPTION 'essay_types: dissertacao and dissertacao-em-prosa both exist in the same exam';
                    END IF;
                END $$;

                UPDATE essay_types
                SET slug = 'dissertacao-em-prosa', name = 'Dissertação em prosa'
                WHERE slug = 'dissertacao';
                """);

            // Linhas e título obrigatório saem do cadastro: essas regras ficam nos itens das Instruções. As versões
            // publicadas não são reescritas (regra 14); quem precisar das regras cria uma versão nova com elas.
            migrationBuilder.DropColumn(
                name: "genre_label",
                table: "prompts");

            migrationBuilder.DropColumn(
                name: "sheet_lines_max",
                table: "prompts");

            migrationBuilder.DropColumn(
                name: "sheet_lines_min",
                table: "prompts");

            migrationBuilder.DropColumn(
                name: "title_required",
                table: "prompts");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "genre_label",
                table: "prompts",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "sheet_lines_max",
                table: "prompts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "sheet_lines_min",
                table: "prompts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "title_required",
                table: "prompts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(
                """
                UPDATE essay_types AS t
                SET slug = 'dissertacao', name = 'Dissertação'
                WHERE t.slug = 'dissertacao-em-prosa'
                  AND NOT EXISTS (
                      SELECT 1 FROM essay_types AS other
                      WHERE other.exam_id = t.exam_id AND other.slug = 'dissertacao');
                """);
        }
    }
}
