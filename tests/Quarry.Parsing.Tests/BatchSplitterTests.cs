using Quarry.Parsing;

namespace Quarry.Parsing.Tests;

public class BatchSplitterTests
{
    private static string[] Texts(string sql) => BatchSplitter.Split(sql).Select(b => b.Text).ToArray();

    [Fact]
    public void NoSeparator_IsOneBatch()
    {
        var batches = BatchSplitter.Split("SELECT 1\nSELECT 2");
        Assert.Single(batches);
        Assert.Equal("SELECT 1\nSELECT 2", batches[0].Text);
        Assert.Null(batches[0].SeparatorSpan);
    }

    [Fact]
    public void EmptyText_IsOneEmptyBatch()
    {
        var batch = Assert.Single(BatchSplitter.Split(""));
        Assert.Equal("", batch.Text);
    }

    [Fact]
    public void SplitsOnGoLines()
    {
        Assert.Equal(["SELECT 1\n", "SELECT 2\n", "SELECT 3"], Texts("SELECT 1\nGO\nSELECT 2\ngo\nSELECT 3"));
    }

    [Fact]
    public void HandlesCrLf()
    {
        var batches = BatchSplitter.Split("SELECT 1\r\nGO\r\nSELECT 2\r\n");
        Assert.Equal(["SELECT 1\r\n", "SELECT 2\r\n"], batches.Select(b => b.Text));
        Assert.Equal(TextSpan.FromBounds(10, 14), batches[0].SeparatorSpan);
        Assert.Equal(14, batches[1].Span.Start);
    }

    [Fact]
    public void TrailingGo_DoesNotCreateEmptyBatch()
    {
        Assert.Equal(["SELECT 1\n"], Texts("SELECT 1\nGO"));
        Assert.Equal(["SELECT 1\n"], Texts("SELECT 1\nGO\n"));
    }

    [Theory]
    [InlineData("  GO  ")]
    [InlineData("\tgo")]
    [InlineData("Go -- end of batch")]
    [InlineData("GO--x")]
    public void SeparatorVariants_AreRecognised(string goLine)
    {
        Assert.Equal(2, BatchSplitter.Split($"SELECT 1\n{goLine}\nSELECT 2").Count);
    }

    [Fact]
    public void RepeatCount_IsParsed()
    {
        var batches = BatchSplitter.Split("INSERT t DEFAULT VALUES\nGO 5\nSELECT 1");
        Assert.Equal(5, batches[0].RepeatCount);
        Assert.Equal(1, batches[1].RepeatCount);
    }

    [Theory]
    [InlineData("GOTO label")]
    [InlineData("GO SELECT 1")]
    [InlineData("SELECT 1 GO")]
    [InlineData("GO /* c */")]
    [InlineData("GO x")]
    [InlineData("GOGO")]
    public void NotSeparators(string line)
    {
        Assert.Single(BatchSplitter.Split($"SELECT 1\n{line}\nSELECT 2"));
    }

    [Fact]
    public void GoInsideBlockComment_IsIgnored()
    {
        Assert.Single(BatchSplitter.Split("SELECT 1\n/*\nGO\n*/\nSELECT 2"));
    }

    [Fact]
    public void GoInsideNestedBlockComment_IsIgnored()
    {
        Assert.Single(BatchSplitter.Split("/* outer /* inner */\nGO\n*/\nSELECT 1"));
    }

    [Fact]
    public void GoAfterNestedCommentCloses_Splits()
    {
        Assert.Equal(2, BatchSplitter.Split("/* a /* b */ c */\nGO\nSELECT 1").Count);
    }

    [Fact]
    public void GoInsideString_IsIgnored()
    {
        Assert.Single(BatchSplitter.Split("SELECT 'line1\nGO\nline3'"));
    }

    [Fact]
    public void GoInsideStringWithEscapedQuotes_IsIgnored()
    {
        Assert.Single(BatchSplitter.Split("SELECT 'it''s\nGO\n'''"));
        Assert.Equal(2, BatchSplitter.Split("SELECT 'it''s'\nGO\nSELECT 2").Count);
    }

    [Fact]
    public void GoInsideBracketOrQuotedIdentifier_IsIgnored()
    {
        Assert.Single(BatchSplitter.Split("SELECT 1 AS [a\nGO\n]"));
        Assert.Single(BatchSplitter.Split("SELECT 1 AS \"a\nGO\n\""));
    }

    [Fact]
    public void GoAfterLineComment_Splits()
    {
        Assert.Equal(2, BatchSplitter.Split("SELECT 1 -- it's a comment\nGO\nSELECT 2").Count);
    }

    [Fact]
    public void CustomSeparator()
    {
        Assert.Equal(2, BatchSplitter.Split("SELECT 1\nENDBATCH\nSELECT 2", "ENDBATCH").Count);
        Assert.Single(BatchSplitter.Split("SELECT 1\nGO\nSELECT 2", "ENDBATCH"));
    }
}
