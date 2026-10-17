using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedigiAI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PromptCreationKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_prompts_created_by",
                table: "prompts");

            migrationBuilder.AddColumn<string>(
                name: "creation_key",
                table: "prompts",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_prompts_created_by_creation_key",
                table: "prompts",
                columns: new[] { "created_by", "creation_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_prompts_created_by_creation_key",
                table: "prompts");

            migrationBuilder.DropColumn(
                name: "creation_key",
                table: "prompts");

            migrationBuilder.CreateIndex(
                name: "ix_prompts_created_by",
                table: "prompts",
                column: "created_by");
        }
    }
}
