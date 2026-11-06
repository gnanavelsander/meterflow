using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;
namespace MeterFlow.Tests;
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? container;
    public string ConnectionString { get; private set; } = "";
    public async Task InitializeAsync()
    {
        ConnectionString = Environment.GetEnvironmentVariable("METERFLOW_TEST_DB") ?? "";
        if (ConnectionString.Length > 0) return;
        container = new PostgreSqlBuilder("postgres:17").Build();
        await container.StartAsync(); ConnectionString = container.GetConnectionString();
    }
    public async Task DisposeAsync() { if (container is not null) await container.DisposeAsync(); }
}
public sealed class ApiFactory(string connection, string keyPath, bool enableWorker = false) : WebApplicationFactory<Program>
{
    public string ConnectionString => connection;
    private string admin = "";
    private string database = "";
    public static async Task<ApiFactory> Create(string adminConnection)
    {
        var name = "test_" + Guid.NewGuid().ToString("N");
        await using var conn = new NpgsqlConnection(adminConnection); await conn.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", conn); await command.ExecuteNonQueryAsync();
        var builder = new NpgsqlConnectionStringBuilder(adminConnection) { Database = name };
        return new ApiFactory(builder.ConnectionString, Path.Combine(Path.GetTempPath(), name)) { admin = adminConnection, database = name };
    }
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureAppConfiguration((_, config) =>
        config.AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["ConnectionStrings:Database"] = connection, ["Database:MigrateOnStartup"] = "true",
            ["Worker:Enabled"] = enableWorker.ToString(), ["DataProtection:KeyPath"] = keyPath
        }));
    public async Task DisposeDatabase()
    {
        await DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(admin); await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", conn); await cmd.ExecuteNonQueryAsync();
        if (Directory.Exists(keyPath)) Directory.Delete(keyPath, true);
    }
}
