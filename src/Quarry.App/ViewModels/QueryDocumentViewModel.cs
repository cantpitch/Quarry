using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.SqlClient;
using Quarry.App.Editor;
using Quarry.App.Services;
using Quarry.Core.Connections;
using Quarry.Core.Execution;
using Quarry.Core.Export;
using Quarry.Core.History;
using Quarry.Core.Results;
using Quarry.Parsing;
using Quarry.Parsing.Formatting;

namespace Quarry.App.ViewModels;

/// <summary>What the document needs from its editor view.</summary>
public interface IEditorAccessor
{
    int CaretOffset { get; }

    TextSpan Selection { get; }

    ScriptModel GetModel();

    void GoToLine(int zeroBasedLine);

    void InsertAtCaret(string text);

    void SetCaret(int offset);

    void Select(int start, int length);

    void Focus();
}

public enum RunMode
{
    Script,
    Query,
}

/// <summary>One query tab: the editor text, its own connection, and the last execution's output.</summary>
public sealed partial class QueryDocumentViewModel : ObservableObject, ICompletionContext, IAsyncDisposable
{
    private static int _untitledCounter;

    private readonly Func<Task<ServerConnection?>> _requestConnection;
    private readonly DispatcherTimer _elapsedTimer;
    private readonly Stopwatch _stopwatch = new();
    private SqlConnection? _connection;
    private CancellationTokenSource? _cts;
    private bool _suppressDatabaseChange;
    private int _textRenderVersion;

    public QueryDocumentViewModel(Func<Task<ServerConnection?>> requestConnection)
    {
        _requestConnection = requestConnection;
        _untitledName = $"SQLQuery{Interlocked.Increment(ref _untitledCounter)}.sql";
        _outputMode = AppServices.Settings.DefaultOutputMode;
        Document.UndoStack.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UndoStack.IsOriginalFile))
                OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(Title));
        };
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _elapsedTimer.Tick += (_, _) => ElapsedText = FormatElapsed(_stopwatch.Elapsed);
        Results.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CanExport));
    }

    public TextDocument Document { get; } = new();

    public IEditorAccessor? Editor { get; set; }

    private readonly string _untitledName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(FileName))]
    private string? _filePath;

    public string FileName => FilePath is null ? _untitledName : Path.GetFileName(FilePath);

    public bool IsDirty => !Document.UndoStack.IsOriginalFile;

    public string Title => IsDirty ? FileName + " •" : FileName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionText))]
    private ServerConnection? _server;

    private string? _database;

    /// <summary>
    /// The tab's current database. Null/empty assignments are ignored: a ComboBox pushes null
    /// back while its item list is being refreshed, which must not lose the real value.
    /// </summary>
    public string? Database
    {
        get => _database;
        set
        {
            if (string.IsNullOrEmpty(value) || value == _database)
                return;
            _database = value;
            OnPropertyChanged();
            if (!_suppressDatabaseChange)
                _ = ChangeDatabaseAsync(value);
        }
    }

    public ObservableCollection<string> Databases { get; } = [];

    public string ConnectionText => Server is null ? "Not connected" : Server.DisplayName;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isExecuting;

    [ObservableProperty]
    private OutputMode _outputMode;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string _elapsedText = "";

    [ObservableProperty]
    private string _rowCountText = "";

    [ObservableProperty]
    private string _textOutput = "";

    /// <summary>0 = Results, 1 = Messages.</summary>
    [ObservableProperty]
    private int _selectedOutputTab;

    [ObservableProperty]
    private bool _hasOutput;

    /// <summary>True for the tab currently shown.</summary>
    [ObservableProperty]
    private bool _isActive;

    public bool IsGridMode => OutputMode == OutputMode.Grid;

    public ObservableCollection<ResultSetViewModel> Results { get; } = [];

    public ObservableCollection<MessageViewModel> Messages { get; } = [];

    partial void OnOutputModeChanged(OutputMode value)
    {
        OnPropertyChanged(nameof(IsGridMode));
        _ = RenderTextOutputAsync();
    }

    // ---- Connection ----

    public async Task SetServerAsync(ServerConnection? server, string? database)
    {
        await CloseConnectionAsync();
        Server = server;
        string target = database ?? server?.Profile.Database ?? "master";
        _database = null;
        Databases.Clear();
        OnPropertyChanged(nameof(Database));
        if (server is null)
            return;
        try
        {
            foreach (var db in await server.Metadata.GetDatabasesAsync())
                Databases.Add(db);
        }
        catch (Exception ex)
        {
            AddMessage(new ExecutionMessage(MessageKind.Error, $"Could not list databases: {ex.Message}"));
        }
        if (!Databases.Contains(target, StringComparer.OrdinalIgnoreCase))
            Databases.Add(target);
        SetDatabaseSilently(Databases.First(d => d.Equals(target, StringComparison.OrdinalIgnoreCase)));
    }

    private void SetDatabaseSilently(string database)
    {
        _suppressDatabaseChange = true;
        try
        {
            Database = database;
        }
        finally
        {
            _suppressDatabaseChange = false;
        }
    }

    private async Task ChangeDatabaseAsync(string database)
    {
        if (_connection is not { State: System.Data.ConnectionState.Open } || IsExecuting)
            return; // applied when the connection is next opened
        try
        {
            await _connection.ChangeDatabaseAsync(database);
        }
        catch (Exception ex)
        {
            AddMessage(new ExecutionMessage(MessageKind.Error, ex.Message));
            SelectedOutputTab = 1;
            HasOutput = true;
        }
    }

    private async Task<SqlConnection> EnsureConnectionAsync(ServerConnection server, CancellationToken ct)
    {
        if (_connection is { State: System.Data.ConnectionState.Open })
            return _connection;
        await CloseConnectionAsync();
        var connection = server.CreateConnection(Database);
        await connection.OpenAsync(ct);
        _connection = connection;
        return connection;
    }

    private async Task CloseConnectionAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    // ---- Execution ----

    [RelayCommand]
    public Task RunScriptAsync() => RunAsync(RunMode.Script);

    [RelayCommand]
    public Task RunQueryAsync() => RunAsync(RunMode.Query);

    [RelayCommand(CanExecute = nameof(IsExecuting))]
    public void Cancel() => _cts?.Cancel();

    public async Task RunAsync(RunMode mode)
    {
        if (IsExecuting || Editor is null || GetUnits(mode) is not { } units || !await EnsureServerAsync())
            return;
        await ExecuteAsync(Server!, units, KindOf(mode), exportPath: null);
    }

    /// <summary>
    /// Runs the script or query and streams every result set straight to a file instead of the grid.
    /// No row limit applies and rows are never held in memory.
    /// </summary>
    public async Task RunToFileAsync(RunMode mode, string? path = null)
    {
        if (IsExecuting || Editor is null || GetUnits(mode) is not { } units)
            return;
        path ??= await PickExportPathAsync();
        if (path is null || !await EnsureServerAsync())
            return;
        await ExecuteAsync(Server!, units, KindOf(mode), path);
    }

    private HistoryRunKind KindOf(RunMode mode)
        => Editor?.Selection.IsEmpty == false ? HistoryRunKind.Selection
            : mode == RunMode.Script ? HistoryRunKind.Script : HistoryRunKind.Query;

    /// <summary>What Run Script / Run Query would send, or null (with a status message) when nothing.</summary>
    private IReadOnlyList<ExecutionUnit>? GetUnits(RunMode mode)
    {
        var model = Editor!.GetModel();
        var options = new AnalyzerOptions { BatchSeparator = AppServices.Settings.BatchSeparator };
        var selection = Editor.Selection;
        IReadOnlyList<ExecutionUnit> units;
        if (!selection.IsEmpty)
            units = ExecutionPlanner.ForSelection(model, selection, options);
        else if (mode == RunMode.Script)
            units = ExecutionPlanner.ForScript(model);
        else
            units = ExecutionPlanner.ForQuery(model, Editor.CaretOffset) is { } unit ? [unit] : [];

        if (units.Count > 0)
            return units;
        StatusText = mode == RunMode.Query ? "No statement at the cursor." : "Nothing to run.";
        return null;
    }

    private async Task<bool> EnsureServerAsync()
    {
        if (Server is not null)
            return true;
        var server = await _requestConnection();
        if (server is null)
            return false;
        await SetServerAsync(server, null);
        return true;
    }

    /// <param name="exportPath">When set, result sets are streamed to this file instead of the grid.</param>
    private async Task ExecuteAsync(ServerConnection server, IReadOnlyList<ExecutionUnit> units, HistoryRunKind kind, string? exportPath)
    {
        Results.Clear();
        Messages.Clear();
        TextOutput = "";
        RowCountText = "";
        HasOutput = true;
        SelectedOutputTab = 0;
        IsExecuting = true;
        StatusText = exportPath is null ? "Executing…" : $"Executing to {Path.GetFileName(exportPath)}…";
        string? startDatabase = Database;
        _cts = new CancellationTokenSource();
        _stopwatch.Restart();
        _elapsedTimer.Start();
        var settings = AppServices.Settings;
        var options = new ExecutionOptions
        {
            CommandTimeoutSeconds = settings.CommandTimeoutSeconds,
            MaxRowsPerResultSet = exportPath is null ? settings.MaxRowsPerResultSet : 0,
            StopOnError = settings.StopOnError,
        };

        ExecutionSummary? summary = null;
        ExportingSink? exportSink = null;
        IResultExporter? exporter = null;
        try
        {
            var connection = await EnsureConnectionAsync(server, _cts.Token);
            var uiSink = new UiExecutionSink(this);
            IExecutionSink sink = uiSink;
            if (exportPath is not null)
            {
                exporter = ResultExporter.Create(ResultExporter.FormatFromPath(exportPath) ?? ExportFormat.Csv, exportPath, settings.ExportOptions);
                sink = exportSink = new ExportingSink(exporter, uiSink);
            }
            summary = await Task.Run(async () =>
            {
                var result = await QueryExecutor.ExecuteAsync(connection, units, sink, options, _cts.Token);
                exporter?.Complete();
                return result;
            });
            await uiSink.FlushAsync();

            if (connection.State == System.Data.ConnectionState.Open)
            {
                string? current = await QueryExecutor.GetCurrentDatabaseAsync(connection);
                if (current is not null)
                {
                    if (!Databases.Contains(current))
                        Databases.Add(current);
                    SetDatabaseSilently(current);
                }
            }
            else
            {
                await CloseConnectionAsync();
            }
        }
        catch (OperationCanceledException)
        {
            AddMessage(new ExecutionMessage(MessageKind.Status, "Query was cancelled by user."));
        }
        catch (Exception ex)
        {
            AddMessage(new ExecutionMessage(MessageKind.Error, ex.Message));
            await CloseConnectionAsync();
        }
        finally
        {
            try
            {
                exporter?.Dispose(); // closes files even after an error or cancel
            }
            catch (Exception ex)
            {
                AddMessage(new ExecutionMessage(MessageKind.Error, $"Could not finish writing the file: {ex.Message}"));
            }
            _stopwatch.Stop();
            _elapsedTimer.Stop();
            ElapsedText = FormatElapsed(_stopwatch.Elapsed);
            IsExecuting = false;
            _cts.Dispose();
            _cts = null;
        }

        long rows = exportSink?.RowCount ?? Results.Sum(r => (long)r.Model.Rows.Count);
        RowCountText = rows == 1 ? "1 row" : string.Create(CultureInfo.CurrentCulture, $"{rows:N0} rows");
        StatusText = summary switch
        {
            { Cancelled: true } => "Query cancelled.",
            { Succeeded: true } => "Query completed successfully.",
            _ => "Query completed with errors.",
        };

        if (exporter is not null && exportSink is not null)
        {
            bool partial = summary is not { Cancelled: false }; // cancelled or failed part-way
            AddMessage(new ExecutionMessage(MessageKind.Status, DescribeExport(exportSink.ResultSetCount, rows, exporter.Files, partial)));
            if (exporter.Files.Count > 0)
                StatusText += $" Results written to {Path.GetFileName(exporter.Files[0])}{(exporter.Files.Count > 1 ? $" (+{exporter.Files.Count - 1})" : "")}.";
        }

        AddMessage(new ExecutionMessage(MessageKind.Status,
            string.Create(CultureInfo.CurrentCulture, $"Completion time: {DateTimeOffset.Now:O}")));

        if (ExecutionRecorded is { } record)
        {
            await record(new QueryHistoryEntry
            {
                Timestamp = DateTimeOffset.Now - _stopwatch.Elapsed,
                Server = server.Profile.Server,
                ServerDisplayName = server.Profile.DisplayName,
                Database = startDatabase,
                Text = QueryHistoryEntry.ScriptFromUnits(units, AppServices.Settings.BatchSeparator),
                Kind = kind,
                Outcome = summary switch
                {
                    { Cancelled: true } => HistoryOutcome.Cancelled,
                    { Succeeded: true } => HistoryOutcome.Succeeded,
                    _ => HistoryOutcome.Failed,
                },
                DurationMs = (long)_stopwatch.Elapsed.TotalMilliseconds,
                RowCount = rows,
                ResultSetCount = exportSink?.ResultSetCount ?? Results.Count,
                ExportPath = exporter?.Files.FirstOrDefault(),
            });
        }

        if (Results.Count == 0)
            SelectedOutputTab = 1;
        await RenderTextOutputAsync();
    }

    private static string DescribeExport(int resultSets, long rows, IReadOnlyList<string> files, bool partial)
    {
        if (files.Count == 0)
            return "No result sets were returned, so no file was written.";
        var sb = new StringBuilder();
        sb.Append(CultureInfo.CurrentCulture, $"{(partial ? "Partial output: wrote" : "Wrote")} {rows:N0} row{(rows == 1 ? "" : "s")} ");
        sb.Append(CultureInfo.CurrentCulture, $"from {resultSets} result set{(resultSets == 1 ? "" : "s")} to:");
        foreach (var file in files)
            sb.Append('\n').Append(file);
        return sb.ToString();
    }

    // ---- Formatting ----

    /// <summary>
    /// Formats the selection, or the whole document when nothing is selected, as one undoable edit.
    /// Batches with syntax errors are left as they are.
    /// </summary>
    public async Task FormatAsync()
    {
        if (Editor is null)
            return;
        var selection = Editor.Selection;
        bool whole = selection.IsEmpty;
        int start = whole ? 0 : selection.Start;
        string input = whole ? Document.Text : Document.GetText(selection.Start, selection.Length);
        if (string.IsNullOrWhiteSpace(input))
            return;

        int caret = Editor.CaretOffset;
        var options = AppServices.Settings.EffectiveFormatting;
        SqlFormatResult result;
        try
        {
            result = await Task.Run(() => SqlFormatter.Format(input, options));
        }
        catch (Exception ex)
        {
            StatusText = $"Formatting failed: {ex.Message}";
            return;
        }

        // The document may have changed while formatting ran in the background.
        string current = whole ? Document.Text : start + input.Length <= Document.TextLength ? Document.GetText(start, input.Length) : "";
        if (current != input)
        {
            StatusText = "The text changed while formatting; nothing was replaced.";
            return;
        }

        if (result.Changed)
        {
            Document.Replace(start, input.Length, result.Text);
            if (whole)
                Editor.SetCaret(MapOffset(input, result.Text, caret));
            else
                Editor.Select(start, result.Text.Length);
        }

        StatusText = result.SkippedBatches switch
        {
            0 => result.Changed ? "Formatted." : "Already formatted.",
            _ => $"Formatted, except: {result.Problems[0]}"
                 + (result.Problems.Count > 1 ? $" (and {result.Problems.Count - 1} more)" : ""),
        };
    }

    /// <summary>
    /// Maps a caret offset across a whitespace/case-only change by counting the non-whitespace
    /// characters before it.
    /// </summary>
    internal static int MapOffset(string before, string after, int offset)
    {
        int significant = 0;
        for (int i = 0; i < Math.Min(offset, before.Length); i++)
        {
            if (!char.IsWhiteSpace(before[i]))
                significant++;
        }
        for (int i = 0; i < after.Length; i++)
        {
            if (significant == 0)
                return i;
            if (!char.IsWhiteSpace(after[i]))
                significant--;
        }
        return after.Length;
    }

    // ---- Export ----

    /// <summary>Called after every execution so it can be added to the query history.</summary>
    public Func<QueryHistoryEntry, Task>? ExecutionRecorded { get; set; }

    /// <summary>Supplied by the window: shows a save dialog for an export file.</summary>
    public Func<string, Task<string?>>? PickExportFile { get; set; }

    private Task<string?> PickExportPathAsync()
        => PickExportFile?.Invoke(Path.GetFileNameWithoutExtension(FileName)) ?? Task.FromResult<string?>(null);

    public bool CanExport => Results.Count > 0 && !IsExecuting && !IsExporting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    private bool _isExporting;

    partial void OnIsExecutingChanged(bool value) => OnPropertyChanged(nameof(CanExport));

    /// <summary>Exports all loaded result sets (or just <paramref name="only"/>) to a file.</summary>
    public async Task ExportResultsAsync(string? path = null, ResultSetViewModel? only = null)
    {
        if (!CanExport)
            return;
        path ??= await PickExportPathAsync();
        if (path is null)
            return;

        var sets = only is not null ? [only.Model] : Results.Select(r => r.Model).ToList();
        var format = ResultExporter.FormatFromPath(path) ?? ExportFormat.Csv;
        IsExporting = true;
        StatusText = $"Exporting to {Path.GetFileName(path)}…";
        try
        {
            var options = AppServices.Settings.ExportOptions;
            var files = await Task.Run(() => ResultExporter.Export(sets, format, path, options));
            long rows = sets.Sum(s => (long)s.Rows.Count);
            AddMessage(new ExecutionMessage(MessageKind.Status, DescribeExport(sets.Count, rows, files, partial: false)));
            if (sets.Any(s => s.IsTruncated))
            {
                AddMessage(new ExecutionMessage(MessageKind.Error,
                    "Some result sets were cut off by the row limit, so the file is incomplete. Use Run Script to File for the full results."));
            }
            StatusText = $"Exported to {Path.GetFileName(files.FirstOrDefault() ?? path)}{(files.Count > 1 ? $" (+{files.Count - 1})" : "")}.";
        }
        catch (Exception ex)
        {
            AddMessage(new ExecutionMessage(MessageKind.Error, $"Export failed: {ex.Message}"));
            StatusText = "Export failed.";
            SelectedOutputTab = 1;
        }
        finally
        {
            IsExporting = false;
        }
    }

    internal void AddMessage(ExecutionMessage message)
        => Messages.Add(new MessageViewModel(message.Kind, message.Text, message.DocumentLine));

    private async Task RenderTextOutputAsync()
    {
        int version = ++_textRenderVersion;
        if (OutputMode == OutputMode.Grid || Results.Count == 0)
        {
            TextOutput = "";
            return;
        }
        var sets = Results.Select(r => r.Model).ToList();
        var mode = OutputMode;
        string text = await Task.Run(() => ResultFormatting.ToString(sets, mode));
        if (version == _textRenderVersion)
            TextOutput = text;
    }

    [RelayCommand]
    private void NavigateToMessage(MessageViewModel? message)
    {
        if (message?.DocumentLine is { } line)
            Editor?.GoToLine(line);
    }

    private static string FormatElapsed(TimeSpan elapsed)
        => elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"mm\:ss\.f");

    // ---- Files ----

    public async Task LoadFileAsync(string path)
    {
        string text;
        using (var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            text = await reader.ReadToEndAsync();
        Document.Text = text;
        Document.UndoStack.ClearAll();
        Document.UndoStack.MarkAsOriginalFile();
        FilePath = path;
    }

    public async Task SaveFileAsync(string path)
    {
        await File.WriteAllTextAsync(path, Document.Text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        FilePath = path;
        Document.UndoStack.MarkAsOriginalFile();
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(Title));
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        await CloseConnectionAsync();
    }

    /// <summary>Marshals executor callbacks onto the UI thread, preserving order.</summary>
    private sealed class UiExecutionSink(QueryDocumentViewModel owner) : IExecutionSink
    {
        public void BatchStarted(ExecutionUnit unit, int iteration) { }

        public void ResultSetStarted(int index, IReadOnlyList<ColumnInfo> columns)
            => Dispatcher.UIThread.Post(() => owner.Results.Add(new ResultSetViewModel(index, columns)), DispatcherPriority.Background);

        public void RowsReceived(int index, IReadOnlyList<object?[]> rows)
            => Dispatcher.UIThread.Post(() =>
            {
                var rs = owner.Results.FirstOrDefault(r => r.Index == index);
                rs?.AddRows(rows);
                owner.RowCountText = string.Create(CultureInfo.CurrentCulture, $"{owner.Results.Sum(r => (long)r.Model.Rows.Count):N0} rows");
            }, DispatcherPriority.Background);

        public void ResultSetCompleted(int index, long rowCount, bool truncated)
            => Dispatcher.UIThread.Post(() =>
            {
                if (owner.Results.FirstOrDefault(r => r.Index == index) is { } rs)
                    rs.Model.IsTruncated = truncated;
                if (truncated)
                    owner.AddMessage(new ExecutionMessage(MessageKind.Status,
                        $"Result set {index + 1} was truncated at {AppServices.Settings.MaxRowsPerResultSet:N0} rows (see Settings)."));
            }, DispatcherPriority.Background);

        public void Message(ExecutionMessage message)
            => Dispatcher.UIThread.Post(() => owner.AddMessage(message), DispatcherPriority.Background);

        public void BatchCompleted(ExecutionUnit unit, TimeSpan elapsed, bool hadErrors) { }

        /// <summary>Waits until everything posted so far has been applied.</summary>
        public Task FlushAsync() => Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background).GetTask();
    }
}
