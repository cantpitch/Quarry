using System.Globalization;
using Quarry.Core.Results;

namespace Quarry.Core.Export;

/// <summary>
/// Streaming "results to text" file. Rows are not known in advance, so column widths come from
/// the declared column types (like SSMS results-to-file), capped at <see cref="TextTableFormatter.MaxColumnWidth"/>.
/// </summary>
internal sealed class TextFileExporter(string path, ExportOptions options) : IResultExporter
{
    private StreamWriter? _writer;
    private IReadOnlyList<ColumnInfo> _columns = [];
    private int[] _widths = [];
    private bool[] _rightAlign = [];
    private bool _any;

    public IReadOnlyList<string> Files => _writer is null && !_any ? [] : [path];

    public void BeginResultSet(IReadOnlyList<ColumnInfo> columns)
    {
        if (_writer is null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            _writer = new StreamWriter(path, false, options.Encoding, bufferSize: 1 << 16);
        }
        if (_any)
            _writer.WriteLine();
        _any = true;
        _columns = columns;
        _widths = columns.Select(c => Math.Max(c.DisplayName.Length, DeclaredWidth(c))).ToArray();
        _rightAlign = columns.Select(c => IsNumeric(c.ClrType)).ToArray();

        if (options.IncludeHeaders)
        {
            WriteLine(columns.Select(c => c.DisplayName).ToArray(), rightAlign: false);
            _writer.WriteLine(string.Join(' ', _widths.Select(w => new string('-', w))));
        }
    }

    public void WriteRows(IReadOnlyList<object?[]> rows)
    {
        var format = FormatOptions.Display with { MaxLength = TextTableFormatter.MaxColumnWidth };
        var cells = new string[_columns.Count];
        foreach (var row in rows)
        {
            for (int c = 0; c < cells.Length; c++)
            {
                string text = ValueFormatter.Format(row[c], _columns[c], format)
                    .Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
                cells[c] = text.Length > _widths[c] ? text[.._widths[c]] : text;
            }
            WriteLine(cells, rightAlign: true);
        }
    }

    public void EndResultSet(long rowCount, bool truncated)
    {
        _writer!.WriteLine();
        _writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"({rowCount} row{(rowCount == 1 ? "" : "s")} affected{(truncated ? ", truncated by row limit" : "")})"));
    }

    public void Complete() => Dispose();

    public void Dispose()
    {
        _writer?.Dispose();
        _writer = null;
    }

    private void WriteLine(string[] cells, bool rightAlign)
    {
        for (int c = 0; c < cells.Length; c++)
        {
            if (c > 0)
                _writer!.Write(' ');
            bool last = c == cells.Length - 1;
            if (rightAlign && _rightAlign[c])
                _writer!.Write(cells[c].PadLeft(_widths[c]));
            else
                _writer!.Write(last ? cells[c] : cells[c].PadRight(_widths[c]));
        }
        _writer!.WriteLine();
    }

    /// <summary>Display width implied by the column's type, as SSMS uses for results to file.</summary>
    internal static int DeclaredWidth(ColumnInfo c)
    {
        int width = c.SqlTypeName.ToLowerInvariant() switch
        {
            "bit" => 1,
            "tinyint" => 3,
            "smallint" => 6,
            "int" => 11,
            "bigint" => 20,
            "decimal" or "numeric" => (c.Precision ?? 18) + 2,
            "money" => 21,
            "smallmoney" => 12,
            "float" => 24,
            "real" => 14,
            "date" => 10,
            "smalldatetime" => 19,
            "datetime" => 23,
            "datetime2" => 20 + (c.Scale ?? 7),
            "datetimeoffset" => 27 + (c.Scale ?? 7),
            "time" => 9 + (c.Scale ?? 7),
            "uniqueidentifier" => 36,
            "binary" or "varbinary" or "timestamp" or "rowversion" => c.Size is > 0 and < 10000 ? 2 + 2 * c.Size.Value : TextTableFormatter.MaxColumnWidth,
            _ => c.Size is > 0 and < 10000 ? c.Size.Value : TextTableFormatter.MaxColumnWidth,
        };
        return Math.Clamp(width, 1, TextTableFormatter.MaxColumnWidth);
    }

    private static bool IsNumeric(Type type)
        => type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)
           || type == typeof(decimal) || type == typeof(double) || type == typeof(float);
}
