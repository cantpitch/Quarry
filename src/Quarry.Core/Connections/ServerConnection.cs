using Microsoft.Data.SqlClient;
using Quarry.Core.Metadata;

namespace Quarry.Core.Connections;

/// <summary>
/// A connected server: the profile plus the secret used for this session. Creates connections for
/// editor tabs and owns the server's metadata cache.
/// </summary>
public sealed class ServerConnection
{
    private readonly string? _secret;

    public ServerConnection(ConnectionProfile profile, string? secret)
    {
        Profile = profile;
        _secret = secret;
        Metadata = new MetadataCache(new MetadataService(CreateConnection));
    }

    public ConnectionProfile Profile { get; }

    public MetadataCache Metadata { get; }

    public ServerInfo? Info { get; private set; }

    public string DisplayName => Info is { } info && !string.IsNullOrEmpty(info.Login)
        ? $"{Profile.DisplayName} ({info.Login})"
        : Profile.DisplayName;

    public SqlConnection CreateConnection(string? database = null)
        => new(ConnectionStringFactory.Build(Profile, _secret, database));

    /// <summary>Opens a connection to verify credentials and reads server details.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        Info = await Metadata.Service.GetServerInfoAsync(ct);
    }
}
