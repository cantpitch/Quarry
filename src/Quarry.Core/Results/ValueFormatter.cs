using System.Globalization;
using System.Text;

namespace Quarry.Core.Results;

public sealed record FormatOptions
{
    public static FormatOptions Display { get; } = new();

    /// <summary>For files and clipboard: full values, NULL as empty.</summary>
    public static FormatOptions Export { get; } = new() { NullText = "", MaxLength = null, MaxBinaryBytes = null };

    public string NullText { get; init; } = "NULL";

    /// <summary>Maximum characters of a value; longer values end with "…". Null means no limit.</summary>
    public int? MaxLength { get; init; } = 1024;

    /// <summary>Maximum bytes of binary data rendered as hex. Null means no limit.</summary>
    public int? MaxBinaryBytes { get; init; } = 512;
}

/// <summary>Converts result values to text the same way everywhere (grid, text, CSV, TSV, clipboard).</summary>
public static class ValueFormatter
{
    public static string Format(object? value, ColumnInfo column, FormatOptions? options = null)
    {
        options ??= FormatOptions.Display;
        string text = value switch
        {
            null or DBNull => options.NullText,
            string s => s,
            bool b => b ? "1" : "0",
            byte[] bytes => FormatBinary(bytes, options.MaxBinaryBytes),
            DateTime dt => FormatDateTime(dt, column),
            DateTimeOffset dto => FormatDateTimeOffset(dto, column),
            TimeSpan ts => FormatTime(ts, column),
            Guid g => g.ToString("D").ToUpperInvariant(),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            decimal m => FormatDecimal(m, column),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };

        if (options.MaxLength is { } max && text.Length > max)
            text = string.Concat(text.AsSpan(0, max), "…");
        return text;
    }

    private static string FormatBinary(byte[] bytes, int? maxBytes)
    {
        int count = maxBytes is { } max ? Math.Min(bytes.Length, max) : bytes.Length;
        var sb = new StringBuilder(2 + count * 2 + 1);
        sb.Append("0x");
        sb.Append(Convert.ToHexString(bytes, 0, count));
        if (count < bytes.Length)
            sb.Append('…');
        return sb.ToString();
    }

    private static string FormatDateTime(DateTime value, ColumnInfo column)
    {
        return column.SqlTypeName.ToLowerInvariant() switch
        {
            "date" => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "smalldatetime" => value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            "datetime" => value.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            _ => value.ToString("yyyy-MM-dd HH:mm:ss" + Fraction(column.Scale ?? 7), CultureInfo.InvariantCulture),
        };
    }

    private static string FormatDateTimeOffset(DateTimeOffset value, ColumnInfo column)
        => value.ToString("yyyy-MM-dd HH:mm:ss" + Fraction(column.Scale ?? 7) + " zzz", CultureInfo.InvariantCulture);

    private static string FormatTime(TimeSpan value, ColumnInfo column)
    {
        int scale = Math.Clamp(column.Scale ?? 7, 0, 7);
        string text = value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        if (value.Days > 0)
            text = $"{value.Days}.{text}";
        if (scale > 0)
            text += "." + (value.Ticks % TimeSpan.TicksPerSecond).ToString("0000000", CultureInfo.InvariantCulture)[..scale];
        return text;
    }

    private static string FormatDecimal(decimal value, ColumnInfo column)
    {
        // money/decimal keep their declared scale, e.g. 1.5 in decimal(10,2) shows as 1.50.
        if (column.Scale is { } scale and >= 0 and <= 28 && column.SqlTypeName is not ("float" or "real"))
            return value.ToString("F" + scale, CultureInfo.InvariantCulture);
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string Fraction(int scale)
    {
        scale = Math.Clamp(scale, 0, 7);
        return scale == 0 ? "" : "." + new string('f', scale);
    }
}
