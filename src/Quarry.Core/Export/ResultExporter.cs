using System.Text;
using Quarry.Core.Results;

namespace Quarry.Core.Export;

public enum ExportFormat
{
    Csv,
    Tsv,
    Text,
    Json,
    Xlsx,
}

public sealed record ExportOptions
{
    public bool IncludeHeaders { get; init; } = true;

    /// <summary>Write a UTF-8 byte order mark to text files, which Excel needs to detect UTF-8 in CSV.</summary>
    public bool Utf8Bom { get; init; } = true;

    internal Encoding Encoding => new UTF8Encoding(Utf8Bom);
}

/// <summary>
/// Writes result sets to files as they arrive. CSV, TSV and JSON write one file per result set
/// (name.csv, name_2.csv, …); TXT writes all result sets to one file; XLSX writes one worksheet each.
/// </summary>
public interface IResultExporter : IDisposable
{
    void BeginResultSet(IReadOnlyList<ColumnInfo> columns);

    void WriteRows(IReadOnlyList<object?[]> rows);

    void EndResultSet(long rowCount, bool truncated);

    /// <summary>Flushes and closes all files. Called once after the last result set.</summary>
    void Complete();

    /// <summary>Files written so far.</summary>
    IReadOnlyList<string> Files { get; }
}

public static class ResultExporter
{
    public static ExportFormat? FormatFromPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".csv" => ExportFormat.Csv,
        ".tsv" or ".tab" => ExportFormat.Tsv,
        ".txt" or ".rpt" => ExportFormat.Text,
        ".json" => ExportFormat.Json,
        ".xlsx" => ExportFormat.Xlsx,
        _ => null,
    };

    public static string Extension(ExportFormat format) => format switch
    {
        ExportFormat.Csv => ".csv",
        ExportFormat.Tsv => ".tsv",
        ExportFormat.Text => ".txt",
        ExportFormat.Json => ".json",
        _ => ".xlsx",
    };

    /// <summary>Creates a streaming exporter. Nothing is written until the first result set begins.</summary>
    public static IResultExporter Create(ExportFormat format, string path, ExportOptions? options = null)
    {
        options ??= new ExportOptions();
        return format switch
        {
            ExportFormat.Csv => new DelimitedFileExporter(path, ',', options),
            ExportFormat.Tsv => new DelimitedFileExporter(path, '\t', options),
            ExportFormat.Text => new TextFileExporter(path, options),
            ExportFormat.Json => new JsonFileExporter(path),
            _ => new XlsxExporter(path, options),
        };
    }

    /// <summary>Exports result sets that are already in memory.</summary>
    public static IReadOnlyList<string> Export(IReadOnlyList<ResultSet> resultSets, ExportFormat format, string path,
        ExportOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ExportOptions();
        if (format == ExportFormat.Text)
        {
            // In-memory results can size text columns to the data rather than the declared types.
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var writer = new StreamWriter(path, false, options.Encoding);
            ResultFormatting.Write(writer, resultSets, OutputMode.Text);
            return [path];
        }

        using var exporter = Create(format, path, options);
        foreach (var rs in resultSets)
        {
            exporter.BeginResultSet(rs.Columns);
            const int chunk = 5000;
            for (int i = 0; i < rs.Rows.Count; i += chunk)
            {
                cancellationToken.ThrowIfCancellationRequested();
                exporter.WriteRows(rs.Rows.GetRange(i, Math.Min(chunk, rs.Rows.Count - i)));
            }
            exporter.EndResultSet(rs.Rows.Count, rs.IsTruncated);
        }
        exporter.Complete();
        return exporter.Files;
    }

    /// <summary>The file for the n-th (zero-based) result set: the path itself, then name_2.ext, name_3.ext, …</summary>
    public static string PathForResultSet(string path, int index)
    {
        if (index == 0)
            return path;
        string directory = Path.GetDirectoryName(path) ?? "";
        return Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(path)}_{index + 1}{Path.GetExtension(path)}");
    }
}

/// <summary>Base for formats that write one file per result set.</summary>
internal abstract class PerResultSetFileExporter(string path, ExportOptions options) : IResultExporter
{
    private readonly List<string> _files = [];
    private StreamWriter? _writer;

    protected ExportOptions Options { get; } = options;

    protected IReadOnlyList<ColumnInfo> Columns { get; private set; } = [];

    public IReadOnlyList<string> Files => _files;

    public void BeginResultSet(IReadOnlyList<ColumnInfo> columns)
    {
        CloseCurrent();
        Columns = columns;
        string file = ResultExporter.PathForResultSet(path, _files.Count);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        _writer = new StreamWriter(file, false, Options.Encoding, bufferSize: 1 << 16);
        _files.Add(file);
        OnBegin(_writer);
    }

    public void WriteRows(IReadOnlyList<object?[]> rows)
    {
        if (_writer is null)
            throw new InvalidOperationException("BeginResultSet was not called.");
        foreach (var row in rows)
            OnRow(_writer, row);
    }

    public void EndResultSet(long rowCount, bool truncated) => CloseCurrent();

    public void Complete() => CloseCurrent();

    private void CloseCurrent()
    {
        if (_writer is null)
            return;
        OnEnd(_writer);
        _writer.Dispose();
        _writer = null;
    }

    protected abstract void OnBegin(TextWriter writer);

    protected abstract void OnRow(TextWriter writer, object?[] row);

    protected virtual void OnEnd(TextWriter writer) { }

    public void Dispose() => CloseCurrent();
}

internal sealed class DelimitedFileExporter(string path, char delimiter, ExportOptions options) : PerResultSetFileExporter(path, options)
{
    protected override void OnBegin(TextWriter writer)
    {
        if (!Options.IncludeHeaders)
            return;
        for (int c = 0; c < Columns.Count; c++)
        {
            if (c > 0)
                writer.Write(delimiter);
            writer.Write(DelimitedFormatter.Quote(Columns[c].Name, delimiter));
        }
        writer.Write("\r\n");
    }

    protected override void OnRow(TextWriter writer, object?[] row)
    {
        DelimitedFormatter.WriteRow(writer, row, Columns, delimiter, FormatOptions.Export);
        writer.Write("\r\n");
    }
}
