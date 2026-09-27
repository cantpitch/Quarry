using Quarry.Core.History;
using Quarry.Parsing;

namespace Quarry.Core.Tests;

public sealed class QueryHistoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"quarry-history-{Guid.NewGuid():N}.jsonl");

    public void Dispose()
    {
        File.Delete(_path);
        File.Delete(_path + ".tmp");
    }

    private static QueryHistoryEntry Entry(string text, DateTimeOffset? at = null) => new()
    {
        Text = text,
        Server = "db1",
        ServerDisplayName = "Prod",
        Database = "app",
        Timestamp = at ?? DateTimeOffset.Now,
        Kind = HistoryRunKind.Query,
        Outcome = HistoryOutcome.Succeeded,
        DurationMs = 12,
        RowCount = 3,
        ResultSetCount = 1,
    };

    [Fact]
    public void AppendAndLoad_NewestFirst_RoundTripsAllFields()
    {
        var store = new QueryHistoryStore(_path);
        var first = store.Append(Entry("SELECT 1"));
        var second = store.Append(Entry("SELECT 2") with { Outcome = HistoryOutcome.Failed, ExportPath = "/tmp/x.csv" });

        var loaded = new QueryHistoryStore(_path).Load();
        Assert.Equal([second.Id, first.Id], loaded.Select(e => e.Id));
        Assert.Equal(second, loaded[0]);
        Assert.Equal(first, loaded[1]);
        Assert.Equal(2, File.ReadAllLines(_path).Length); // one JSON object per line
    }

    [Fact]
    public void Load_MissingFile_IsEmpty()
        => Assert.Empty(new QueryHistoryStore(_path).Load());

    [Fact]
    public void Load_SkipsCorruptLines()
    {
        var store = new QueryHistoryStore(_path);
        store.Append(Entry("SELECT 1"));
        File.AppendAllText(_path, "{\"text\": \"half a line\n");
        store.Append(Entry("SELECT 2"));

        Assert.Equal(["SELECT 2", "SELECT 1"], new QueryHistoryStore(_path).Load().Select(e => e.Text));
    }

    [Fact]
    public void Append_TruncatesVeryLongText()
    {
        var stored = new QueryHistoryStore(_path).Append(Entry(new string('x', QueryHistoryStore.MaxTextLength + 10)));
        Assert.True(stored.TextTruncated);
        Assert.Equal(QueryHistoryStore.MaxTextLength, stored.Text.Length);
    }

    [Fact]
    public void Append_CompactsToNewestEntries()
    {
        var store = new QueryHistoryStore(_path);
        int total = QueryHistoryStore.MaxEntries + QueryHistoryStore.MaxEntries / 5 + 1;
        for (int i = 0; i < total; i++)
            store.Append(Entry($"SELECT {i}"));

        var loaded = store.Load();
        Assert.Equal(QueryHistoryStore.MaxEntries, loaded.Count);
        Assert.Equal($"SELECT {total - 1}", loaded[0].Text);
    }

    [Fact]
    public void DeleteAndClear()
    {
        var store = new QueryHistoryStore(_path);
        var a = store.Append(Entry("SELECT 1"));
        store.Append(Entry("SELECT 2"));

        store.Delete(a.Id);
        Assert.Equal(["SELECT 2"], store.Load().Select(e => e.Text));

        store.Clear();
        Assert.Empty(store.Load());
        store.Append(Entry("SELECT 3"));
        Assert.Single(store.Load());
    }

    [Fact]
    public void ScriptFromUnits_RebuildsBatchesWithGo()
    {
        var model = ScriptAnalyzer.Analyze("SELECT 1\nGO\nINSERT t DEFAULT VALUES\nGO 3\n");
        Assert.Equal("SELECT 1\nGO\nINSERT t DEFAULT VALUES\nGO 3",
            QueryHistoryEntry.ScriptFromUnits(ExecutionPlanner.ForScript(model)));

        var twoBatches = ScriptAnalyzer.Analyze("SELECT 1\nGO\nSELECT 2\n");
        Assert.Equal("SELECT 1\nGO\nSELECT 2", QueryHistoryEntry.ScriptFromUnits(ExecutionPlanner.ForScript(twoBatches)));

        var single = ExecutionPlanner.ForQuery(ScriptAnalyzer.Analyze("  SELECT 42  "), 4)!;
        Assert.Equal("SELECT 42", QueryHistoryEntry.ScriptFromUnits([single]));
    }
}
