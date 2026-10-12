using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedigiAI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Prompts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "scope",
                table: "assets",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "essay_photo");

            migrationBuilder.CreateTable(
                name: "prompts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    exam_id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    summary = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    source_label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    essay_type_id = table.Column<Guid>(type: "uuid", nullable: true),
                    genre_label = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    sheet_lines_min = table.Column<int>(type: "integer", nullable: true),
                    sheet_lines_max = table.Column<int>(type: "integer", nullable: true),
                    title_required = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    published_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_prompts", x => x.id);
                    table.ForeignKey(
                        name: "fk_prompts_essay_types_essay_type_id",
                        column: x => x.essay_type_id,
                        principalTable: "essay_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_prompts_exams_exam_id",
                        column: x => x.exam_id,
                        principalTable: "exams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_prompts_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_prompts_users_published_by",
                        column: x => x.published_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "prompt_blocks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    prompt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_prompt_blocks", x => x.id);
                    table.CheckConstraint("ck_prompt_blocks_position", "position >= 0");
                    table.ForeignKey(
                        name: "fk_prompt_blocks_prompts_prompt_id",
                        column: x => x.prompt_id,
                        principalTable: "prompts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "prompt_grading_notes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    prompt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expected_focus = table.Column<string>(type: "text", nullable: false),
                    tangent_signals = table.Column<string>(type: "text", nullable: false),
                    off_topic_signals = table.Column<string>(type: "text", nullable: false),
                    key_data = table.Column<string>(type: "jsonb", nullable: false),
                    extra = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_prompt_grading_notes", x => x.id);
                    table.ForeignKey(
                        name: "fk_prompt_grading_notes_prompts_prompt_id",
                        column: x => x.prompt_id,
                        principalTable: "prompts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_prompt_blocks_prompt_id",
                table: "prompt_blocks",
                column: "prompt_id");

            migrationBuilder.CreateIndex(
                name: "ix_prompt_grading_notes_prompt_id",
                table: "prompt_grading_notes",
                column: "prompt_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_prompts_created_by",
                table: "prompts",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_prompts_essay_type_id",
                table: "prompts",
                column: "essay_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_prompts_exam_id_status",
                table: "prompts",
                columns: new[] { "exam_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_prompts_published_by",
                table: "prompts",
                column: "published_by");

            migrationBuilder.CreateIndex(
                name: "ix_prompts_slug_version",
                table: "prompts",
                columns: new[] { "slug", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_prompts_published_per_slug",
                table: "prompts",
                column: "slug",
                unique: true,
                filter: "status = 'published'");

            // Posição única por proposta, conferida no fim da transação: trocar dois blocos de lugar grava uma linha
            // de cada vez, e no meio do caminho as duas têm a mesma posição. O EF não descreve restrição adiável.
            migrationBuilder.Sql(
                "ALTER TABLE prompt_blocks ADD CONSTRAINT ux_prompt_blocks_position UNIQUE (prompt_id, position) DEFERRABLE INITIALLY DEFERRED;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "prompt_blocks");

            migrationBuilder.DropTable(
                name: "prompt_grading_notes");

            migrationBuilder.DropTable(
                name: "prompts");

            migrationBuilder.DropColumn(
                name: "scope",
                table: "assets");
        }
    }
}
