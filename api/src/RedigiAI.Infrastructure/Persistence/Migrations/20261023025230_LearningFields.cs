using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedigiAI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LearningFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "answer",
                table: "correction_marks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "answer_kind",
                table: "correction_marks",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ask_label",
                table: "correction_marks",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "why",
                table: "correction_marks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "example",
                table: "correction_criteria",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "plain_explanation",
                table: "correction_criteria",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "answer",
                table: "correction_marks");

            migrationBuilder.DropColumn(
                name: "answer_kind",
                table: "correction_marks");

            migrationBuilder.DropColumn(
                name: "ask_label",
                table: "correction_marks");

            migrationBuilder.DropColumn(
                name: "why",
                table: "correction_marks");

            migrationBuilder.DropColumn(
                name: "example",
                table: "correction_criteria");

            migrationBuilder.DropColumn(
                name: "plain_explanation",
                table: "correction_criteria");
        }
    }
}
