using Microsoft.Data.SqlClient;
using Quarry.Core.Connections;
using Quarry.Core.Credentials;

namespace Quarry.Core.Tests;

public class ConnectionTests
{
    [Fact]
    public void SqlPassword_SetsUserAndPassword()
    {
        var profile = new ConnectionProfile { Server = "db1", UserName = "sa", Database = "app" };
        var builder = new SqlConnectionStringBuilder(ConnectionStringFactory.Build(profile, "secret"));
        Assert.Equal("db1", builder.DataSource);
        Assert.Equal("sa", builder.UserID);
        Assert.Equal("secret", builder.Password);
        Assert.Equal("app", builder.InitialCatalog);
        Assert.Equal("Quarry", builder.ApplicationName);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, builder.Encrypt);
    }

    [Fact]
    public void DatabaseOverride_WinsOverProfileDefault()
    {
        var profile = new ConnectionProfile { Server = "db1", Database = "app", Authentication = AuthenticationKind.EntraDefault };
        var builder = new SqlConnectionStringBuilder(ConnectionStringFactory.Build(profile, null, "other"));
        Assert.Equal("other", builder.InitialCatalog);
        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryDefault, builder.Authentication);
    }

    [Fact]
    public void EntraInteractive_UsesLoginHint()
    {
        var profile = new ConnectionProfile { Server = "x.database.windows.net", UserName = "me@contoso.com", Authentication = AuthenticationKind.EntraInteractive };
        var builder = new SqlConnectionStringBuilder(ConnectionStringFactory.Build(profile, null));
        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryInteractive, builder.Authentication);
        Assert.Equal("me@contoso.com", builder.UserID);
        Assert.Equal("", builder.Password);
    }

    [Fact]
    public void WindowsIntegrated_OnlyOnWindows()
    {
        var profile = new ConnectionProfile { Server = "db1", Authentication = AuthenticationKind.WindowsIntegrated };
        if (OperatingSystem.IsWindows())
            Assert.True(new SqlConnectionStringBuilder(ConnectionStringFactory.Build(profile, null)).IntegratedSecurity);
        else
            Assert.Throws<PlatformNotSupportedException>(() => ConnectionStringFactory.Build(profile, null));
        Assert.Equal(OperatingSystem.IsWindows(), AuthenticationKindExtensions.Available().Contains(AuthenticationKind.WindowsIntegrated));
    }

    [Fact]
    public void ProfileStore_RoundTripsProfilesAndSecrets()
    {
        string path = Path.Combine(Path.GetTempPath(), $"quarry-test-{Guid.NewGuid():N}.json");
        try
        {
            var creds = new InMemoryCredentialStore();
            var store = new ProfileStore(creds, path);
            var profile = new ConnectionProfile { Server = "db1", UserName = "sa" };
            store.Save(profile, "pw");

            var loaded = Assert.Single(store.Load());
            Assert.Equal(profile, loaded);
            Assert.Equal("pw", store.GetSecret(loaded));
            Assert.DoesNotContain("pw", File.ReadAllText(path));

            store.Save(loaded with { SaveSecret = false }, "pw");
            Assert.Null(creds.Get(profile.CredentialKey));

            store.Delete(loaded);
            Assert.Empty(store.Load());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    [Trait("Category", "Platform")]
    public void PlatformCredentialStore_RoundTrips()
    {
        var store = CredentialStore.CreateDefault();
        if (!store.IsAvailable)
            return; // No keychain on this machine (e.g. headless Linux).

        string key = $"Quarry/test-{Guid.NewGuid():N}";
        try
        {
            Assert.Null(store.Get(key));
            store.Set(key, "p@ss wörd");
            Assert.Equal("p@ss wörd", store.Get(key));
            store.Set(key, "changed");
            Assert.Equal("changed", store.Get(key));
        }
        finally
        {
            store.Delete(key);
        }
        Assert.Null(store.Get(key));
        store.Delete(key); // deleting a missing key is not an error
    }
}
