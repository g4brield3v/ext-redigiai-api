using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedigiAI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SubscriptionCreationLock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "creating_until",
                table: "subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            // Antes do índice: de cada aluno com mais de uma assinatura pendente, fica só a mais recente.
            migrationBuilder.Sql("""
                UPDATE subscriptions
                SET status = 'cancelled', cancelled_at = now(), updated_at = now()
                WHERE status = 'pending'
                  AND id NOT IN (
                      SELECT DISTINCT ON (user_id) id FROM subscriptions
                      WHERE status = 'pending'
                      ORDER BY user_id, created_at DESC);
                """);

            migrationBuilder.CreateIndex(
                name: "ux_subscriptions_pending_per_user",
                table: "subscriptions",
                column: "user_id",
                unique: true,
                filter: "status = 'pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_subscriptions_pending_per_user",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "creating_until",
                table: "subscriptions");
        }
    }
}
