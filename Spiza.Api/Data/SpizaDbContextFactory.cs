using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Spiza.Api.Data;

public sealed class SpizaDbContextFactory : IDesignTimeDbContextFactory<SpizaDbContext>
{
    public SpizaDbContext CreateDbContext(string[] args)
    {
        var databasePath = Environment.GetEnvironmentVariable("SPIZA_DATABASE_PATH") ?? "data/spiza.db";
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true
        }.ToString();
        var options = new DbContextOptionsBuilder<SpizaDbContext>().UseSqlite(connectionString).Options;
        return new SpizaDbContext(options);
    }
}
