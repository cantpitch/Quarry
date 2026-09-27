using System.Text;
using System.Text.Json;
using Quarry.Parsing;

namespace Quarry.Core.History;

public enum HistoryRunKind
{
    Script,
    Query,
    Selection,
}

public enum HistoryOutcome
{
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>One execution: what ran, where, and how it went.</summary>
public sealed record QueryHistoryEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    /// <summary>Server name as typed in the connection (used to reconnect).</summary>
    public string Server { get; init; } = "";

    /// <summary>Friendly connection name shown in the list.</summary>
    public string ServerDisplayName { get; init; } = "";

    public string? Database { get; init; }

    /// <summary>The SQL that was sent, batches joined with GO lines.</summary>
    public string Text { get; init; } = "";

    /// <summary>True when <see cref="Text"/> was cut to <see cref="QueryHistoryStore.MaxTextLength"/>.</summary>
    public bool TextTruncated { get; init; }

    public HistoryRunKind Kind { get; init; }

    public HistoryOutcome Outcome { get; init; }

    public long DurationMs { get; init; }

    public long RowCount { get; init; }

    public int ResultSetCount { get; init; }

    /// <summary>Set when results were streamed to a file instead of the grid.</summary>
    public string? ExportPath { get; init; }

    /// <summary>
    /// Rebuilds a runnable script from what was executed: batches separated by the batch
    /// separator, with "GO n" kept for repeated batches.
    /// </summary>
    public static string ScriptFromUnits(IReadOnlyList<ExecutionUnit> units, string separator = BatchSplitter.DefaultSeparator)
    {
        if (units.Count == 1 && units[0].RepeatCount == 1)
            return units[0].Text.Trim();

        var sb = new StringBuilder();
        for (int i = 0; i < units.Count; i++)
        {
            var unit = units[i];
            sb.Append(unit.Text.Trim());
            // A separator after the last batch is only needed to carry a repeat count.
            if (i < units.Count - 1 || unit.RepeatCount > 1)
            {
                sb.Append('\n').Append(separator);
                if (unit.RepeatCount > 1)
                    sb.Append(' ').Append(unit.RepeatCount);
            }
            if (i < units.Count - 1)
                sb.Append('\n');
        }
        return sb.ToString();
    }
}

/// <summary>
/// Query history persisted as JSON Lines (one entry per line, oldest first), so recording an
/// execution is a single append. The file is compacted to the newest <see cref="MaxEntries"/>
/// once it grows well past that.
/// </summary>
public sealed class QueryHistoryStore(string? path = null)
{
    public const int MaxEntries = 5000;
    public const int MaxTextLength = 1_000_000;

    private readonly string _path = path ?? AppPaths.HistoryFile;
    private readonly object _gate = new();
    private int _linesInFile = -1;

    /// <summary>All entries, newest first. Unreadable lines are skipped.</summary>
    public List<QueryHistoryEntry> Load()
    {
        lock (_gate)
        {
            var entries = ReadAll();
            _linesInFile = entries.Count;
            entries.Reverse();
            return entries;
        }
    }

    /// <summary>Appends an entry (truncating very long text) and returns what was stored.</summary>
    public QueryHistoryEntry Append(QueryHistoryEntry entry)
    {
        if (entry.Text.Length > MaxTextLength)
            entry = entry with { Text = entry.Text[..MaxTextLength], TextTruncated = true };

        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.AppendAllText(_path, JsonSerializer.Serialize(entry, JsonFile.CompactOptions) + "\n", Utf8NoBom);
            if (_linesInFile < 0)
                _linesInFile = ReadAll().Count;
            else
                _linesInFile++;

            if (_linesInFile > MaxEntries + MaxEntries / 5)
                Rewrite(ReadAll().TakeLast(MaxEntries).ToList());
        }
        return entry;
    }

    public void Delete(Guid id)
    {
        lock (_gate)
            Rewrite(ReadAll().Where(e => e.Id != id).ToList());
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (File.Exists(_path))
                File.Delete(_path);
            _linesInFile = 0;
        }
    }

    private List<QueryHistoryEntry> ReadAll()
    {
        var entries = new List<QueryHistoryEntry>();
        if (!File.Exists(_path))
            return entries;
        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                if (JsonSerializer.Deserialize<QueryHistoryEntry>(line, JsonFile.CompactOptions) is { } entry)
                    entries.Add(entry);
            }
            catch (JsonException)
            {
                // A partially written line (e.g. after a crash) must not lose the rest.
            }
        }
        return entries;
    }

    private void Rewrite(List<QueryHistoryEntry> oldestFirst)
    {
        string temp = _path + ".tmp";
        using (var writer = new StreamWriter(temp, false, Utf8NoBom))
        {
            foreach (var entry in oldestFirst)
                writer.Write(JsonSerializer.Serialize(entry, JsonFile.CompactOptions) + "\n");
        }
        File.Move(temp, _path, overwrite: true);
        _linesInFile = oldestFirst.Count;
    }

    private static readonly UTF8Encoding Utf8NoBom = new(false);
}
