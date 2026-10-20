using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedigiAI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdminFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A flag nasce desligada para todos: o papel antigo só valia junto com a lista de e-mails, e uma conta que saiu da
            // lista podia manter role = admin. Quem é administrador recebe a flag à mão (docs/decisoes.md, 20/10/2026).
            migrationBuilder.AddColumn<bool>(
                name: "is_admin",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.DropColumn(
                name: "role",
                table: "users");

            migrationBuilder.DropColumn(
                name: "role_checked_session_id",
                table: "users");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "role",
                table: "users",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "student");

            migrationBuilder.Sql("UPDATE users SET role = 'admin' WHERE is_admin;");

            migrationBuilder.DropColumn(
                name: "is_admin",
                table: "users");

            migrationBuilder.AddColumn<string>(
                name: "role_checked_session_id",
                table: "users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }
    }
}
