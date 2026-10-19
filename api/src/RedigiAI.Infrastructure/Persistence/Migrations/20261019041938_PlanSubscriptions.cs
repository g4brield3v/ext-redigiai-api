using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedigiAI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PlanSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_subscriptions_mp_preapproval_id",
                table: "subscriptions");

            migrationBuilder.AlterColumn<string>(
                name: "mp_preapproval_id",
                table: "subscriptions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cancelled_at",
                table: "subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "checkout_url",
                table: "subscriptions",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "guardian_authorized_at",
                table: "subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "idempotency_key",
                table: "subscriptions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "subscription_id",
                table: "payments",
                type: "uuid",
                nullable: true);

            // Assinaturas gravadas antes desta migração (sem pedido de checkout) ganham uma chave própria, para o índice
            // único por aluno e chave.
            migrationBuilder.Sql("UPDATE subscriptions SET idempotency_key = id::text WHERE idempotency_key = '';");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_mp_preapproval_id",
                table: "subscriptions",
                column: "mp_preapproval_id",
                unique: true,
                filter: "mp_preapproval_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_user_id_idempotency_key",
                table: "subscriptions",
                columns: new[] { "user_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payments_subscription_id",
                table: "payments",
                column: "subscription_id");

            migrationBuilder.AddForeignKey(
                name: "fk_payments_subscriptions_subscription_id",
                table: "payments",
                column: "subscription_id",
                principalTable: "subscriptions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_payments_subscriptions_subscription_id",
                table: "payments");

            migrationBuilder.DropIndex(
                name: "ix_subscriptions_mp_preapproval_id",
                table: "subscriptions");

            migrationBuilder.DropIndex(
                name: "ix_subscriptions_user_id_idempotency_key",
                table: "subscriptions");

            migrationBuilder.DropIndex(
                name: "ix_payments_subscription_id",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "cancelled_at",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "checkout_url",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "guardian_authorized_at",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "idempotency_key",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "subscription_id",
                table: "payments");

            // A coluna volta a ser obrigatória: pedidos de assinatura que não chegaram ao Mercado Pago ganham um id próprio.
            migrationBuilder.Sql("UPDATE subscriptions SET mp_preapproval_id = 'pending-' || id WHERE mp_preapproval_id IS NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "mp_preapproval_id",
                table: "subscriptions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_mp_preapproval_id",
                table: "subscriptions",
                column: "mp_preapproval_id",
                unique: true);
        }
    }
}
