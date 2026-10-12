using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedigiAI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Transcription : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_usage",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    essay_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stage = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    model = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    input_tokens = table.Column<int>(type: "integer", nullable: false),
                    output_tokens = table.Column<int>(type: "integer", nullable: false),
                    cache_read_tokens = table.Column<int>(type: "integer", nullable: false),
                    cache_write_tokens = table.Column<int>(type: "integer", nullable: false),
                    cost_micro_usd = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_usage", x => x.id);
                    table.ForeignKey(
                        name: "fk_ai_usage_essays_essay_id",
                        column: x => x.essay_id,
                        principalTable: "essays",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "transcriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    essay_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lines = table.Column<string>(type: "jsonb", nullable: false),
                    illegible_count = table.Column<int>(type: "integer", nullable: false),
                    identification_found = table.Column<bool>(type: "boolean", nullable: false),
                    model = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transcriptions", x => x.id);
                    table.ForeignKey(
                        name: "fk_transcriptions_essays_essay_id",
                        column: x => x.essay_id,
                        principalTable: "essays",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "illegible_words",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    transcription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    char_start = table.Column<int>(type: "integer", nullable: false),
                    char_end = table.Column<int>(type: "integer", nullable: false),
                    student_value = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    filled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_illegible_words", x => x.id);
                    table.ForeignKey(
                        name: "fk_illegible_words_transcriptions_transcription_id",
                        column: x => x.transcription_id,
                        principalTable: "transcriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_created_at",
                table: "ai_usage",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_essay_id",
                table: "ai_usage",
                column: "essay_id");

            migrationBuilder.CreateIndex(
                name: "ix_illegible_words_transcription_id",
                table: "illegible_words",
                column: "transcription_id");

            migrationBuilder.CreateIndex(
                name: "ix_transcriptions_essay_id",
                table: "transcriptions",
                column: "essay_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_usage");

            migrationBuilder.DropTable(
                name: "illegible_words");

            migrationBuilder.DropTable(
                name: "transcriptions");
        }
    }
}
