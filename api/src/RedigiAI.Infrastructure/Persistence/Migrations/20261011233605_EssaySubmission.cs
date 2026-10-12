using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedigiAI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EssaySubmission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    public_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    bytes = table.Column<long>(type: "bigint", nullable: true),
                    mime = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    purge_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assets", x => x.id);
                    table.ForeignKey(
                        name: "fk_assets_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "essay_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_essay_batches", x => x.id);
                    table.ForeignKey(
                        name: "fk_essay_batches_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "essays",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    exam_id = table.Column<Guid>(type: "uuid", nullable: false),
                    theme_id = table.Column<Guid>(type: "uuid", nullable: false),
                    essay_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rubric_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    photo_asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    parent_essay_id = table.Column<Guid>(type: "uuid", nullable: true),
                    review_deadline_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_essays", x => x.id);
                    table.ForeignKey(
                        name: "fk_essays_assets_photo_asset_id",
                        column: x => x.photo_asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_essays_essay_batches_batch_id",
                        column: x => x.batch_id,
                        principalTable: "essay_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_essays_essay_types_essay_type_id",
                        column: x => x.essay_type_id,
                        principalTable: "essay_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_essays_essays_parent_essay_id",
                        column: x => x.parent_essay_id,
                        principalTable: "essays",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_essays_exams_exam_id",
                        column: x => x.exam_id,
                        principalTable: "exams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_essays_rubric_versions_rubric_version_id",
                        column: x => x.rubric_version_id,
                        principalTable: "rubric_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_essays_themes_theme_id",
                        column: x => x.theme_id,
                        principalTable: "themes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_essays_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_credit_ledger_essay_id",
                table: "credit_ledger",
                column: "essay_id");

            migrationBuilder.CreateIndex(
                name: "ix_assets_public_id",
                table: "assets",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_assets_purge_after",
                table: "assets",
                column: "purge_after",
                filter: "purge_after IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_assets_user_id",
                table: "assets",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_essay_batches_user_id_idempotency_key",
                table: "essay_batches",
                columns: new[] { "user_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_essays_batch_id",
                table: "essays",
                column: "batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_essays_essay_type_id",
                table: "essays",
                column: "essay_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_essays_exam_id",
                table: "essays",
                column: "exam_id");

            migrationBuilder.CreateIndex(
                name: "ix_essays_parent_essay_id",
                table: "essays",
                column: "parent_essay_id");

            migrationBuilder.CreateIndex(
                name: "ix_essays_photo_asset_id",
                table: "essays",
                column: "photo_asset_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_essays_rubric_version_id",
                table: "essays",
                column: "rubric_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_essays_status_review_deadline_at",
                table: "essays",
                columns: new[] { "status", "review_deadline_at" });

            migrationBuilder.CreateIndex(
                name: "ix_essays_theme_id",
                table: "essays",
                column: "theme_id");

            migrationBuilder.CreateIndex(
                name: "ix_essays_user_id_id",
                table: "essays",
                columns: new[] { "user_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_jobs_type_run_at",
                table: "jobs",
                columns: new[] { "type", "run_at" },
                filter: "status IN ('ready', 'running')");

            migrationBuilder.AddForeignKey(
                name: "fk_credit_ledger_essays_essay_id",
                table: "credit_ledger",
                column: "essay_id",
                principalTable: "essays",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_credit_ledger_essays_essay_id",
                table: "credit_ledger");

            migrationBuilder.DropTable(
                name: "essays");

            migrationBuilder.DropTable(
                name: "jobs");

            migrationBuilder.DropTable(
                name: "assets");

            migrationBuilder.DropTable(
                name: "essay_batches");

            migrationBuilder.DropIndex(
                name: "ix_credit_ledger_essay_id",
                table: "credit_ledger");
        }
    }
}
