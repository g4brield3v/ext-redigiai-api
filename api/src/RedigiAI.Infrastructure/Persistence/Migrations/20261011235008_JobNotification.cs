using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedigiAI.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Avisa os workers (LISTEN jobs) quando um job é gravado. O aviso só é entregue no commit da transação
    /// que criou o job, junto com a mudança que o disparou.
    /// </summary>
    public partial class JobNotification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE FUNCTION notify_job_inserted() RETURNS trigger AS $$
                BEGIN
                    PERFORM pg_notify('jobs', NEW.type);
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER jobs_notify_insert
                    AFTER INSERT ON jobs
                    FOR EACH ROW EXECUTE FUNCTION notify_job_inserted();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER jobs_notify_insert ON jobs;
                DROP FUNCTION notify_job_inserted();
                """);
        }
    }
}
