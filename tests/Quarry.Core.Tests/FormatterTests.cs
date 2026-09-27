using Quarry.Core.Results;

namespace Quarry.Core.Tests;

public class FormatterTests
{
    private static ColumnInfo Col(string name, string sqlType, Type clr, int? scale = null) => new(name, sqlType, clr, true, Scale: scale);

    private static ResultSet Sample()
    {
        var rs = new ResultSet([Col("id", "int", typeof(int)), Col("name", "nvarchar", typeof(string)), Col("", "decimal", typeof(decimal), 2)]);
        rs.Rows.Add([1, "plain", 1.5m]);
        rs.Rows.Add([22, "has, comma", null]);
        rs.Rows.Add([3, "quote \"here\"\nnewline", 10m]);
        return rs;
    }

    [Theory]
    [InlineData(null, "NULL")]
    [InlineData(true, "1")]
    [InlineData(3.25d, "3.25")]
    public void FormatsScalars(object? value, string expected)
    {
        Assert.Equal(expected, ValueFormatter.Format(value, Col("c", "x", typeof(object))));
    }

    [Fact]
    public void FormatsDatesBySqlType()
    {
        var dt = new DateTime(2024, 1, 2, 3, 4, 5, 678);
        Assert.Equal("2024-01-02", ValueFormatter.Format(dt, Col("c", "date", typeof(DateTime))));
        Assert.Equal("2024-01-02 03:04:05.678", ValueFormatter.Format(dt, Col("c", "datetime", typeof(DateTime))));
        Assert.Equal("2024-01-02 03:04:05.678", ValueFormatter.Format(dt, Col("c", "datetime2", typeof(DateTime), 3)));
        Assert.Equal("03:04:05.12", ValueFormatter.Format(new TimeSpan(0, 3, 4, 5).Add(TimeSpan.FromMilliseconds(120)), Col("c", "time", typeof(TimeSpan), 2)));
    }

    [Fact]
    public void FormatsBinaryAsHexAndTruncates()
    {
        Assert.Equal("0x0A0B", ValueFormatter.Format(new byte[] { 10, 11 }, Col("c", "varbinary", typeof(byte[]))));
        var options = FormatOptions.Display with { MaxBinaryBytes = 1 };
        Assert.Equal("0x0A…", ValueFormatter.Format(new byte[] { 10, 11 }, Col("c", "varbinary", typeof(byte[])), options));
    }

    [Fact]
    public void DecimalKeepsDeclaredScale()
    {
        Assert.Equal("1.50", ValueFormatter.Format(1.5m, Col("c", "decimal", typeof(decimal), 2)));
    }

    [Fact]
    public void LongStringsAreTruncatedForDisplayOnly()
    {
        string s = new('x', 2000);
        Assert.EndsWith("…", ValueFormatter.Format(s, Col("c", "nvarchar", typeof(string))));
        Assert.Equal(s, ValueFormatter.Format(s, Col("c", "nvarchar", typeof(string)), FormatOptions.Export));
    }

    [Fact]
    public void Csv_QuotesPerRfc4180_NullIsEmpty()
    {
        var sw = new StringWriter();
        DelimitedFormatter.Write(sw, Sample(), ',');
        Assert.Equal(
            "id,name,\r\n1,plain,1.50\r\n22,\"has, comma\",\r\n3,\"quote \"\"here\"\"\nnewline\",10.00\r\n",
            sw.ToString());
    }

    [Fact]
    public void Tsv_UsesTabsAndQuotesOnlyWhenNeeded()
    {
        var sw = new StringWriter();
        DelimitedFormatter.Write(sw, Sample(), '\t');
        var lines = sw.ToString().Split("\r\n");
        Assert.Equal("id\tname\t", lines[0]);
        Assert.Equal("22\thas, comma\t", lines[2]);
    }

    [Fact]
    public void TextTable_PadsColumnsAndRightAlignsNumbers()
    {
        var sw = new StringWriter();
        TextTableFormatter.Write(sw, Sample());
        var lines = sw.ToString().Split(Environment.NewLine);
        // Widths: id = 2, name = 20 ("quote "here" newline" with the line break flattened), third = 16.
        static string Line(string a, string b, string c) => $"{a,2} {b,-20} {c,16}";
        Assert.Equal("id " + "name".PadRight(20) + " (No column name)", lines[0]);
        Assert.Equal("-- " + new string('-', 20) + " " + new string('-', 16), lines[1]);
        Assert.Equal(Line("1", "plain", "1.50"), lines[2]);
        Assert.Equal(Line("22", "has, comma", "NULL"), lines[3]);
        Assert.Equal(Line("3", "quote \"here\" newline", "10.00"), lines[4]);
        Assert.Equal("(3 rows affected)", lines[6]);
    }
}
