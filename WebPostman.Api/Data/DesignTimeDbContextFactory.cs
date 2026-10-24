using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace WebPostman.Api.Data;

// Tooling does not start the web app or import any user databases.
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<WebPostmanDbContext>
{
    public WebPostmanDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true).AddEnvironmentVariables().AddCommandLine(args).Build();
        var options = new DbContextOptionsBuilder<WebPostmanDbContext>();
        var provider = configuration["Database:Provider"] ?? "Sqlite";
        if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase)) options.UseSqlite("Data Source=design-time.db");
        else if (provider.Equals("MySql", StringComparison.OrdinalIgnoreCase))
            options.UseMySql(configuration.GetConnectionString("WebPostman") ?? "Server=localhost;Database=webpostman;User=root;Password=unused;", new MySqlServerVersion(Version.Parse(configuration["Database:MySqlVersion"] ?? "8.0.36")));
        else throw new InvalidOperationException("Database:Provider 仅支持 Sqlite 或 MySql。");
        return new(options.Options);
    }
}
