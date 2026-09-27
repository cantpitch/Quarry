using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Quarry.Core.Tests;

/// <summary>
/// Provides a SQL Server for integration tests: the QUARRY_TEST_CONNECTION connection string when set,
/// otherwise a Testcontainers SQL Server 2022 container (requires Docker; set QUARRY_NO_DOCKER=1 to skip instead).
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string EnvironmentVariable = "QUARRY_TEST_CONNECTION";

    private MsSqlContainer? _container;

    public string ConnectionString { get; private set; } = "";

    public static bool IsAvailable
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable)) || DockerLikelyAvailable();

    public async Task InitializeAsync()
    {
        string? configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            ConnectionString = configured;
            return;
        }
        if (!IsAvailable)
            return;

        _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    public async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static bool DockerLikelyAvailable()
    {
        // CI runners may expose Docker without being able to run the Linux SQL Server image.
        if (Environment.GetEnvironmentVariable("QUARRY_NO_DOCKER") is "1" or "true")
            return false;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOCKER_HOST")))
            return true;
        return OperatingSystem.IsWindows()
            ? File.Exists(@"\\.\pipe\docker_engine")
            : File.Exists("/var/run/docker.sock");
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SqlServer";
}

/// <summary>A fact that is skipped when no SQL Server is available.</summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (!SqlServerFixture.IsAvailable)
            Skip = $"No SQL Server: set {SqlServerFixture.EnvironmentVariable} or run Docker.";
    }
}
