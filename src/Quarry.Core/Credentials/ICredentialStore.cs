namespace Quarry.Core.Credentials;

/// <summary>Stores secrets in the operating system's credential store.</summary>
public interface ICredentialStore
{
    /// <summary>False when the platform store cannot be used (e.g. libsecret missing); secrets are then not persisted.</summary>
    bool IsAvailable { get; }

    string? Get(string key);

    void Set(string key, string secret);

    void Delete(string key);
}

public static class CredentialStore
{
    /// <summary>Creates the store for the current OS, falling back to a no-op store when it is unavailable.</summary>
    public static ICredentialStore CreateDefault()
    {
        ICredentialStore store;
        if (OperatingSystem.IsWindows())
            store = new WindowsCredentialStore();
        else if (OperatingSystem.IsMacOS())
            store = new MacKeychainCredentialStore();
        else if (OperatingSystem.IsLinux())
            store = new LibSecretCredentialStore();
        else
            store = new NullCredentialStore();

        return store.IsAvailable ? store : new NullCredentialStore();
    }
}

/// <summary>Persists nothing; users are asked for secrets each session.</summary>
public sealed class NullCredentialStore : ICredentialStore
{
    public bool IsAvailable => false;

    public string? Get(string key) => null;

    public void Set(string key, string secret) { }

    public void Delete(string key) { }
}

/// <summary>Keeps secrets in memory only; for tests.</summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _secrets = [];

    public bool IsAvailable => true;

    public string? Get(string key) => _secrets.GetValueOrDefault(key);

    public void Set(string key, string secret) => _secrets[key] = secret;

    public void Delete(string key) => _secrets.Remove(key);
}
