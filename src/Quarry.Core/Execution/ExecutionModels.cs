using Quarry.Core.Results;
using Quarry.Parsing;

namespace Quarry.Core.Execution;

public enum MessageKind
{
    Info,
    Error,
    RowsAffected,
    Status,
}

/// <summary>A line for the Messages pane.</summary>
/// <param name="DocumentLine">Zero-based editor line the message refers to, when known.</param>
public sealed record ExecutionMessage(MessageKind Kind, string Text, int? DocumentLine = null, int? Number = null, byte? Severity = null)
{
    public DateTimeOffset Timestamp { get; } = DateTimeOffset.Now;
}

public sealed record ExecutionOptions
{
    /// <summary>Seconds before a command times out; 0 waits indefinitely.</summary>
    public int CommandTimeoutSeconds { get; init; }

    /// <summary>Maximum rows kept per result set; 0 keeps all.</summary>
    public int MaxRowsPerResultSet { get; init; }

    /// <summary>Stop running later batches after a batch reports an error.</summary>
    public bool StopOnError { get; init; }

    /// <summary>Rows delivered to the sink per call.</summary>
    public int ChunkSize { get; init; } = 1000;
}

public sealed record ExecutionSummary(bool Succeeded, bool Cancelled, TimeSpan Elapsed, int ResultSetCount, long RowCount);

/// <summary>
/// Receives execution progress. Calls arrive on a background thread, in order, never concurrently.
/// </summary>
public interface IExecutionSink
{
    void BatchStarted(ExecutionUnit unit, int iteration);

    /// <summary>A result set's columns are known; rows follow via <see cref="RowsReceived"/>.</summary>
    void ResultSetStarted(int index, IReadOnlyList<ColumnInfo> columns);

    /// <summary>A chunk of rows for the current result set. The list is not reused by the executor.</summary>
    void RowsReceived(int index, IReadOnlyList<object?[]> rows);

    void ResultSetCompleted(int index, long rowCount, bool truncated);

    void Message(ExecutionMessage message);

    void BatchCompleted(ExecutionUnit unit, TimeSpan elapsed, bool hadErrors);
}
