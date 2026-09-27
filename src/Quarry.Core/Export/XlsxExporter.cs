using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using Quarry.Core.Results;

namespace Quarry.Core.Export;

/// <summary>
/// Minimal streaming XLSX (SpreadsheetML) writer: one worksheet per result set, rows written as they
/// arrive, so memory use does not grow with the result size. Numbers, booleans and dates are native
/// cell types; everything else is an inline string. A result set larger than Excel's row limit
/// continues on another sheet.
/// </summary>
internal sealed class XlsxExporter : IResultExporter
{
    public const int MaxRowsPerSheet = 1_048_576;

    /// <summary>Rows per worksheet before continuing on a new one; lowered in tests.</summary>
    internal int RowsPerSheet { get; init; } = MaxRowsPerSheet;
    private const int MaxCellText = 32_767;
    private const int StyleHeader = 1;
    private const int StyleDateTime = 2;
    private const int StyleDate = 3;

    private readonly string _path;
    private readonly ExportOptions _options;
    private readonly List<string> _sheetNames = [];
    private FileStream? _file;
    private ZipArchive? _zip;
    private Stream? _sheetStream;
    private XmlWriter? _sheet;
    private IReadOnlyList<ColumnInfo> _columns = [];
    private int _resultSetNumber;
    private int _sheetPart;
    private int _rowsInSheet;
    private bool _completed;

    public XlsxExporter(string path, ExportOptions options)
    {
        _path = path;
        _options = options;
    }

    public IReadOnlyList<string> Files => _file is null && !_completed ? [] : [_path];

    public void BeginResultSet(IReadOnlyList<ColumnInfo> columns)
    {
        EnsureArchive();
        _columns = columns;
        _resultSetNumber++;
        _sheetPart = 0;
        StartSheet();
    }

    public void WriteRows(IReadOnlyList<object?[]> rows)
    {
        if (_sheet is null)
            throw new InvalidOperationException("BeginResultSet was not called.");
        foreach (var row in rows)
        {
            if (_rowsInSheet >= RowsPerSheet)
            {
                EndSheet();
                StartSheet();
            }
            _sheet.WriteStartElement("row");
            for (int c = 0; c < _columns.Count; c++)
                WriteCell(_sheet, row[c], _columns[c]);
            _sheet.WriteEndElement();
            _rowsInSheet++;
        }
    }

    public void EndResultSet(long rowCount, bool truncated) => EndSheet();

    public void Complete()
    {
        if (_completed)
            return;
        EndSheet();
        if (_zip is not null)
        {
            if (_sheetNames.Count == 0)
            {
                // An empty workbook is invalid; write one blank sheet.
                _columns = [];
                _resultSetNumber = 1;
                StartSheet();
                EndSheet();
            }
            WritePackageParts();
            _zip.Dispose();
            _file!.Dispose();
            _zip = null;
            _file = null;
        }
        _completed = true;
    }

    public void Dispose()
    {
        // Disposing without Complete (e.g. after an error) still leaves a readable file.
        if (!_completed && _zip is not null)
            Complete();
        _sheet?.Dispose();
        _sheetStream?.Dispose();
        _zip?.Dispose();
        _file?.Dispose();
    }

    private void EnsureArchive()
    {
        if (_zip is not null)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        _file = new FileStream(_path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        _zip = new ZipArchive(_file, ZipArchiveMode.Create, leaveOpen: true);
    }

    private void StartSheet()
    {
        _sheetPart++;
        string name = _sheetPart == 1 ? $"Result {_resultSetNumber}" : $"Result {_resultSetNumber} ({_sheetPart})";
        _sheetNames.Add(name);
        var entry = _zip!.CreateEntry($"xl/worksheets/sheet{_sheetNames.Count}.xml", CompressionLevel.Fastest);
        _sheetStream = entry.Open();
        _sheet = XmlWriter.Create(_sheetStream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
        _sheet.WriteStartDocument(true);
        _sheet.WriteStartElement("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");

        bool header = _options.IncludeHeaders && _columns.Count > 0;
        if (header)
        {
            // Freeze the header row.
            _sheet.WriteStartElement("sheetViews");
            _sheet.WriteStartElement("sheetView");
            _sheet.WriteAttributeString("workbookViewId", "0");
            _sheet.WriteStartElement("pane");
            _sheet.WriteAttributeString("ySplit", "1");
            _sheet.WriteAttributeString("topLeftCell", "A2");
            _sheet.WriteAttributeString("activePane", "bottomLeft");
            _sheet.WriteAttributeString("state", "frozen");
            _sheet.WriteEndElement();
            _sheet.WriteEndElement();
            _sheet.WriteEndElement();
        }

        _sheet.WriteStartElement("sheetData");
        _rowsInSheet = 0;
        if (header)
        {
            _sheet.WriteStartElement("row");
            foreach (var column in _columns)
                WriteInlineString(_sheet, column.DisplayName, StyleHeader);
            _sheet.WriteEndElement();
            _rowsInSheet++;
        }
    }

    private void EndSheet()
    {
        if (_sheet is null)
            return;
        _sheet.WriteEndElement(); // sheetData
        _sheet.WriteEndElement(); // worksheet
        _sheet.WriteEndDocument();
        _sheet.Dispose();
        _sheetStream!.Dispose();
        _sheet = null;
        _sheetStream = null;
    }

    private static void WriteCell(XmlWriter w, object? value, ColumnInfo column)
    {
        switch (value)
        {
            case null:
                w.WriteStartElement("c");
                w.WriteEndElement();
                return;
            case bool b:
                WriteRaw(w, "b", b ? "1" : "0", 0);
                return;
            case byte or short or int:
                WriteRaw(w, "n", Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture), 0);
                return;
            case long l when Math.Abs(l) < 1_000_000_000_000_000L:
                WriteRaw(w, "n", l.ToString(CultureInfo.InvariantCulture), 0);
                return;
            case decimal m when HasAtMost15SignificantDigits(m):
                WriteRaw(w, "n", m.ToString(CultureInfo.InvariantCulture), 0);
                return;
            case double d when double.IsFinite(d):
                WriteRaw(w, "n", d.ToString("R", CultureInfo.InvariantCulture), 0);
                return;
            case float f when float.IsFinite(f):
                WriteRaw(w, "n", ((double)f).ToString("R", CultureInfo.InvariantCulture), 0);
                return;
            case DateTime dt when dt.Year >= 1900:
                bool dateOnly = column.SqlTypeName.Equals("date", StringComparison.OrdinalIgnoreCase);
                WriteRaw(w, "n", dt.ToOADate().ToString("R", CultureInfo.InvariantCulture), dateOnly ? StyleDate : StyleDateTime);
                return;
            default:
                // Values Excel would round (long / decimal beyond 15 digits) stay exact as text.
                WriteInlineString(w, ValueFormatter.Format(value, column, FormatOptions.Export), 0);
                return;
        }
    }

    private static bool HasAtMost15SignificantDigits(decimal m)
    {
        string digits = Math.Abs(m).ToString(CultureInfo.InvariantCulture).Replace(".", "").TrimStart('0').TrimEnd('0');
        return digits.Length <= 15;
    }

    private static void WriteRaw(XmlWriter w, string type, string value, int style)
    {
        w.WriteStartElement("c");
        if (type != "n")
            w.WriteAttributeString("t", type);
        if (style != 0)
            w.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
        w.WriteElementString("v", value);
        w.WriteEndElement();
    }

    private static void WriteInlineString(XmlWriter w, string text, int style)
    {
        w.WriteStartElement("c");
        w.WriteAttributeString("t", "inlineStr");
        if (style != 0)
            w.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
        w.WriteStartElement("is");
        w.WriteStartElement("t");
        w.WriteAttributeString("xml", "space", null, "preserve");
        w.WriteString(SanitizeForXml(text.Length > MaxCellText ? text[..MaxCellText] : text));
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();
    }

    /// <summary>Replaces characters that XML 1.0 cannot contain (most control characters, lone surrogates).</summary>
    internal static string SanitizeForXml(string text)
    {
        StringBuilder? sb = null;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool valid;
            if (char.IsHighSurrogate(c))
            {
                valid = i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
                if (valid)
                {
                    sb?.Append(c).Append(text[i + 1]);
                    i++;
                    continue;
                }
            }
            else
            {
                valid = XmlConvert.IsXmlChar(c);
            }

            if (!valid)
            {
                sb ??= new StringBuilder(text, 0, i, text.Length);
                sb.Append('�');
            }
            else
            {
                sb?.Append(c);
            }
        }
        return sb?.ToString() ?? text;
    }

    private void WritePackageParts()
    {
        var sheets = _sheetNames.Select((name, i) => (Name: SafeSheetName(name), Index: i + 1)).ToList();

        WriteEntry("[Content_Types].xml", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
            {string.Concat(sheets.Select(s => $"""  <Override PartName="/xl/worksheets/sheet{s.Index}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>{"\n"}"""))}</Types>
            """);

        WriteEntry("_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);

        WriteEntry("xl/workbook.xml", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets>
            {string.Concat(sheets.Select(s => $"""    <sheet name="{XmlEscape(s.Name)}" sheetId="{s.Index}" r:id="rId{s.Index}"/>{"\n"}"""))}  </sheets>
            </workbook>
            """);

        WriteEntry("xl/_rels/workbook.xml.rels", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
            {string.Concat(sheets.Select(s => $"""  <Relationship Id="rId{s.Index}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{s.Index}.xml"/>{"\n"}"""))}  <Relationship Id="rId{sheets.Count + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
            </Relationships>
            """);

        // Style indexes: 0 default, 1 bold header, 2 date-time, 3 date.
        WriteEntry("xl/styles.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <numFmts count="2">
                <numFmt numFmtId="164" formatCode="yyyy\-mm\-dd\ hh:mm:ss"/>
                <numFmt numFmtId="165" formatCode="yyyy\-mm\-dd"/>
              </numFmts>
              <fonts count="2">
                <font><sz val="11"/><name val="Calibri"/></font>
                <font><b/><sz val="11"/><name val="Calibri"/></font>
              </fonts>
              <fills count="2">
                <fill><patternFill patternType="none"/></fill>
                <fill><patternFill patternType="gray125"/></fill>
              </fills>
              <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
              <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
              <cellXfs count="4">
                <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
                <xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/>
                <xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
                <xf numFmtId="165" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
              </cellXfs>
              <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
            </styleSheet>
            """);
    }

    private void WriteEntry(string name, string content)
    {
        var entry = _zip!.CreateEntry(name, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    /// <summary>Sheet names: at most 31 characters and none of : \ / ? * [ ].</summary>
    private static string SafeSheetName(string name)
    {
        var chars = name.Select(c => ":\\/?*[]".Contains(c) ? '_' : c).ToArray();
        string safe = new string(chars).Trim('\'');
        return safe.Length > 31 ? safe[..31] : safe;
    }

    private static string XmlEscape(string s) => System.Security.SecurityElement.Escape(s) ?? "";
}
