using Quarry.Core.Execution;
using Quarry.Core.Results;
using Quarry.Parsing;

namespace Quarry.Core.Export;

/// <summary>
/// Streams result rows straight into an <see cref="IResultExporter"/> while forwarding messages and
/// batch events to another sink (e.g. the Messages pane). Rows never accumulate in memory.
/// </summary>
public sealed class ExportingSink(IResultExporter exporter, IExecutionSink messages) : IExecutionSink
{
    public int ResultSetCount { get; private set; }

    public long RowCount { get; private set; }

    public void BatchStarted(ExecutionUnit unit, int iteration) => messages.BatchStarted(unit, iteration);

    public void ResultSetStarted(int index, IReadOnlyList<ColumnInfo> columns)
    {
        ResultSetCount++;
        exporter.BeginResultSet(columns);
    }

    public void RowsReceived(int index, IReadOnlyList<object?[]> rows)
    {
        exporter.WriteRows(rows);
        RowCount += rows.Count;
    }

    public void ResultSetCompleted(int index, long rowCount, bool truncated) => exporter.EndResultSet(rowCount, truncated);

    public void Message(ExecutionMessage message) => messages.Message(message);

    public void BatchCompleted(ExecutionUnit unit, TimeSpan elapsed, bool hadErrors) => messages.BatchCompleted(unit, elapsed, hadErrors);
}
