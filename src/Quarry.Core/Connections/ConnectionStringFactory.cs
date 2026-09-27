using Microsoft.Data.SqlClient;

namespace Quarry.Core.Connections;

public static class ConnectionStringFactory
{
    public const string ApplicationName = "Quarry";

    /// <param name="secret">Password or client secret, when the authentication kind needs one.</param>
    /// <param name="database">Overrides the profile's default database.</param>
    public static string Build(ConnectionProfile profile, string? secret, string? database = null)
    {
        if (!profile.Authentication.IsSupportedOnThisPlatform())
            throw new PlatformNotSupportedException($"{profile.Authentication.DisplayName()} is only supported on Windows.");

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = profile.Server,
            ApplicationName = ApplicationName,
            ConnectTimeout = profile.ConnectTimeoutSeconds,
            TrustServerCertificate = profile.TrustServerCertificate,
            Encrypt = profile.Encrypt switch
            {
                EncryptMode.Optional => SqlConnectionEncryptOption.Optional,
                EncryptMode.Strict => SqlConnectionEncryptOption.Strict,
                _ => SqlConnectionEncryptOption.Mandatory,
            },
            MultipleActiveResultSets = false,
        };

        string? db = string.IsNullOrWhiteSpace(database) ? profile.Database : database;
        if (!string.IsNullOrWhiteSpace(db))
            builder.InitialCatalog = db;

        switch (profile.Authentication)
        {
            case AuthenticationKind.SqlPassword:
                builder.UserID = profile.UserName ?? "";
                builder.Password = secret ?? "";
                break;
            case AuthenticationKind.WindowsIntegrated:
                builder.IntegratedSecurity = true;
                break;
            case AuthenticationKind.EntraInteractive:
                builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryInteractive;
                if (!string.IsNullOrWhiteSpace(profile.UserName))
                    builder.UserID = profile.UserName;
                break;
            case AuthenticationKind.EntraDefault:
                builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryDefault;
                break;
            case AuthenticationKind.EntraServicePrincipal:
                builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryServicePrincipal;
                builder.UserID = profile.UserName ?? "";
                builder.Password = secret ?? "";
                break;
            case AuthenticationKind.EntraDeviceCode:
                builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryDeviceCodeFlow;
                break;
        }

        return builder.ConnectionString;
    }
}
