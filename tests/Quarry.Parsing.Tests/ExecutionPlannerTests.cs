using Quarry.Parsing;

namespace Quarry.Parsing.Tests;

public class ExecutionPlannerTests
{
    /// <summary>Removes the caret marker '§' from <paramref name="marked"/> and returns the text to run for it.</summary>
    private static string? QueryAt(string marked)
    {
        int caret = marked.IndexOf('§');
        string sql = marked.Remove(caret, 1);
        return ExecutionPlanner.ForQuery(ScriptAnalyzer.Analyze(sql), caret)?.Text;
    }

    [Theory]
    [InlineData("SEL§ECT 1\nSELECT 2", "SELECT 1")]
    [InlineData("SELECT 1\nSELECT §2", "SELECT 2")]
    [InlineData("§SELECT 1\nSELECT 2", "SELECT 1")]
    [InlineData("SELECT 1§\nSELECT 2", "SELECT 1")]
    [InlineData("SELECT 1\n§SELECT 2", "SELECT 2")]
    public void CaretInsideOrAtEdgeOfStatement(string marked, string expected)
    {
        Assert.Equal(expected, QueryAt(marked));
    }

    [Fact]
    public void CaretAfterSemicolon_RunsThatStatement()
    {
        Assert.Equal("SELECT 1;", QueryAt("SELECT 1;§\nSELECT 2"));
    }

    [Fact]
    public void CaretInTrailingWhitespaceOnSameLine_RunsPreviousStatement()
    {
        Assert.Equal("SELECT 1", QueryAt("SELECT 1   §\n\nSELECT 2"));
    }

    [Fact]
    public void CaretOnLineDirectlyBelow_RunsPreviousStatement()
    {
        Assert.Equal("SELECT 1", QueryAt("SELECT 1\n§\n\nSELECT 2"));
    }

    [Fact]
    public void CaretAfterBlankLine_RunsNextStatement()
    {
        Assert.Equal("SELECT 2", QueryAt("SELECT 1\n\n§\nSELECT 2"));
    }

    [Fact]
    public void CaretOnCommentAboveStatement_RunsThatStatement()
    {
        Assert.Equal("SELECT 2", QueryAt("SELECT 1\n\n-- users§\nSELECT 2"));
    }

    [Fact]
    public void CaretAfterEverythingPastBlankLines_RunsLastStatement()
    {
        Assert.Equal("SELECT 1", QueryAt("SELECT 1\n\n\n§"));
    }

    [Fact]
    public void CaretInsideIfBlock_RunsWholeIf()
    {
        const string ifBlock = "IF 1 = 1\nBEGIN\n  SELECT 1\n  SELECT 2\nEND";
        Assert.Equal(ifBlock, QueryAt("IF 1 = 1\nBEGIN\n  SELECT §1\n  SELECT 2\nEND\nSELECT 3"));
    }

    [Fact]
    public void CaretOnGoLine_UsesItsOwnBatch()
    {
        Assert.Equal("SELECT 1", QueryAt("SELECT 1\nG§O\nSELECT 2"));
    }

    [Fact]
    public void StatementsInOtherBatches_AreNotChosen()
    {
        Assert.Null(QueryAt("SELECT 1\nGO\n-- only a comment§\n"));
    }

    [Fact]
    public void EmptyScript_RunsNothing()
    {
        Assert.Null(QueryAt("§"));
    }

    [Fact]
    public void ForQuery_ReportsStartLineForErrorMapping()
    {
        const string sql = "SELECT 1\n\nSELECT 2\nFROM nowhere";
        var unit = ExecutionPlanner.ForQuery(ScriptAnalyzer.Analyze(sql), sql.Length)!;
        Assert.Equal(2, unit.StartLine);
        Assert.Equal(3, unit.MapServerLine(2));
    }

    [Fact]
    public void ForScript_ReturnsBatchesWithRepeatCountsAndSkipsEmptyOnes()
    {
        const string sql = "SELECT 1\nGO\n-- comment only\nGO\n\nGO\nINSERT t DEFAULT VALUES\nGO 3\n";
        var units = ExecutionPlanner.ForScript(ScriptAnalyzer.Analyze(sql));
        Assert.Equal(2, units.Count);
        Assert.Equal("SELECT 1\n", units[0].Text);
        Assert.Equal(0, units[0].StartLine);
        Assert.Equal("INSERT t DEFAULT VALUES\n", units[1].Text);
        Assert.Equal(6, units[1].StartLine);
        Assert.Equal(3, units[1].RepeatCount);
    }

    [Fact]
    public void ForScript_KeepsWholeBatchText_SoVariablesStayInScope()
    {
        const string sql = "DECLARE @x int = 1\nSELECT @x";
        var unit = Assert.Single(ExecutionPlanner.ForScript(ScriptAnalyzer.Analyze(sql)));
        Assert.Equal(sql, unit.Text);
    }

    [Fact]
    public void ForScript_IncludesBatchesWithSyntaxErrors()
    {
        var units = ExecutionPlanner.ForScript(ScriptAnalyzer.Analyze("SELEC 1\nGO\nSELECT 2"));
        Assert.Equal(2, units.Count);
    }

    [Fact]
    public void ForSelection_SplitsOnGoAndMapsOffsets()
    {
        const string sql = "SELECT 0\nSELECT 1\nGO\nSELECT 2\nSELECT 3";
        var model = ScriptAnalyzer.Analyze(sql);
        int start = sql.IndexOf("SELECT 1", StringComparison.Ordinal);
        int end = sql.IndexOf("SELECT 3", StringComparison.Ordinal);
        var units = ExecutionPlanner.ForSelection(model, TextSpan.FromBounds(start, end));
        Assert.Equal(["SELECT 1\n", "SELECT 2\n"], units.Select(u => u.Text));
        Assert.Equal([1, 3], units.Select(u => u.StartLine));
        Assert.Equal(start, units[0].Span.Start);
    }
}
