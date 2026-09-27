using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using Quarry.Core.Export;
using Quarry.Core.Results;

namespace Quarry.Core.Tests;

public sealed class ExportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"quarry-export-{Guid.NewGuid():N}");

    public ExportTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string PathFor(string name) => Path.Combine(_dir, name);

    private static ColumnInfo Col(string name, string sqlType, Type clr, int? scale = null, int? size = null)
        => new(name, sqlType, clr, true, size, Scale: scale);

    private static ResultSet People()
    {
        var rs = new ResultSet(
        [
            Col("id", "int", typeof(int)),
            Col("name", "nvarchar", typeof(string), size: 20),
            Col("balance", "decimal", typeof(decimal), 2),
            Col("born", "date", typeof(DateTime)),
            Col("seen", "datetime2", typeof(DateTime), 3),
            Col("active", "bit", typeof(bool)),
            Col("", "varbinary", typeof(byte[])),
        ]);
        rs.Rows.Add([1, "Ann, \"A\"", 12.5m, new DateTime(1990, 5, 1), new DateTime(2024, 1, 2, 3, 4, 5, 600), true, new byte[] { 1, 255 }]);
        rs.Rows.Add([2, "Bø\nb", null, null, null, false, null]);
        return rs;
    }

    private static ResultSet Numbers(int count)
    {
        var rs = new ResultSet([Col("n", "int", typeof(int))]);
        for (int i = 1; i <= count; i++)
            rs.Rows.Add([i]);
        return rs;
    }

    [Theory]
    [InlineData("a.csv", ExportFormat.Csv)]
    [InlineData("a.TSV", ExportFormat.Tsv)]
    [InlineData("a.txt", ExportFormat.Text)]
    [InlineData("a.json", ExportFormat.Json)]
    [InlineData("a.xlsx", ExportFormat.Xlsx)]
    [InlineData("a.sql", null)]
    public void FormatFromPath(string path, ExportFormat? expected)
        => Assert.Equal(expected, ResultExporter.FormatFromPath(path));

    [Fact]
    public void Csv_OneFilePerResultSet_WithBomAndQuoting()
    {
        var files = ResultExporter.Export([People(), Numbers(2)], ExportFormat.Csv, PathFor("out.csv"));

        Assert.Equal([PathFor("out.csv"), PathFor("out_2.csv")], files);
        byte[] bytes = File.ReadAllBytes(files[0]);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        string text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.Equal(
            "id,name,balance,born,seen,active,\r\n" +
            "1,\"Ann, \"\"A\"\"\",12.50,1990-05-01,2024-01-02 03:04:05.600,1,0x01FF\r\n" +
            "2,\"Bø\nb\",,,,0,\r\n",
            text);
        Assert.Equal("n\r\n1\r\n2\r\n", File.ReadAllText(files[1]));
    }

    [Fact]
    public void Tsv_WithoutHeadersOrBom()
    {
        var options = new ExportOptions { IncludeHeaders = false, Utf8Bom = false };
        var file = Assert.Single(ResultExporter.Export([Numbers(2)], ExportFormat.Tsv, PathFor("out.tsv"), options));
        Assert.Equal("1\r\n2\r\n", File.ReadAllText(file));
        Assert.NotEqual(0xEF, File.ReadAllBytes(file)[0]);
    }

    [Fact]
    public void Json_KeepsTypesAndMakesKeysUnique()
    {
        var rs = People();
        var dup = new ResultSet([Col("x", "int", typeof(int)), Col("x", "int", typeof(int))]);
        dup.Rows.Add([1, 2]);
        var files = ResultExporter.Export([rs, dup], ExportFormat.Json, PathFor("out.json"));
        Assert.Equal(2, files.Count);

        Assert.NotEqual(0xEF, File.ReadAllBytes(files[0])[0]);
        using var doc = JsonDocument.Parse(File.ReadAllText(files[0]));
        var first = doc.RootElement[0];
        Assert.Equal(1, first.GetProperty("id").GetInt32());
        Assert.Equal("Ann, \"A\"", first.GetProperty("name").GetString());
        Assert.Equal(12.5m, first.GetProperty("balance").GetDecimal());
        Assert.Equal("1990-05-01", first.GetProperty("born").GetString());
        Assert.Equal("2024-01-02T03:04:05.6", first.GetProperty("seen").GetString());
        Assert.True(first.GetProperty("active").GetBoolean());
        Assert.Equal("0x01FF", first.GetProperty("Column7").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement[1].GetProperty("balance").ValueKind);

        using var dupDoc = JsonDocument.Parse(File.ReadAllText(files[1]));
        Assert.Equal(2, dupDoc.RootElement[0].GetProperty("x_2").GetInt32());
    }

    [Fact]
    public void Text_InMemory_UsesDataWidths()
    {
        var file = Assert.Single(ResultExporter.Export([Numbers(3)], ExportFormat.Text, PathFor("out.txt")));
        var lines = File.ReadAllLines(file);
        Assert.Equal(["n", "-", "1", "2", "3", "", "(3 rows affected)"], lines);
    }

    [Fact]
    public void Text_Streaming_UsesDeclaredWidths()
    {
        using (var exporter = ResultExporter.Create(ExportFormat.Text, PathFor("s.txt")))
        {
            exporter.BeginResultSet([Col("id", "int", typeof(int)), Col("name", "nvarchar", typeof(string), size: 6)]);
            exporter.WriteRows([[7, "abcdefghij"]]);
            exporter.EndResultSet(1, false);
            exporter.Complete();
        }
        var lines = File.ReadAllLines(PathFor("s.txt"));
        Assert.Equal("id          name", lines[0]);
        Assert.Equal("----------- ------", lines[1]);
        Assert.Equal("          7 abcdef", lines[2]); // truncated to nvarchar(6), like SSMS
        Assert.Equal("(1 row affected)", lines[4]);
    }

    [Fact]
    public void Xlsx_IsValidSpreadsheetMl_WithTypedCells()
    {
        var bad = new ResultSet([Col("s", "nvarchar", typeof(string)), Col("big", "bigint", typeof(long)), Col("d", "decimal", typeof(decimal), 4)]);
        bad.Rows.Add(["ctrl\u0001char", 1234567890123456789L, 12345678901234.5678m]);
        string path = PathFor("out.xlsx");
        Assert.Equal([path], ResultExporter.Export([People(), bad], ExportFormat.Xlsx, path));

        using var doc = SpreadsheetDocument.Open(path, false);
        var errors = new OpenXmlValidator().Validate(doc).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(e => $"{e.Path?.XPath}: {e.Description}")));

        var workbook = doc.WorkbookPart!;
        var sheets = workbook.Workbook!.Sheets!.Elements<Sheet>().ToList();
        Assert.Equal(["Result 1", "Result 2"], sheets.Select(s => s.Name!.Value));

        var rows = ((WorksheetPart)workbook.GetPartById(sheets[0].Id!)).Worksheet!.Descendants<Row>().ToList();
        Assert.Equal(3, rows.Count);
        var header = rows[0].Elements<Cell>().ToList();
        Assert.Equal("id", header[0].InnerText);
        Assert.Equal(1u, header[0].StyleIndex!.Value); // bold

        var cells = rows[1].Elements<Cell>().ToList();
        Assert.Equal(7, cells.Count);
        Assert.Equal("1", cells[0].CellValue!.Text); // number
        Assert.Null(cells[0].DataType);
        Assert.Equal("Ann, \"A\"", cells[1].InnerText);
        Assert.Equal("12.5", cells[2].CellValue!.Text);
        Assert.Equal(new DateTime(1990, 5, 1).ToOADate(), double.Parse(cells[3].CellValue!.Text, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(3u, cells[3].StyleIndex!.Value); // date format
        Assert.Equal(2u, cells[4].StyleIndex!.Value); // date-time format
        Assert.Equal(CellValues.Boolean, cells[5].DataType!.Value);
        Assert.Equal("0x01FF", cells[6].InnerText);

        // Nulls keep their column position as empty cells.
        Assert.Equal(7, rows[2].Elements<Cell>().Count());

        var second = ((WorksheetPart)workbook.GetPartById(sheets[1].Id!)).Worksheet!.Descendants<Row>().ToList()[1].Elements<Cell>().ToList();
        Assert.Equal("ctrl�char", second[0].InnerText);
        Assert.Equal("1234567890123456789", second[1].InnerText); // beyond Excel precision: kept as text
        Assert.Equal(CellValues.InlineString, second[1].DataType!.Value);
        Assert.Equal("12345678901234.5678", second[2].InnerText);
    }

    [Fact]
    public void Xlsx_ContinuesOnNewSheetPastRowLimit()
    {
        string path = PathFor("big.xlsx");
        using (var exporter = new XlsxExporter(path, new ExportOptions()) { RowsPerSheet = 4 })
        {
            exporter.BeginResultSet([Col("n", "int", typeof(int))]);
            exporter.WriteRows(Enumerable.Range(1, 10).Select(i => new object?[] { i }).ToList());
            exporter.EndResultSet(10, false);
            exporter.Complete();
        }

        using var doc = SpreadsheetDocument.Open(path, false);
        Assert.Empty(new OpenXmlValidator().Validate(doc));
        var sheets = doc.WorkbookPart!.Workbook!.Sheets!.Elements<Sheet>().Select(s => s.Name!.Value).ToList();
        Assert.Equal(["Result 1", "Result 1 (2)", "Result 1 (3)", "Result 1 (4)"], sheets);
        // Each continuation sheet repeats the header: 3 data rows per sheet after the first 3.
        int dataRows = doc.WorkbookPart.WorksheetParts.Sum(p => p.Worksheet!.Descendants<Row>().Count() - 1);
        Assert.Equal(10, dataRows);
    }

    [Fact]
    public void Xlsx_WithNoResultSets_IsStillAValidWorkbook()
    {
        string path = PathFor("empty.xlsx");
        using (var exporter = ResultExporter.Create(ExportFormat.Xlsx, path))
        {
            exporter.BeginResultSet([]);
            exporter.EndResultSet(0, false);
            exporter.Complete();
        }
        using var doc = SpreadsheetDocument.Open(path, false);
        Assert.Empty(new OpenXmlValidator().Validate(doc));
        using var zip = ZipFile.OpenRead(path);
        Assert.Contains(zip.Entries, e => e.FullName == "xl/worksheets/sheet1.xml");
    }

    [Fact]
    public void PathForResultSet_AppendsIndexFromSecond()
    {
        Assert.Equal(Path.Combine("d", "r.csv"), ResultExporter.PathForResultSet(Path.Combine("d", "r.csv"), 0));
        Assert.Equal(Path.Combine("d", "r_3.csv"), ResultExporter.PathForResultSet(Path.Combine("d", "r.csv"), 2));
    }
}
