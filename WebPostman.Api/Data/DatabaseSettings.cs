using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace WebPostman.Api.Data;

public sealed class DatabaseSettings
{
    public string Provider { get; }
    public string DataDirectory { get; }
    public string ConnectionString { get; }
    public Version MySqlVersion { get; }
    public bool ImportLegacyData { get; }

    public DatabaseSettings(IWebHostEnvironment environment, IConfiguration configuration)
    {
        DataDirectory = Path.GetFullPath(configuration["WebPostman:DataDirectory"] ?? Path.Combine(environment.ContentRootPath, "App_Data"));
        Provider = configuration["Database:Provider"] ?? "Sqlite";
        ImportLegacyData = configuration.GetValue<bool?>("Database:ImportLegacyData")
            ?? Provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase);
        MySqlVersion = Version.Parse(configuration["Database:MySqlVersion"] ?? "8.0.36");
        if (Provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(DataDirectory);
            ConnectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(DataDirectory, "accounts.db") }.ToString();
        }
        else if (Provider.Equals("MySql", StringComparison.OrdinalIgnoreCase))
            ConnectionString = configuration.GetConnectionString("WebPostman") is { Length: > 0 } connection ? connection
                : throw new InvalidOperationException("使用 MySQL 时必须设置 ConnectionStrings:WebPostman。");
        else throw new InvalidOperationException("Database:Provider 仅支持 Sqlite 或 MySql。");
    }

    public void Configure(DbContextOptionsBuilder options)
    {
        if (Provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase)) options.UseSqlite(ConnectionString);
        else options.UseMySql(ConnectionString, new MySqlServerVersion(MySqlVersion));
    }
}
