using Microsoft.Data.SqlClient;
using Quarry.Core.Execution;
using Quarry.Core.Metadata;
using Quarry.Core.Results;
using Quarry.Parsing;

namespace Quarry.Core.Tests;

/// <summary>Collects everything the executor reports, in order.</summary>
internal sealed class RecordingSink : IExecutionSink
{
    public List<ResultSet> ResultSets { get; } = [];
    public List<ExecutionMessage> Messages { get; } = [];
    public List<string> Events { get; } = [];

    public void BatchStarted(ExecutionUnit unit, int iteration) => Events.Add($"batch {iteration}");

    public void ResultSetStarted(int index, IReadOnlyList<ColumnInfo> columns)
    {
        Assert.Equal(ResultSets.Count, index);
        ResultSets.Add(new ResultSet(columns));
        Events.Add("resultset");
    }

    public void RowsReceived(int index, IReadOnlyList<object?[]> rows) => ResultSets[index].Rows.AddRange(rows);

    public void ResultSetCompleted(int index, long rowCount, bool truncated) => ResultSets[index].IsTruncated = truncated;

    public void Message(ExecutionMessage message)
    {
        Messages.Add(message);
        Events.Add($"{message.Kind}: {message.Text.Split('\n')[^1]}");
    }

    public void BatchCompleted(ExecutionUnit unit, TimeSpan elapsed, bool hadErrors) => Events.Add(hadErrors ? "batch failed" : "batch ok");
}

[Collection(SqlServerCollection.Name)]
[Trait("Category", "Integration")]
public class QueryExecutorTests(SqlServerFixture server)
{
    private static async Task<(ExecutionSummary Summary, RecordingSink Sink)> RunScriptAsync(
        SqlConnection connection, string script, ExecutionOptions? options = null, CancellationToken ct = default)
    {
        var sink = new RecordingSink();
        var units = ExecutionPlanner.ForScript(ScriptAnalyzer.Analyze(script));
        var summary = await QueryExecutor.ExecuteAsync(connection, units, sink, options ?? new ExecutionOptions(), ct);
        return (summary, sink);
    }

    [SqlServerFact]
    public async Task MultipleResultSets_AndTypes()
    {
        await using var conn = await server.OpenAsync();
        var (summary, sink) = await RunScriptAsync(conn,
            "SELECT 1 AS a, N'x' AS b\nSELECT CAST(NULL AS int) AS c, CAST(12.5 AS decimal(10,2)) AS d, CAST('2024-01-02' AS date) AS e");

        Assert.True(summary.Succeeded);
        Assert.Equal(2, sink.ResultSets.Count);
        Assert.Equal(["a", "b"], sink.ResultSets[0].Columns.Select(c => c.Name));
        Assert.Equal([1, "x"], sink.ResultSets[0].Rows[0]);
        var second = sink.ResultSets[1];
        Assert.Null(second.Rows[0][0]);
        Assert.Equal("12.50", ValueFormatter.Format(second.Rows[0][1], second.Columns[1]));
        Assert.Equal("2024-01-02", ValueFormatter.Format(second.Rows[0][2], second.Columns[2]));
    }

    [SqlServerFact]
    public async Task PrintErrorsAndRowCounts_ArriveInOrder()
    {
        await using var conn = await server.OpenAsync();
        var (summary, sink) = await RunScriptAsync(conn, """
            PRINT 'one'
            CREATE TABLE #t (i int)
            INSERT #t VALUES (1), (2)
            RAISERROR('two', 16, 1)
            PRINT 'three'
            """);

        Assert.False(summary.Succeeded);
        var relevant = sink.Events.Where(e => !e.StartsWith("batch")).ToList();
        Assert.Equal(["Info: one", "RowsAffected: (2 rows affected)", "Error: two", "Info: three"], relevant);
    }

    [SqlServerFact]
    public async Task ErrorLine_IsMappedToDocumentLine()
    {
        await using var conn = await server.OpenAsync();
        var (_, sink) = await RunScriptAsync(conn, "SELECT 1\nGO\n\nSELECT 1\nSELECT * FROM dbo.DoesNotExist_Quarry");

        var error = Assert.Single(sink.Messages, m => m.Kind == MessageKind.Error);
        Assert.Equal(4, error.DocumentLine);
        Assert.Contains("Line 5", error.Text);
        Assert.Equal(208, error.Number);
    }

    [SqlServerFact]
    public async Task FailingBatch_DoesNotStopLaterBatches_UnlessStopOnError()
    {
        await using var conn = await server.OpenAsync();
        const string script = "SELECT * FROM dbo.DoesNotExist_Quarry\nGO\nSELECT 2";

        var (_, sink) = await RunScriptAsync(conn, script);
        Assert.Single(sink.ResultSets);

        var (_, stopped) = await RunScriptAsync(conn, script, new ExecutionOptions { StopOnError = true });
        Assert.Empty(stopped.ResultSets);
    }

    [SqlServerFact]
    public async Task GoCount_RepeatsBatch()
    {
        await using var conn = await server.OpenAsync();
        var (_, sink) = await RunScriptAsync(conn, "CREATE TABLE #g (i int)\nGO\nINSERT #g VALUES (1)\nGO 3\nSELECT COUNT(*) FROM #g");
        Assert.Equal(3, sink.ResultSets.Single().Rows[0][0]);
    }

    [SqlServerFact]
    public async Task SessionState_PersistsAcrossRunsOnOneConnection()
    {
        await using var conn = await server.OpenAsync();
        await RunScriptAsync(conn, "CREATE TABLE #keep (v int); INSERT #keep VALUES (42)");
        var (_, sink) = await RunScriptAsync(conn, "SELECT v FROM #keep");
        Assert.Equal(42, sink.ResultSets.Single().Rows[0][0]);
    }

    [SqlServerFact]
    public async Task RowLimit_TruncatesAndContinues()
    {
        await using var conn = await server.OpenAsync();
        var (_, sink) = await RunScriptAsync(conn,
            "SELECT TOP (50) object_id FROM sys.all_objects\nSELECT 'after'",
            new ExecutionOptions { MaxRowsPerResultSet = 10, ChunkSize = 3 });
        Assert.Equal(10, sink.ResultSets[0].Rows.Count);
        Assert.True(sink.ResultSets[0].IsTruncated);
        Assert.Equal("after", sink.ResultSets[1].Rows[0][0]);
    }

    [SqlServerFact]
    public async Task Cancellation_StopsLongQuery()
    {
        await using var conn = await server.OpenAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var started = DateTime.UtcNow;
        var (summary, _) = await RunScriptAsync(conn, "WAITFOR DELAY '00:01:00'\nGO\nSELECT 1", ct: cts.Token);
        Assert.True(summary.Cancelled);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20));
    }

    [SqlServerFact]
    public async Task UseDatabase_IsReflectedInCurrentDatabase()
    {
        await using var conn = await server.OpenAsync();
        await RunScriptAsync(conn, "USE tempdb");
        Assert.Equal("tempdb", await QueryExecutor.GetCurrentDatabaseAsync(conn));
    }

    [SqlServerFact]
    public async Task Metadata_ReadsObjectsColumnsAndScripts()
    {
        string table = $"QuarryTest_{Guid.NewGuid():N}";
        var service = new MetadataService(db =>
        {
            var builder = new SqlConnectionStringBuilder(server.ConnectionString);
            if (db is not null)
                builder.InitialCatalog = db;
            return new SqlConnection(builder.ConnectionString);
        });

        await using var conn = await server.OpenAsync();
        await RunScriptAsync(conn, $"USE tempdb\nCREATE TABLE dbo.{table} (id int IDENTITY PRIMARY KEY, name nvarchar(50) NULL, amount decimal(9,2) NOT NULL)");
        try
        {
            Assert.Contains("tempdb", await service.GetDatabasesAsync());
            var obj = Assert.Single(await service.GetObjectsAsync("tempdb"), o => o.Name == table);
            Assert.Equal(DbObjectKind.Table, obj.Kind);

            var columns = await service.GetColumnsAsync("tempdb", obj.ObjectId);
            Assert.Equal(["id", "name", "amount"], columns.Select(c => c.Name));
            Assert.True(columns[0].IsPrimaryKey && columns[0].IsIdentity);
            Assert.Equal("nvarchar(50)", columns[1].TypeName);
            Assert.Equal("decimal(9,2)", columns[2].TypeName);

            var index = Assert.Single(await service.GetIndexesAsync("tempdb", obj.ObjectId));
            Assert.True(index.IsPrimaryKey);
            Assert.Equal("id", index.Columns);

            string create = await service.ScriptCreateAsync("tempdb", obj);
            Assert.Contains("[id] int IDENTITY NOT NULL", create);
            Assert.Contains("PRIMARY KEY ([id])", create);
        }
        finally
        {
            await RunScriptAsync(conn, $"DROP TABLE tempdb.dbo.{table}");
        }
    }
}
