using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedigiAI.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// O plano deixa de ser semanal (3 correções por segunda-feira) e passa a mensal (12 por cobrança): a origem
    /// <c>plan_week</c> vira <c>plan</c> nos buckets e no ledger. Só dados; o esquema não muda.
    /// </summary>
    public partial class MonthlyPlan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE credit_buckets SET source = 'plan' WHERE source = 'plan_week';");
            migrationBuilder.Sql("UPDATE credit_ledger SET source = 'plan' WHERE source = 'plan_week';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE credit_buckets SET source = 'plan_week' WHERE source = 'plan';");
            migrationBuilder.Sql("UPDATE credit_ledger SET source = 'plan_week' WHERE source = 'plan';");
        }
    }
}
