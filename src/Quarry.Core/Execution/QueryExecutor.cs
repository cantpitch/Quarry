using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Quarry.Core.Results;
using Quarry.Parsing;

namespace Quarry.Core.Execution;

/// <summary>
/// Runs execution units on an open connection. Each unit is sent as one batch; PRINT output,
/// errors and row counts are reported in the order the server produces them.
/// </summary>
public static class QueryExecutor
{
    public static async Task<ExecutionSummary> ExecuteAsync(
        SqlConnection connection,
        IReadOnlyList<ExecutionUnit> units,
        IExecutionSink sink,
        ExecutionOptions options,
        CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();
        ExecutionUnit? current = null;
        bool batchHadError = false;
        bool anyError = false;
        int resultSetIndex = 0;
        long rowTotal = 0;

        void OnInfoMessage(object? sender, SqlInfoMessageEventArgs e)
        {
            foreach (SqlError error in e.Errors)
            {
                bool isError = error.Class > 10;
                batchHadError |= isError;
                sink.Message(ToMessage(error, current, isError));
            }
        }

        bool previousFire = connection.FireInfoMessageEventOnUserErrors;
        connection.FireInfoMessageEventOnUserErrors = true;
        connection.InfoMessage += OnInfoMessage;
        try
        {
            foreach (var unit in units)
            {
                for (int iteration = 1; iteration <= unit.RepeatCount; iteration++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    current = unit;
                    batchHadError = false;
                    sink.BatchStarted(unit, iteration);
                    var watch = Stopwatch.StartNew();

                    try
                    {
                        (int sets, long rows) = await ExecuteBatchAsync(connection, unit, sink, options, resultSetIndex, cancellationToken);
                        resultSetIndex += sets;
                        rowTotal += rows;
                    }
                    catch (SqlException ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        batchHadError = true;
                        foreach (SqlError error in ex.Errors)
                            sink.Message(ToMessage(error, unit, isError: true));
                    }

                    sink.BatchCompleted(unit, watch.Elapsed, batchHadError);
                    anyError |= batchHadError;

                    if (connection.State != ConnectionState.Open)
                    {
                        sink.Message(new ExecutionMessage(MessageKind.Error, "The connection was closed."));
                        return new ExecutionSummary(false, false, total.Elapsed, resultSetIndex, rowTotal);
                    }
                    if (batchHadError && options.StopOnError)
                    {
                        sink.Message(new ExecutionMessage(MessageKind.Status, "Execution stopped after an error."));
                        return new ExecutionSummary(false, false, total.Elapsed, resultSetIndex, rowTotal);
                    }
                }
            }

            return new ExecutionSummary(!anyError, false, total.Elapsed, resultSetIndex, rowTotal);
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is OperationCanceledException or SqlException)
        {
            sink.Message(new ExecutionMessage(MessageKind.Status, "Query was cancelled by user."));
            return new ExecutionSummary(false, true, total.Elapsed, resultSetIndex, rowTotal);
        }
        finally
        {
            connection.InfoMessage -= OnInfoMessage;
            connection.FireInfoMessageEventOnUserErrors = previousFire;
        }
    }

    private static async Task<(int ResultSets, long Rows)> ExecuteBatchAsync(
        SqlConnection connection,
        ExecutionUnit unit,
        IExecutionSink sink,
        ExecutionOptions options,
        int firstResultSetIndex,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(unit.Text, connection)
        {
            CommandType = CommandType.Text,
            CommandTimeout = options.CommandTimeoutSeconds,
        };
        command.StatementCompleted += (_, e) =>
            sink.Message(new ExecutionMessage(MessageKind.RowsAffected,
                string.Create(CultureInfo.InvariantCulture, $"({e.RecordCount} row{(e.RecordCount == 1 ? "" : "s")} affected)")));

        await using var registration = cancellationToken.Register(static state => ((SqlCommand)state!).Cancel(), command);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.Default, cancellationToken);

        int sets = 0;
        long rows = 0;
        do
        {
            if (reader.FieldCount == 0)
                continue;

            int index = firstResultSetIndex + sets++;
            var columns = GetColumns(reader);
            sink.ResultSetStarted(index, columns);

            long count = 0;
            bool truncated = false;
            var chunk = new List<object?[]>(options.ChunkSize);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (options.MaxRowsPerResultSet > 0 && count >= options.MaxRowsPerResultSet)
                {
                    truncated = true;
                    break; // NextResult skips the remaining rows.
                }
                chunk.Add(ReadRow(reader));
                count++;
                if (chunk.Count >= options.ChunkSize)
                {
                    sink.RowsReceived(index, chunk);
                    chunk = new List<object?[]>(options.ChunkSize);
                }
            }
            if (chunk.Count > 0)
                sink.RowsReceived(index, chunk);
            sink.ResultSetCompleted(index, count, truncated);
            rows += count;
        }
        while (await reader.NextResultAsync(cancellationToken));

        return (sets, rows);
    }

    internal static IReadOnlyList<ColumnInfo> GetColumns(DbDataReader reader)
    {
        var schema = reader.GetColumnSchema();
        var columns = new ColumnInfo[schema.Count];
        for (int i = 0; i < schema.Count; i++)
        {
            var c = schema[i];
            columns[i] = new ColumnInfo(
                c.ColumnName ?? "",
                c.DataTypeName ?? reader.GetDataTypeName(i),
                c.DataType ?? typeof(object),
                c.AllowDBNull ?? true,
                c.ColumnSize,
                c.NumericPrecision,
                c.NumericScale is { } scale and < 255 ? scale : null);
        }
        return columns;
    }

    internal static object?[] ReadRow(SqlDataReader reader)
    {
        var values = new object?[reader.FieldCount];
        try
        {
            reader.GetValues(values!);
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] is DBNull)
                    values[i] = null;
            }
        }
        catch (Exception)
        {
            // A value .NET cannot represent (decimal(38) overflow, CLR UDTs such as geography
            // without Microsoft.SqlServer.Types): read column by column with fallbacks.
            for (int i = 0; i < values.Length; i++)
                values[i] = ReadValueSafely(reader, i);
        }
        return values;
    }

    private static object? ReadValueSafely(SqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;
        try
        {
            return reader.GetValue(ordinal);
        }
        catch (OverflowException)
        {
            return reader.GetSqlDecimal(ordinal).ToString();
        }
        catch (Exception)
        {
            try
            {
                return reader.GetSqlBytes(ordinal).Value;
            }
            catch (Exception)
            {
                return $"<unsupported {reader.GetDataTypeName(ordinal)} value>";
            }
        }
    }

    private static ExecutionMessage ToMessage(SqlError error, ExecutionUnit? unit, bool isError)
    {
        if (!isError)
            return new ExecutionMessage(MessageKind.Info, error.Message, Number: error.Number, Severity: error.Class);

        // Inside a module the line number is relative to the module, not to our batch.
        bool inBatch = string.IsNullOrEmpty(error.Procedure);
        int? line = unit is not null && inBatch && error.LineNumber > 0 ? unit.MapServerLine(error.LineNumber) : null;
        string location = line is { } l
            ? string.Create(CultureInfo.InvariantCulture, $", Line {l + 1}")
            : error.LineNumber > 0 ? string.Create(CultureInfo.InvariantCulture, $", Line {error.LineNumber}") : "";
        string procedure = string.IsNullOrEmpty(error.Procedure) ? "" : $", Procedure {error.Procedure}";
        string header = string.Create(CultureInfo.InvariantCulture,
            $"Msg {error.Number}, Level {error.Class}, State {error.State}{procedure}{location}");
        return new ExecutionMessage(MessageKind.Error, $"{header}\n{error.Message}", line, error.Number, error.Class);
    }

    /// <summary>Returns the connection's current database (it changes after USE).</summary>
    public static async Task<string?> GetCurrentDatabaseAsync(SqlConnection connection, CancellationToken cancellationToken = default)
    {
        await using var command = new SqlCommand("SELECT DB_NAME()", connection);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }
}
