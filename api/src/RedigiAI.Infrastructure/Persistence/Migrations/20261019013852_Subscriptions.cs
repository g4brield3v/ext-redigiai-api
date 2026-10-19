using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedigiAI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Subscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "grant_key",
                table: "credit_buckets",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mp_preapproval_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    next_billing_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    card_last4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscriptions", x => x.id);
                    table.CheckConstraint("ck_subscriptions_card_last4", "card_last4 IS NULL OR card_last4 ~ '^[0-9]{4}$'");
                    table.ForeignKey(
                        name: "fk_subscriptions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_credit_buckets_expires_at",
                table: "credit_buckets",
                column: "expires_at",
                filter: "remaining > 0 AND expires_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_credit_buckets_grant_key",
                table: "credit_buckets",
                column: "grant_key",
                unique: true,
                filter: "grant_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_mp_preapproval_id",
                table: "subscriptions",
                column: "mp_preapproval_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_status",
                table: "subscriptions",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_user_id",
                table: "subscriptions",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "subscriptions");

            migrationBuilder.DropIndex(
                name: "ix_credit_buckets_expires_at",
                table: "credit_buckets");

            migrationBuilder.DropIndex(
                name: "ix_credit_buckets_grant_key",
                table: "credit_buckets");

            migrationBuilder.DropColumn(
                name: "grant_key",
                table: "credit_buckets");
        }
    }
}
