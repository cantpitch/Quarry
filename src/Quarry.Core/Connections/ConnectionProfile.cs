namespace Quarry.Core.Connections;

public enum AuthenticationKind
{
    SqlPassword,
    WindowsIntegrated,
    EntraInteractive,
    EntraDefault,
    EntraServicePrincipal,
    EntraDeviceCode,
}

public enum EncryptMode
{
    Mandatory,
    Optional,
    Strict,
}

/// <summary>A saved connection. Secrets (passwords, client secrets) are not stored here but in the OS keychain.</summary>
public sealed record ConnectionProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Optional friendly name; the server name is shown when empty.</summary>
    public string? Name { get; init; }

    public string Server { get; init; } = "";

    public AuthenticationKind Authentication { get; init; } = AuthenticationKind.SqlPassword;

    /// <summary>SQL login, Entra login hint, or service principal client id, depending on <see cref="Authentication"/>.</summary>
    public string? UserName { get; init; }

    public string? Database { get; init; }

    public EncryptMode Encrypt { get; init; } = EncryptMode.Mandatory;

    public bool TrustServerCertificate { get; init; }

    public int ConnectTimeoutSeconds { get; init; } = 15;

    /// <summary>Whether the secret should be saved in the OS keychain.</summary>
    public bool SaveSecret { get; init; } = true;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Server : Name;

    /// <summary>Key under which the secret is stored in the credential store.</summary>
    public string CredentialKey => $"Quarry/{Id:N}";
}

public static class AuthenticationKindExtensions
{
    /// <summary>Whether the authentication kind needs a password or client secret.</summary>
    public static bool RequiresSecret(this AuthenticationKind kind)
        => kind is AuthenticationKind.SqlPassword or AuthenticationKind.EntraServicePrincipal;

    /// <summary>Whether a user name (login, login hint, or client id) is used.</summary>
    public static bool UsesUserName(this AuthenticationKind kind)
        => kind is AuthenticationKind.SqlPassword or AuthenticationKind.EntraInteractive or AuthenticationKind.EntraServicePrincipal;

    public static bool IsSupportedOnThisPlatform(this AuthenticationKind kind)
        => kind != AuthenticationKind.WindowsIntegrated || OperatingSystem.IsWindows();

    public static string DisplayName(this AuthenticationKind kind) => kind switch
    {
        AuthenticationKind.SqlPassword => "SQL Server Authentication",
        AuthenticationKind.WindowsIntegrated => "Windows Authentication",
        AuthenticationKind.EntraInteractive => "Microsoft Entra – Interactive (MFA)",
        AuthenticationKind.EntraDefault => "Microsoft Entra – Default credential",
        AuthenticationKind.EntraServicePrincipal => "Microsoft Entra – Service principal",
        AuthenticationKind.EntraDeviceCode => "Microsoft Entra – Device code",
        _ => kind.ToString(),
    };

    public static IReadOnlyList<AuthenticationKind> Available()
        => Enum.GetValues<AuthenticationKind>().Where(k => k.IsSupportedOnThisPlatform()).ToArray();
}
