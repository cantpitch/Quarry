using Quarry.Core.Results;

namespace Quarry.Core.Settings;

public sealed record AppSettings
{
    public string EditorFontFamily { get; init; } = "Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace";

    public double EditorFontSize { get; init; } = 13;

    /// <summary>0 keeps every row.</summary>
    public int MaxRowsPerResultSet { get; init; }

    /// <summary>0 waits indefinitely.</summary>
    public int CommandTimeoutSeconds { get; init; }

    public bool StopOnError { get; init; }

    public OutputMode DefaultOutputMode { get; init; } = OutputMode.Grid;

    public string BatchSeparator { get; init; } = "GO";

    /// <summary>Record executed queries in the History panel.</summary>
    public bool SaveQueryHistory { get; init; } = true;

    public bool ExportIncludeHeaders { get; init; } = true;

    /// <summary>Write a UTF-8 BOM to CSV/TSV/TXT exports so Excel reads them as UTF-8.</summary>
    public bool ExportUtf8Bom { get; init; } = true;

    [System.Text.Json.Serialization.JsonIgnore]
    public Export.ExportOptions ExportOptions => new() { IncludeHeaders = ExportIncludeHeaders, Utf8Bom = ExportUtf8Bom };

    public static AppSettings Load(string? path = null)
    {
        try
        {
            return JsonFile.Read<AppSettings>(path ?? AppPaths.SettingsFile) ?? new AppSettings();
        }
        catch (Exception)
        {
            return new AppSettings();
        }
    }

    public void Save(string? path = null) => JsonFile.Write(path ?? AppPaths.SettingsFile, this);
}
