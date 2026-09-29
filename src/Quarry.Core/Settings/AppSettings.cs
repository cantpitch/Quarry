using Quarry.Core.Results;

namespace Quarry.Core.Settings;

public sealed record AppSettings
{
    /// <summary>A single font family name; empty uses the built-in monospace stack. Also used for text/CSV/TSV output.</summary>
    public string EditorFontFamily { get; init; } = "";

    public double EditorFontSize { get; init; } = 13;

    /// <summary>A single font family name (monospace or proportional); empty uses the application font.</summary>
    public string GridFontFamily { get; init; } = "";

    public double GridFontSize { get; init; } = 12;

    /// <summary>Query editor background as #RRGGBB, per theme.</summary>
    public string EditorBackgroundLight { get; init; } = "#FFFFFF";

    public string EditorBackgroundDark { get; init; } = "#1E1E1E";

    /// <summary>Background of the explorer, history and results panes as #RRGGBB, per theme.</summary>
    public string PaneBackgroundLight { get; init; } = "#F5F5F7";

    public string PaneBackgroundDark { get; init; } = "#202024";

    /// <summary>0 keeps every row.</summary>
    public int MaxRowsPerResultSet { get; init; }

    /// <summary>0 waits indefinitely.</summary>
    public int CommandTimeoutSeconds { get; init; }

    public bool StopOnError { get; init; }

    public OutputMode DefaultOutputMode { get; init; } = OutputMode.Grid;

    public string BatchSeparator { get; init; } = "GO";

    /// <summary>SQL formatter style. Its batch separator is ignored in favour of <see cref="BatchSeparator"/>.</summary>
    public Parsing.Formatting.SqlFormatOptions Formatting { get; init; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public Parsing.Formatting.SqlFormatOptions EffectiveFormatting => Formatting with { BatchSeparator = BatchSeparator };

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
