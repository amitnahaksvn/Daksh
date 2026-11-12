using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daksh.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExecutionVersionConcurrencyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // "xmin" is a PostgreSQL system column that already exists on every table; this migration only tells
            // EF Core to start treating it as a concurrency token for Executions (see PlatformDbContext). No schema
            // change is needed or possible - the scaffolded AddColumn would fail against a real server, since
            // "xmin" conflicts with a reserved system column name.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
