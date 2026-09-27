namespace Quarry.Core;

/// <summary>Per-user storage locations.</summary>
public static class AppPaths
{
    public static string DataDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Quarry");

    public static string ConnectionsFile => Path.Combine(DataDirectory, "connections.json");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string HistoryFile => Path.Combine(DataDirectory, "history.jsonl");

    /// <summary>The open tabs, restored on the next start.</summary>
    public static string SessionFile => Path.Combine(DataDirectory, "session.json");

    /// <summary>Unsaved tab text, one file per tab.</summary>
    public static string SessionBackupDirectory => Path.Combine(DataDirectory, "session");
}
