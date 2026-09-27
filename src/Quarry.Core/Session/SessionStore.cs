using System.Text;
using Quarry.Core.Results;

namespace Quarry.Core.Session;

/// <summary>One open tab, as remembered between runs.</summary>
public sealed record SessionDocument
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Where the tab was saved; null for an untitled tab.</summary>
    public string? FilePath { get; init; }

    /// <summary>The "SQLQueryN.sql" name of an untitled tab.</summary>
    public string? UntitledName { get; init; }

    /// <summary>
    /// True when the tab had text that is not in <see cref="FilePath"/> (an untitled tab, or unsaved
    /// changes); that text is kept in a backup file. Otherwise the tab is reloaded from its file.
    /// </summary>
    public bool HasBackup { get; init; }

    /// <summary>The saved connection profile the tab was connected to.</summary>
    public Guid? ProfileId { get; init; }

    /// <summary>The profile's display name, for messages when it cannot be found.</summary>
    public string? ServerDisplayName { get; init; }

    public string? Database { get; init; }

    public int CaretOffset { get; init; }

    public OutputMode OutputMode { get; init; } = OutputMode.Grid;
}

/// <summary>The open tabs and connected servers.</summary>
public sealed record SessionState
{
    public List<SessionDocument> Documents { get; init; } = [];

    public int SelectedIndex { get; init; }

    /// <summary>Profiles connected in the object explorer.</summary>
    public List<Guid> Servers { get; init; } = [];
}

/// <summary>
/// Remembers the open tabs between runs: <c>session.json</c> lists them, and the text of untitled or
/// modified tabs goes in one backup file per tab, so a change to one tab rewrites only that tab.
/// </summary>
public sealed class SessionStore(string? path = null, string? backupDirectory = null)
{
    private readonly string _path = path ?? AppPaths.SessionFile;
    private readonly string _backupDirectory = backupDirectory ?? AppPaths.SessionBackupDirectory;

    /// <summary>The last saved session, or null when there is none or it cannot be read.</summary>
    public SessionState? Load()
    {
        try
        {
            return JsonFile.Read<SessionState>(_path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The backup text of a tab, or null when it is missing or unreadable.</summary>
    public string? ReadBackup(Guid id)
    {
        try
        {
            string file = BackupPath(id);
            return File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Saves the session. <paramref name="changedBackups"/> holds the text of tabs whose backup is new or
    /// out of date; backups of tabs that no longer need one are deleted.
    /// </summary>
    public void Save(SessionState state, IReadOnlyDictionary<Guid, string> changedBackups)
    {
        Directory.CreateDirectory(_backupDirectory);
        foreach (var (id, text) in changedBackups)
        {
            string file = BackupPath(id);
            string temp = file + ".tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, file, overwrite: true);
        }

        JsonFile.Write(_path, state);

        var keep = state.Documents.Where(d => d.HasBackup).Select(d => BackupPath(d.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(_backupDirectory, "*.sql"))
        {
            if (!keep.Contains(file))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Removed on a later save.
                }
            }
        }
    }

    private string BackupPath(Guid id) => Path.Combine(_backupDirectory, $"{id:N}.sql");
}
