using System.Globalization;

namespace Quarry.Core.Results;

public enum OutputMode
{
    Grid,
    Text,
    Csv,
    Tsv,
}

/// <summary>Writes result sets as delimited text (CSV or TSV) with RFC 4180 quoting.</summary>
public static class DelimitedFormatter
{
    public static void Write(TextWriter writer, ResultSet resultSet, char delimiter, bool includeHeaders = true, FormatOptions? options = null)
    {
        options ??= FormatOptions.Export;
        var columns = resultSet.Columns;

        if (includeHeaders)
        {
            for (int c = 0; c < columns.Count; c++)
            {
                if (c > 0)
                    writer.Write(delimiter);
                writer.Write(Quote(columns[c].Name, delimiter));
            }
            writer.Write("\r\n");
        }

        foreach (var row in resultSet.Rows)
        {
            WriteRow(writer, row, columns, delimiter, options);
            writer.Write("\r\n");
        }
    }

    public static void WriteRow(TextWriter writer, object?[] row, IReadOnlyList<ColumnInfo> columns, char delimiter, FormatOptions options)
    {
        for (int c = 0; c < columns.Count; c++)
        {
            if (c > 0)
                writer.Write(delimiter);
            writer.Write(Quote(ValueFormatter.Format(row[c], columns[c], options), delimiter));
        }
    }

    /// <summary>Quotes a field when it contains the delimiter, a quote, a line break, or leading/trailing spaces.</summary>
    public static string Quote(string field, char delimiter)
    {
        bool needsQuotes = field.Length > 0
            && (field.Contains(delimiter) || field.Contains('"') || field.Contains('\n') || field.Contains('\r')
                || field[0] == ' ' || field[^1] == ' ');
        return needsQuotes ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
    }
}

/// <summary>SSMS-style "results to text": fixed-width columns padded with spaces, a header and a dash rule.</summary>
public static class TextTableFormatter
{
    public const int MaxColumnWidth = 256;

    public static void Write(TextWriter writer, ResultSet resultSet, FormatOptions? options = null)
    {
        options ??= FormatOptions.Display with { MaxLength = MaxColumnWidth };
        var columns = resultSet.Columns;
        var cells = new List<string[]>(resultSet.Rows.Count);
        var widths = columns.Select(c => Math.Min(c.DisplayName.Length, MaxColumnWidth)).ToArray();
        var rightAlign = columns.Select(c => IsNumeric(c.ClrType)).ToArray();

        foreach (var row in resultSet.Rows)
        {
            var line = new string[columns.Count];
            for (int c = 0; c < columns.Count; c++)
            {
                string text = Flatten(ValueFormatter.Format(row[c], columns[c], options));
                if (text.Length > MaxColumnWidth)
                    text = text[..MaxColumnWidth];
                line[c] = text;
                widths[c] = Math.Max(widths[c], text.Length);
            }
            cells.Add(line);
        }

        for (int c = 0; c < columns.Count; c++)
        {
            if (c > 0)
                writer.Write(' ');
            writer.Write(Pad(columns[c].DisplayName, widths[c], rightAlign: false, last: c == columns.Count - 1));
        }
        writer.WriteLine();
        for (int c = 0; c < columns.Count; c++)
        {
            if (c > 0)
                writer.Write(' ');
            writer.Write(new string('-', widths[c]));
        }
        writer.WriteLine();

        foreach (var line in cells)
        {
            for (int c = 0; c < columns.Count; c++)
            {
                if (c > 0)
                    writer.Write(' ');
                writer.Write(Pad(line[c], widths[c], rightAlign[c], last: c == columns.Count - 1));
            }
            writer.WriteLine();
        }

        writer.WriteLine();
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"({resultSet.Rows.Count} row{(resultSet.Rows.Count == 1 ? "" : "s")} affected{(resultSet.IsTruncated ? ", truncated by row limit" : "")})"));
    }

    private static string Pad(string text, int width, bool rightAlign, bool last)
    {
        if (rightAlign)
            return text.PadLeft(width);
        return last ? text : text.PadRight(width);
    }

    private static string Flatten(string text)
        => text.Contains('\n') || text.Contains('\r') || text.Contains('\t')
            ? text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ')
            : text;

    private static bool IsNumeric(Type type)
        => type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)
           || type == typeof(decimal) || type == typeof(double) || type == typeof(float);
}

public static class ResultFormatting
{
    /// <summary>Renders result sets in a text output mode, separated by blank lines.</summary>
    public static void Write(TextWriter writer, IEnumerable<ResultSet> resultSets, OutputMode mode)
    {
        bool first = true;
        foreach (var rs in resultSets)
        {
            if (!first)
                writer.WriteLine();
            first = false;
            switch (mode)
            {
                case OutputMode.Csv:
                    DelimitedFormatter.Write(writer, rs, ',');
                    break;
                case OutputMode.Tsv:
                    DelimitedFormatter.Write(writer, rs, '\t');
                    break;
                default:
                    TextTableFormatter.Write(writer, rs);
                    break;
            }
        }
    }

    public static string ToString(IEnumerable<ResultSet> resultSets, OutputMode mode)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        Write(writer, resultSets, mode);
        return writer.ToString();
    }
}
