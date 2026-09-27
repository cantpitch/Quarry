namespace Quarry.Core;

/// <summary>Per-user storage locations.</summary>
public static class AppPaths
{
    public static string DataDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Quarry");

    public static string ConnectionsFile => Path.Combine(DataDirectory, "connections.json");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string HistoryFile => Path.Combine(DataDirectory, "history.jsonl");
}
