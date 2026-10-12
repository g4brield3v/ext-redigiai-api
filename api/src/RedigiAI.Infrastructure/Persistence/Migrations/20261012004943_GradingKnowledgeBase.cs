using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedigiAI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GradingKnowledgeBase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "max_level",
                table: "rubric_versions",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "rubric_versions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "max_points",
                table: "rubric_criteria",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // As grades que já existiam tinham 3 níveis: pontos máximos = 3 × peso.
            migrationBuilder.Sql("UPDATE rubric_criteria SET max_points = 3 * weight;");

            migrationBuilder.CreateTable(
                name: "anchor_sets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    exam_id = table.Column<Guid>(type: "uuid", nullable: false),
                    essay_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_anchor_sets", x => x.id);
                    table.ForeignKey(
                        name: "fk_anchor_sets_essay_types_essay_type_id",
                        column: x => x.essay_type_id,
                        principalTable: "essay_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_anchor_sets_exams_exam_id",
                        column: x => x.exam_id,
                        principalTable: "exams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "prompt_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    system_text = table.Column<string>(type: "text", nullable: false),
                    user_text = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_prompt_templates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "reference_essays",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    exam_id = table.Column<Guid>(type: "uuid", nullable: false),
                    essay_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    theme_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    lines = table.Column<string>(type: "jsonb", nullable: false),
                    levels = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reference_essays", x => x.id);
                    table.ForeignKey(
                        name: "fk_reference_essays_essay_types_essay_type_id",
                        column: x => x.essay_type_id,
                        principalTable: "essay_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reference_essays_exams_exam_id",
                        column: x => x.exam_id,
                        principalTable: "exams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reference_essays_themes_theme_id",
                        column: x => x.theme_id,
                        principalTable: "themes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "rubric_guideline_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    exam_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    general_rules = table.Column<string>(type: "text", nullable: false),
                    guidance_writing = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rubric_guideline_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_rubric_guideline_versions_exams_exam_id",
                        column: x => x.exam_id,
                        principalTable: "exams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "theme_grading_notes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    theme_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
                name: "writing_rule_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_writing_rule_versions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "anchor_essays",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    anchor_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    band = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    lines = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_anchor_essays", x => x.id);
                    table.ForeignKey(
                        name: "fk_anchor_essays_anchor_set_anchor_set_id",
                        column: x => x.anchor_set_id,
                        principalTable: "anchor_sets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "rubric_guidelines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    guidelines_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    criterion_position = table.Column<int>(type: "integer", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rubric_guidelines", x => x.id);
                    table.ForeignKey(
                        name: "fk_rubric_guidelines_rubric_guideline_versions_guidelines_vers",
                        column: x => x.guidelines_version_id,
                        principalTable: "rubric_guideline_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "grading_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    exam_id = table.Column<Guid>(type: "uuid", nullable: false),
                    essay_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    transcription_prompt_template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    grading_prompt_template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rubric_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    guidelines_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    writing_rules_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    anchor_set_id = table.Column<Guid>(type: "uuid", nullable: true),
                    transcription_model = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    grading_model = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_grading_profiles", x => x.id);
                    table.ForeignKey(
                        name: "fk_grading_profiles_anchor_sets_anchor_set_id",
                        column: x => x.anchor_set_id,
                        principalTable: "anchor_sets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_grading_profiles_essay_types_essay_type_id",
                        column: x => x.essay_type_id,
                        principalTable: "essay_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_grading_profiles_exams_exam_id",
                        column: x => x.exam_id,
                        principalTable: "exams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_grading_profiles_guidelines_versions_guidelines_version_id",
                        column: x => x.guidelines_version_id,
                        principalTable: "rubric_guideline_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_grading_profiles_prompt_templates_grading_prompt_template_id",
                        column: x => x.grading_prompt_template_id,
                        principalTable: "prompt_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_grading_profiles_prompt_templates_transcription_prompt_temp",
                        column: x => x.transcription_prompt_template_id,
                        principalTable: "prompt_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_grading_profiles_rubric_versions_rubric_version_id",
                        column: x => x.rubric_version_id,
                        principalTable: "rubric_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_grading_profiles_writing_rules_versions_writing_rules_versi",
                        column: x => x.writing_rules_version_id,
                        principalTable: "writing_rule_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "writing_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    writing_rules_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    explanation = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    wrong_example = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    right_example = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_writing_rules", x => x.id);
                    table.ForeignKey(
                        name: "fk_writing_rules_writing_rules_versions_writing_rules_version_",
                        column: x => x.writing_rules_version_id,
                        principalTable: "writing_rule_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "anchor_scores",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    anchor_essay_id = table.Column<Guid>(type: "uuid", nullable: false),
                    criterion_position = table.Column<int>(type: "integer", nullable: false),
                    level = table.Column<int>(type: "integer", nullable: false),
                    justification = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_anchor_scores", x => x.id);
                    table.ForeignKey(
                        name: "fk_anchor_scores_anchor_essays_anchor_essay_id",
                        column: x => x.anchor_essay_id,
                        principalTable: "anchor_essays",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "eval_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    grading_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    results = table.Column<string>(type: "jsonb", nullable: true),
                    cost_micro_usd = table.Column<long>(type: "bigint", nullable: true),
                    passed = table.Column<bool>(type: "boolean", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_eval_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_eval_runs_grading_profiles_grading_profile_id",
                        column: x => x.grading_profile_id,
                        principalTable: "grading_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_anchor_essays_anchor_set_id",
                table: "anchor_essays",
                column: "anchor_set_id");

            migrationBuilder.CreateIndex(
                name: "ix_anchor_scores_anchor_essay_id_criterion_position",
                table: "anchor_scores",
                columns: new[] { "anchor_essay_id", "criterion_position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_anchor_sets_essay_type_id",
                table: "anchor_sets",
                column: "essay_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_anchor_sets_exam_id_essay_type_id_version",
                table: "anchor_sets",
                columns: new[] { "exam_id", "essay_type_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_eval_runs_grading_profile_id",
                table: "eval_runs",
                column: "grading_profile_id");

            migrationBuilder.CreateIndex(
                name: "ix_grading_profiles_anchor_set_id",
                table: "grading_profiles",
                column: "anchor_set_id");

            migrationBuilder.CreateIndex(
                name: "ix_grading_profiles_essay_type_id",
                table: "grading_profiles",
                column: "essay_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_grading_profiles_exam_id_essay_type_id_version",
                table: "grading_profiles",
                columns: new[] { "exam_id", "essay_type_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_grading_profiles_grading_prompt_template_id",
                table: "grading_profiles",
                column: "grading_prompt_template_id");

            migrationBuilder.CreateIndex(
                name: "ix_grading_profiles_guidelines_version_id",
                table: "grading_profiles",
                column: "guidelines_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_grading_profiles_rubric_version_id",
                table: "grading_profiles",
                column: "rubric_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_grading_profiles_transcription_prompt_template_id",
                table: "grading_profiles",
                column: "transcription_prompt_template_id");

            migrationBuilder.CreateIndex(
                name: "ix_grading_profiles_writing_rules_version_id",
                table: "grading_profiles",
                column: "writing_rules_version_id");

            migrationBuilder.CreateIndex(
                name: "ux_grading_profiles_active",
                table: "grading_profiles",
                columns: new[] { "exam_id", "essay_type_id" },
                unique: true,
                filter: "status = 'active'");

            migrationBuilder.CreateIndex(
                name: "ix_prompt_templates_kind_version",
                table: "prompt_templates",
                columns: new[] { "kind", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reference_essays_essay_type_id",
                table: "reference_essays",
                column: "essay_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_reference_essays_exam_id",
                table: "reference_essays",
                column: "exam_id");

            migrationBuilder.CreateIndex(
                name: "ix_reference_essays_theme_id",
                table: "reference_essays",
                column: "theme_id");

            migrationBuilder.CreateIndex(
                name: "ix_rubric_guideline_versions_exam_id_version",
                table: "rubric_guideline_versions",
                columns: new[] { "exam_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_rubric_guidelines_guidelines_version_id_criterion_position",
                table: "rubric_guidelines",
                columns: new[] { "guidelines_version_id", "criterion_position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_theme_grading_notes_theme_id_version",
                table: "theme_grading_notes",
                columns: new[] { "theme_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_writing_rule_versions_version",
                table: "writing_rule_versions",
                column: "version",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_writing_rules_writing_rules_version_id_code",
                table: "writing_rules",
                columns: new[] { "writing_rules_version_id", "code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "anchor_scores");

            migrationBuilder.DropTable(
                name: "eval_runs");

            migrationBuilder.DropTable(
                name: "reference_essays");

            migrationBuilder.DropTable(
                name: "rubric_guidelines");

            migrationBuilder.DropTable(
                name: "theme_grading_notes");

            migrationBuilder.DropTable(
                name: "writing_rules");

            migrationBuilder.DropTable(
                name: "anchor_essays");

            migrationBuilder.DropTable(
                name: "grading_profiles");

            migrationBuilder.DropTable(
                name: "anchor_sets");

            migrationBuilder.DropTable(
                name: "rubric_guideline_versions");

            migrationBuilder.DropTable(
                name: "prompt_templates");

            migrationBuilder.DropTable(
                name: "writing_rule_versions");

            migrationBuilder.DropColumn(
                name: "max_level",
                table: "rubric_versions");

            migrationBuilder.DropColumn(
                name: "source",
                table: "rubric_versions");

            migrationBuilder.DropColumn(
                name: "max_points",
                table: "rubric_criteria");
        }
    }
}
