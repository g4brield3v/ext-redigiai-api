using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedigiAI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Payments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mp_payment_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    amount_cents = table.Column<long>(type: "bigint", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    guardian_authorized_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    pix_qr_code = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    pix_qr_code_base64 = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    pix_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payments", x => x.id);
                    table.CheckConstraint("ck_payments_amount", "amount_cents > 0 AND quantity >= 1");
                    table.CheckConstraint("ck_payments_approved_at", "status <> 'approved' OR approved_at IS NOT NULL");
                    table.ForeignKey(
                        name: "fk_payments_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "webhook_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    topic = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    resource_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_credit_ledger_payment_id",
                table: "credit_ledger",
                column: "payment_id");

            migrationBuilder.CreateIndex(
                name: "ix_payments_mp_payment_id",
                table: "payments",
                column: "mp_payment_id",
                unique: true,
                filter: "mp_payment_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_payments_user_id_created_at",
                table: "payments",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_payments_user_id_idempotency_key",
                table: "payments",
                columns: new[] { "user_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_webhook_events_provider_external_id",
                table: "webhook_events",
                columns: new[] { "provider", "external_id" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_credit_ledger_payments_payment_id",
                table: "credit_ledger",
                column: "payment_id",
                principalTable: "payments",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_credit_ledger_payments_payment_id",
                table: "credit_ledger");

            migrationBuilder.DropTable(
                name: "payments");

            migrationBuilder.DropTable(
                name: "webhook_events");

            migrationBuilder.DropIndex(
                name: "ix_credit_ledger_payment_id",
                table: "credit_ledger");
        }
    }
}
