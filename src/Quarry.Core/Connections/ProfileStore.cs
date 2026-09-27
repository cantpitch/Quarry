using Quarry.Core.Credentials;

namespace Quarry.Core.Connections;

/// <summary>Saved connection profiles (JSON) plus their secrets (OS keychain).</summary>
public sealed class ProfileStore(ICredentialStore credentials, string? path = null)
{
    private readonly string _path = path ?? AppPaths.ConnectionsFile;
    private readonly object _gate = new();

    public ICredentialStore Credentials { get; } = credentials;

    public IReadOnlyList<ConnectionProfile> Load()
    {
        lock (_gate)
        {
            try
            {
                return JsonFile.Read<List<ConnectionProfile>>(_path) ?? [];
            }
            catch (Exception)
            {
                return [];
            }
        }
    }

    /// <summary>Adds or replaces the profile (matched by id) and updates its stored secret.</summary>
    public void Save(ConnectionProfile profile, string? secret)
    {
        lock (_gate)
        {
            var profiles = Load().Where(p => p.Id != profile.Id).ToList();
            profiles.Add(profile);
            JsonFile.Write(_path, profiles.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList());
        }

        if (!Credentials.IsAvailable)
            return;
        if (profile.SaveSecret && profile.Authentication.RequiresSecret() && !string.IsNullOrEmpty(secret))
            Credentials.Set(profile.CredentialKey, secret);
        else
            Credentials.Delete(profile.CredentialKey);
    }

    public void Delete(ConnectionProfile profile)
    {
        lock (_gate)
        {
            JsonFile.Write(_path, Load().Where(p => p.Id != profile.Id).ToList());
        }
        if (Credentials.IsAvailable)
            Credentials.Delete(profile.CredentialKey);
    }

    public string? GetSecret(ConnectionProfile profile)
        => Credentials.IsAvailable && profile.SaveSecret ? Credentials.Get(profile.CredentialKey) : null;
}
