using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Quarry.Core.Results;

namespace Quarry.Core.Export;

/// <summary>
/// One JSON file per result set (UTF-8, no BOM): an array of objects keyed by column name. Numbers and booleans keep
/// their JSON types; dates are ISO 8601 strings; binary is a 0x… hex string.
/// </summary>
internal sealed class JsonFileExporter(string path) : IResultExporter
{
    private readonly List<string> _files = [];
    private FileStream? _stream;
    private Utf8JsonWriter? _json;
    private IReadOnlyList<ColumnInfo> _columns = [];
    private JsonEncodedText[] _names = [];

    public IReadOnlyList<string> Files => _files;

    public void BeginResultSet(IReadOnlyList<ColumnInfo> columns)
    {
        CloseCurrent();
        _columns = columns;
        _names = UniqueNames(columns).Select(n => JsonEncodedText.Encode(n)).ToArray();
        string file = ResultExporter.PathForResultSet(path, _files.Count);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        _stream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        _json = new Utf8JsonWriter(_stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        _files.Add(file);
        _json.WriteStartArray();
    }

    public void WriteRows(IReadOnlyList<object?[]> rows)
    {
        var json = _json ?? throw new InvalidOperationException("BeginResultSet was not called.");
        foreach (var row in rows)
        {
            json.WriteStartObject();
            for (int c = 0; c < _columns.Count; c++)
            {
                json.WritePropertyName(_names[c]);
                WriteValue(json, row[c], _columns[c]);
            }
            json.WriteEndObject();
            if (json.BytesPending > 1 << 16)
                json.Flush();
        }
    }

    public void EndResultSet(long rowCount, bool truncated) => CloseCurrent();

    public void Complete() => CloseCurrent();

    public void Dispose() => CloseCurrent();

    private void CloseCurrent()
    {
        if (_json is null)
            return;
        _json.WriteEndArray();
        _json.Flush();
        _json.Dispose();
        _stream!.Dispose();
        _json = null;
        _stream = null;
    }

    internal static void WriteValue(Utf8JsonWriter json, object? value, ColumnInfo column)
    {
        switch (value)
        {
            case null:
                json.WriteNullValue();
                break;
            case bool b:
                json.WriteBooleanValue(b);
                break;
            case byte or short or int or long:
                json.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case decimal m:
                json.WriteNumberValue(m);
                break;
            case double d when double.IsFinite(d):
                json.WriteNumberValue(d);
                break;
            case float f when float.IsFinite(f):
                json.WriteNumberValue(f);
                break;
            case DateTime dt:
                json.WriteStringValue(column.SqlTypeName.Equals("date", StringComparison.OrdinalIgnoreCase)
                    ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : dt.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture));
                break;
            case DateTimeOffset dto:
                json.WriteStringValue(dto.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture));
                break;
            default:
                json.WriteStringValue(ValueFormatter.Format(value, column, FormatOptions.Export));
                break;
        }
    }

    /// <summary>JSON keys must be unique: unnamed columns become Column1…, duplicates get a _2, _3 … suffix.</summary>
    internal static List<string> UniqueNames(IReadOnlyList<ColumnInfo> columns)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>(columns.Count);
        for (int i = 0; i < columns.Count; i++)
        {
            string name = string.IsNullOrEmpty(columns[i].Name) ? $"Column{i + 1}" : columns[i].Name;
            string candidate = name;
            for (int n = 2; !used.Add(candidate); n++)
                candidate = $"{name}_{n}";
            names.Add(candidate);
        }
        return names;
    }
}
