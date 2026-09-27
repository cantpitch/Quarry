using Quarry.Parsing;

namespace Quarry.Parsing.Tests;

public class ScriptAnalyzerTests
{
    private static string[] StatementTexts(string sql)
    {
        var model = ScriptAnalyzer.Analyze(sql);
        return model.Statements.Select(s => sql.Substring(s.Span.Start, s.Span.Length)).ToArray();
    }

    [Fact]
    public void StatementsWithoutSemicolons_AreSeparated()
    {
        Assert.Equal(
            ["SELECT * FROM a", "SELECT * FROM b WHERE x = 1", "UPDATE c SET y = 2"],
            StatementTexts("SELECT * FROM a\nSELECT * FROM b WHERE x = 1\nUPDATE c SET y = 2"));
    }

    [Fact]
    public void StatementsOnOneLineWithoutSemicolons_AreSeparated()
    {
        Assert.Equal(["SELECT 1", "SELECT 2"], StatementTexts("SELECT 1 SELECT 2"));
    }

    [Fact]
    public void Semicolons_EndStatements_AndAreIncluded()
    {
        Assert.Equal(["SELECT 1;", "SELECT 2;", "SELECT 3"], StatementTexts("SELECT 1; SELECT 2;\nSELECT 3"));
    }

    [Fact]
    public void MultiLineStatement_IsOneStatement()
    {
        const string sql = "SELECT a,\n       b\nFROM t\nWHERE a = 1\nORDER BY b";
        Assert.Equal([sql], StatementTexts(sql));
    }

    [Fact]
    public void IfElse_IsOneTopLevelStatement()
    {
        const string ifElse = "IF @x = 1\nBEGIN\n  SELECT 1\n  SELECT 2\nEND\nELSE\n  SELECT 3";
        Assert.Equal(["DECLARE @x int = 1", ifElse, "SELECT 4"],
            StatementTexts($"DECLARE @x int = 1\n{ifElse}\nSELECT 4"));
    }

    [Fact]
    public void TryCatch_IsOneTopLevelStatement()
    {
        const string sql = "BEGIN TRY\n  SELECT 1/0\nEND TRY\nBEGIN CATCH\n  SELECT ERROR_MESSAGE()\nEND CATCH";
        Assert.Equal([sql], StatementTexts(sql));
    }

    [Fact]
    public void WhileLoop_IsOneTopLevelStatement()
    {
        const string loop = "WHILE @i < 10\nBEGIN\n  SET @i += 1\nEND";
        Assert.Equal(["DECLARE @i int = 0", loop], StatementTexts($"DECLARE @i int = 0\n{loop}"));
    }

    [Fact]
    public void CteWithLeadingSemicolon()
    {
        var texts = StatementTexts("SELECT 1\n;WITH c AS (SELECT 1 AS n)\nSELECT n FROM c");
        Assert.Equal(2, texts.Length);
        Assert.StartsWith("SELECT 1", texts[0]);
        Assert.Equal("WITH c AS (SELECT 1 AS n)\nSELECT n FROM c", texts[1]);
    }

    [Fact]
    public void Merge_IsOneStatement()
    {
        const string merge = "MERGE t AS tgt\nUSING s AS src ON tgt.id = src.id\nWHEN MATCHED THEN UPDATE SET v = src.v\nWHEN NOT MATCHED THEN INSERT (id, v) VALUES (src.id, src.v);";
        Assert.Equal([merge, "SELECT 1"], StatementTexts($"{merge}\nSELECT 1"));
    }

    [Fact]
    public void CreateProcedure_BodyIsOneStatement()
    {
        const string proc = "CREATE PROCEDURE dbo.p AS\nBEGIN\n  SELECT 1\n  SELECT 2\nEND";
        var model = ScriptAnalyzer.Analyze($"{proc}\nGO\nEXEC dbo.p");
        Assert.Equal(2, model.Statements.Count);
        Assert.Equal(proc, model.Text.Substring(model.Statements[0].Span.Start, model.Statements[0].Span.Length));
        Assert.Equal(1, model.Statements[1].BatchIndex);
    }

    [Fact]
    public void Comments_AreNotStatements()
    {
        Assert.Equal(["SELECT 1", "SELECT 2"], StatementTexts("-- first\nSELECT 1\n/* second */\nSELECT 2 -- trailing"));
    }

    [Fact]
    public void StatementOffsets_AreDocumentOffsetsAcrossBatches()
    {
        const string sql = "SELECT 1\nGO\nSELECT 'x'\nGO 2\nSELECT 3";
        var model = ScriptAnalyzer.Analyze(sql);
        Assert.Equal(["SELECT 1", "SELECT 'x'", "SELECT 3"],
            model.Statements.Select(s => sql.Substring(s.Span.Start, s.Span.Length)));
        Assert.Equal([0, 1, 2], model.Statements.Select(s => s.BatchIndex));
    }

    [Fact]
    public void ValidScript_HasNoErrors()
    {
        Assert.Empty(ScriptAnalyzer.Analyze("SELECT 1\nGO\nSELECT 2").Errors);
    }

    [Fact]
    public void QuotedIdentifiers_AreAccepted()
    {
        Assert.Equal(["SELECT \"col\" FROM \"t\""], StatementTexts("SELECT \"col\" FROM \"t\""));
    }

    [Fact]
    public void SyntaxError_IsReportedWithDocumentPosition()
    {
        const string sql = "SELECT 1\nGO\nSELECT 2\nSELEC 3";
        var model = ScriptAnalyzer.Analyze(sql);
        var error = Assert.Single(model.Errors);
        Assert.Equal(3, error.Line);
    }

    [Fact]
    public void SyntaxError_FallsBackToBlankLineChunks_ParsingGoodChunks()
    {
        const string sql = "SELECT 1\nSELECT 2\n\nSELEC oops\n\nSELECT 3; SELECT 4";
        Assert.Equal(["SELECT 1", "SELECT 2", "SELEC oops", "SELECT 3;", "SELECT 4"], StatementTexts(sql));
    }

    [Fact]
    public void SyntaxError_BadChunkIsCutAtSemicolonsOutsideStrings()
    {
        const string sql = "SELEC 'a;b'; SELECT 2";
        Assert.Equal(["SELEC 'a;b';", "SELECT 2"], StatementTexts(sql));
    }

    [Fact]
    public void BlankLineInsideComment_DoesNotSplitFallbackChunk()
    {
        const string sql = "SELEC /* one\n\ntwo */ x";
        Assert.Equal([sql], StatementTexts(sql));
    }

    [Fact]
    public void OnlyComments_HasNoStatements()
    {
        var model = ScriptAnalyzer.Analyze("-- nothing here\n/* at all */");
        Assert.Empty(model.Statements);
        Assert.Empty(model.Errors);
    }
}
