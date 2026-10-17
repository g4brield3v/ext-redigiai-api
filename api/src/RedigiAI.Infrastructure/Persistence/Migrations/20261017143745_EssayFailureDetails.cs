using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedigiAI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EssayFailureDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "failed_at",
                table: "essays",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "failure_details",
                table: "essays",
                type: "jsonb",
                nullable: true);

            // Redações que já falharam: sem código vira INTERNAL_ERROR; a data é a da mudança de status gravada na
            // trilha de auditoria, ou a do envio se não houver registro. Os detalhes antigos não existem.
            migrationBuilder.Sql(
                """
                UPDATE essays e
                SET failure_code = COALESCE(e.failure_code, 'INTERNAL_ERROR'),
                    failed_at = COALESCE(
                        (SELECT max(a.created_at)
                         FROM audit_log a
                         WHERE a.entity = 'essay'
                           AND a.entity_id = e.id
                           AND a.action = 'essay.status_changed'
                           AND a.data ->> 'to' IN ('Failed', 'Cancelled')),
                        e.created_at)
                WHERE e.status IN ('failed', 'cancelled');
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_essays_failure_code",
                table: "essays",
                sql: "status NOT IN ('failed', 'cancelled') OR (failure_code IS NOT NULL AND failed_at IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_essays_failure_code",
                table: "essays");

            migrationBuilder.DropColumn(
                name: "failed_at",
                table: "essays");

            migrationBuilder.DropColumn(
                name: "failure_details",
                table: "essays");
        }
    }
}
