using Quarry.Core.Results;
using Quarry.Core.Session;

namespace Quarry.Core.Tests;

public sealed class SessionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"quarry-session-{Guid.NewGuid():N}");

    private string SessionFile => Path.Combine(_dir, "session.json");

    private string BackupDir => Path.Combine(_dir, "session");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Load_WithoutASession_ReturnsNull()
    {
        Assert.Null(new SessionStore(SessionFile, BackupDir).Load());
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SessionFile, "{ not json");
        Assert.Null(new SessionStore(SessionFile, BackupDir).Load());
    }

    [Fact]
    public void SaveAndLoad_RoundTripsTabsAndBackups()
    {
        var store = new SessionStore(SessionFile, BackupDir);
        var untitled = new SessionDocument { UntitledName = "SQLQuery3.sql", HasBackup = true, CaretOffset = 4, OutputMode = OutputMode.Csv };
        var file = new SessionDocument { FilePath = "/work/report.sql", ProfileId = Guid.NewGuid(), ServerDisplayName = "Prod", Database = "Sales" };
        var state = new SessionState { Documents = [untitled, file], SelectedIndex = 1, Servers = [file.ProfileId!.Value] };
        store.Save(state, new Dictionary<Guid, string> { [untitled.Id] = "select 'ü'" });

        var loaded = new SessionStore(SessionFile, BackupDir).Load()!;
        Assert.Equal(state.Documents, loaded.Documents);
        Assert.Equal(1, loaded.SelectedIndex);
        Assert.Equal(state.Servers, loaded.Servers);
        Assert.Equal("select 'ü'", store.ReadBackup(untitled.Id));
        Assert.Null(store.ReadBackup(file.Id));
    }

    [Fact]
    public void Save_KeepsUnchangedBackupsAndDeletesUnneededOnes()
    {
        var store = new SessionStore(SessionFile, BackupDir);
        var a = new SessionDocument { HasBackup = true };
        var b = new SessionDocument { HasBackup = true };
        store.Save(new SessionState { Documents = [a, b] }, new Dictionary<Guid, string> { [a.Id] = "a", [b.Id] = "b" });

        // Only a changed; b was saved to its file, so its backup goes.
        store.Save(new SessionState { Documents = [a, b with { HasBackup = false, FilePath = "/b.sql" }] }, new Dictionary<Guid, string> { [a.Id] = "a2" });
        Assert.Equal("a2", store.ReadBackup(a.Id));
        Assert.Null(store.ReadBackup(b.Id));

        // A closed tab's backup goes too.
        store.Save(new SessionState(), new Dictionary<Guid, string>());
        Assert.Null(store.ReadBackup(a.Id));
        Assert.Empty(Directory.GetFiles(BackupDir));
    }
}
