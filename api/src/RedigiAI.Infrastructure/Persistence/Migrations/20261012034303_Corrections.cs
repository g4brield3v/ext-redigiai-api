using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedigiAI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Corrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "supersedes_theme_id",
                table: "themes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "version",
                table: "themes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Os temas que já existiam são a primeira versão.
            migrationBuilder.Sql("UPDATE themes SET version = 1;");

            migrationBuilder.AddColumn<Guid>(
                name: "grading_profile_id",
                table: "essays",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "corrections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    essay_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rubric_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    grading_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    theme_id = table.Column<Guid>(type: "uuid", nullable: false),
                    theme_version = table.Column<int>(type: "integer", nullable: false),
                    theme_grading_notes_id = table.Column<Guid>(type: "uuid", nullable: true),
                    zero_reason = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    zero_explanation = table.Column<string>(type: "text", nullable: true),
                    score = table.Column<int>(type: "integer", nullable: false),
                    max_score = table.Column<int>(type: "integer", nullable: false),
                    strengths = table.Column<string>(type: "text", nullable: false),
                    main_issue = table.Column<string>(type: "text", nullable: false),
                    next_steps = table.Column<string>(type: "jsonb", nullable: false),
                    model = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    raw_response = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_corrections", x => x.id);
                    table.ForeignKey(
                        name: "fk_corrections_essays_essay_id",
                        column: x => x.essay_id,
                        principalTable: "essays",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_corrections_grading_profiles_grading_profile_id",
                        column: x => x.grading_profile_id,
                        principalTable: "grading_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_corrections_rubric_versions_rubric_version_id",
                        column: x => x.rubric_version_id,
                        principalTable: "rubric_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_corrections_theme_grading_notes_theme_grading_notes_id",
                        column: x => x.theme_grading_notes_id,
                        principalTable: "theme_grading_notes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_corrections_themes_theme_id",
                        column: x => x.theme_id,
                        principalTable: "themes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "correction_criteria",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    correction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    criterion_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    level = table.Column<int>(type: "integer", nullable: false),
                    points = table.Column<int>(type: "integer", nullable: false),
                    justification = table.Column<string>(type: "text", nullable: false),
                    guidance = table.Column<string>(type: "text", nullable: true),
                    lines = table.Column<int[]>(type: "integer[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_correction_criteria", x => x.id);
                    table.ForeignKey(
                        name: "fk_correction_criteria_corrections_correction_id",
                        column: x => x.correction_id,
                        principalTable: "corrections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_correction_criteria_rubric_criterion_criterion_id",
                        column: x => x.criterion_id,
                        principalTable: "rubric_criteria",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "correction_marks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    correction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    criterion_id = table.Column<Guid>(type: "uuid", nullable: false),
                    criterion_position = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    line_start = table.Column<int>(type: "integer", nullable: false),
                    line_end = table.Column<int>(type: "integer", nullable: false),
                    char_start = table.Column<int>(type: "integer", nullable: false),
                    char_end = table.Column<int>(type: "integer", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    rule_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_correction_marks", x => x.id);
                    table.ForeignKey(
                        name: "fk_correction_marks_corrections_correction_id",
                        column: x => x.correction_id,
                        principalTable: "corrections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_correction_marks_rubric_criterion_criterion_id",
                        column: x => x.criterion_id,
                        principalTable: "rubric_criteria",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_themes_supersedes_theme_id",
                table: "themes",
                column: "supersedes_theme_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_essays_grading_profile_id",
                table: "essays",
                column: "grading_profile_id");

            migrationBuilder.CreateIndex(
                name: "ix_correction_criteria_correction_id_position",
                table: "correction_criteria",
                columns: new[] { "correction_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_correction_criteria_criterion_id",
                table: "correction_criteria",
                column: "criterion_id");

            migrationBuilder.CreateIndex(
                name: "ix_correction_marks_correction_id",
                table: "correction_marks",
                column: "correction_id");

            migrationBuilder.CreateIndex(
                name: "ix_correction_marks_criterion_id",
                table: "correction_marks",
                column: "criterion_id");

            migrationBuilder.CreateIndex(
                name: "ix_corrections_essay_id",
                table: "corrections",
                column: "essay_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_corrections_grading_profile_id",
                table: "corrections",
                column: "grading_profile_id");

            migrationBuilder.CreateIndex(
                name: "ix_corrections_rubric_version_id",
                table: "corrections",
                column: "rubric_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_corrections_theme_grading_notes_id",
                table: "corrections",
                column: "theme_grading_notes_id");

            migrationBuilder.CreateIndex(
                name: "ix_corrections_theme_id",
                table: "corrections",
                column: "theme_id");

            migrationBuilder.AddForeignKey(
                name: "fk_essays_grading_profiles_grading_profile_id",
                table: "essays",
                column: "grading_profile_id",
                principalTable: "grading_profiles",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_themes_themes_supersedes_theme_id",
                table: "themes",
                column: "supersedes_theme_id",
                principalTable: "themes",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_essays_grading_profiles_grading_profile_id",
                table: "essays");

            migrationBuilder.DropForeignKey(
                name: "fk_themes_themes_supersedes_theme_id",
                table: "themes");

            migrationBuilder.DropTable(
                name: "correction_criteria");

            migrationBuilder.DropTable(
                name: "correction_marks");

            migrationBuilder.DropTable(
                name: "corrections");

            migrationBuilder.DropIndex(
                name: "ix_themes_supersedes_theme_id",
                table: "themes");

            migrationBuilder.DropIndex(
                name: "ix_essays_grading_profile_id",
                table: "essays");

            migrationBuilder.DropColumn(
                name: "supersedes_theme_id",
                table: "themes");

            migrationBuilder.DropColumn(
                name: "version",
                table: "themes");

            migrationBuilder.DropColumn(
                name: "grading_profile_id",
                table: "essays");
        }
    }
}
