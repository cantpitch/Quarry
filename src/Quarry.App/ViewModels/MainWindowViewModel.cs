using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Quarry.App.Services;
using Quarry.Core.Connections;
using Quarry.Core.History;
using Quarry.Core.Results;

namespace Quarry.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IExplorerHost, IHistoryHost
{
    private readonly IDialogService _dialogs;

    public MainWindowViewModel(IDialogService dialogs, QueryHistoryStore? historyStore = null)
    {
        _dialogs = dialogs;
        History = new HistoryViewModel(historyStore ?? new QueryHistoryStore(), this);
    }

    public HistoryViewModel History { get; }

    /// <summary>0 = Object Explorer, 1 = History.</summary>
    [ObservableProperty]
    private int _selectedSidePanel;

    /// <summary>Connected servers; the roots of the object explorer.</summary>
    public ObservableCollection<ExplorerNode> ExplorerRoots { get; } = [];

    public ObservableCollection<ServerConnection> Servers { get; } = [];

    public ObservableCollection<QueryDocumentViewModel> Documents { get; } = [];

    public IReadOnlyList<OutputMode> OutputModes { get; } = Enum.GetValues<OutputMode>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDocument))]
    private QueryDocumentViewModel? _selectedDocument;

    [ObservableProperty]
    private ExplorerNode? _selectedNode;

    [ObservableProperty]
    private string _filter = "";

    public bool HasDocument => SelectedDocument is not null;

    partial void OnSelectedDocumentChanged(QueryDocumentViewModel? oldValue, QueryDocumentViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.IsActive = false;
        if (newValue is not null)
        {
            newValue.IsActive = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => newValue.Editor?.Focus(), Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    partial void OnFilterChanged(string value)
    {
        foreach (var root in ExplorerRoots)
            root.ApplyFilter(value);
    }

    // ---- Connections ----

    [RelayCommand]
    public async Task ConnectAsync()
    {
        var server = await _dialogs.ShowConnectDialogAsync();
        if (server is null)
            return;
        AddServer(server);

        // A fresh, empty, unconnected tab adopts the new connection.
        if (SelectedDocument is { Server: null } doc)
            await doc.SetServerAsync(server, null);
        else if (Documents.Count == 0)
            await NewQueryAsync(server, null);
    }

    private void AddServer(ServerConnection server)
    {
        Servers.Add(server);
        var node = new ServerNode(server, this) { IsExpanded = true };
        ExplorerRoots.Add(node);
        SelectedNode = node;
    }

    /// <summary>Called when a document needs a connection and has none.</summary>
    private async Task<ServerConnection?> RequestConnectionAsync()
    {
        if (SelectedServerForNewQuery() is { } existing)
            return existing;
        var server = await _dialogs.ShowConnectDialogAsync();
        if (server is not null)
            AddServer(server);
        return server;
    }

    /// <summary>The server of the selected explorer node, else the active tab's, else the only one.</summary>
    private ServerConnection? SelectedServerForNewQuery()
    {
        if (SelectedNodePath().OfType<ServerNode>().FirstOrDefault() is { } serverNode)
            return serverNode.Server;
        if (SelectedDocument?.Server is { } server)
            return server;
        return Servers.Count == 1 ? Servers[0] : null;
    }

    /// <summary>The nodes from the root down to the selected node.</summary>
    private List<ExplorerNode> SelectedNodePath()
    {
        var path = new List<ExplorerNode>();
        if (SelectedNode is not null)
        {
            foreach (var root in ExplorerRoots)
            {
                if (FindPath(root, SelectedNode, path))
                    break;
            }
        }
        return path;
    }

    private static bool FindPath(ExplorerNode node, ExplorerNode target, List<ExplorerNode> path)
    {
        path.Add(node);
        if (node == target || node.Children.Any(c => FindPath(c, target, path)))
            return true;
        path.RemoveAt(path.Count - 1);
        return false;
    }

    public void Disconnect(ServerNode node)
    {
        ExplorerRoots.Remove(node);
        Servers.Remove(node.Server);
        foreach (var doc in Documents.Where(d => d.Server == node.Server))
            _ = doc.SetServerAsync(null, null);
    }

    public async Task ChangeDocumentServerAsync(ServerConnection? server)
    {
        if (SelectedDocument is { } doc && server != doc.Server)
            await doc.SetServerAsync(server, null);
    }

    // ---- Documents ----

    [RelayCommand]
    public Task NewQueryAsync() => NewQueryAsync(SelectedServerForNewQuery(), DatabaseOfSelectedNode());

    private string? DatabaseOfSelectedNode()
    {
        var path = SelectedNodePath();
        if (path.OfType<DatabaseNode>().LastOrDefault() is { } db)
            return db.Database;
        if (path.Count == 0 && SelectedDocument is { } doc)
            return doc.Database;
        return null;
    }

    public async Task<QueryDocumentViewModel> NewQueryAsync(ServerConnection? server, string? database, string text = "")
    {
        var doc = new QueryDocumentViewModel(RequestConnectionAsync)
        {
            PickExportFile = _dialogs.PickExportFileAsync,
            ExecutionRecorded = History.RecordAsync,
        };
        if (text.Length > 0)
        {
            doc.Document.Text = text;
            doc.Document.UndoStack.ClearAll();
        }
        Documents.Add(doc);
        SelectedDocument = doc;
        await doc.SetServerAsync(server, database);
        return doc;
    }

    [RelayCommand]
    public async Task OpenFileAsync()
    {
        string? path = await _dialogs.PickOpenFileAsync();
        if (path is null)
            return;
        var existing = Documents.FirstOrDefault(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SelectedDocument = existing;
            return;
        }
        try
        {
            var doc = await NewQueryAsync(SelectedDocument?.Server ?? SelectedServerForNewQuery(), SelectedDocument?.Database);
            await doc.LoadFileAsync(path);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Open File", ex.Message);
        }
    }

    [RelayCommand]
    public Task SaveAsync() => SelectedDocument is { } doc ? SaveDocumentAsync(doc, saveAs: false) : Task.CompletedTask;

    [RelayCommand]
    public Task SaveAsAsync() => SelectedDocument is { } doc ? SaveDocumentAsync(doc, saveAs: true) : Task.CompletedTask;

    /// <returns>False when the user cancelled.</returns>
    public async Task<bool> SaveDocumentAsync(QueryDocumentViewModel doc, bool saveAs)
    {
        string? path = doc.FilePath;
        if (path is null || saveAs)
            path = await _dialogs.PickSaveFileAsync(doc.FileName);
        if (path is null)
            return false;
        try
        {
            await doc.SaveFileAsync(path);
            return true;
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Save File", ex.Message);
            return false;
        }
    }

    [RelayCommand]
    public async Task CloseDocumentAsync(QueryDocumentViewModel? doc)
    {
        doc ??= SelectedDocument;
        if (doc is null || !await ConfirmCloseAsync(doc))
            return;
        int index = Documents.IndexOf(doc);
        Documents.Remove(doc);
        if (Documents.Count > 0)
            SelectedDocument = Documents[Math.Clamp(index, 0, Documents.Count - 1)];
        await doc.DisposeAsync();
    }

    private async Task<bool> ConfirmCloseAsync(QueryDocumentViewModel doc)
    {
        if (!doc.IsDirty || (doc.FilePath is null && doc.Document.TextLength == 0))
            return true;
        SelectedDocument = doc;
        return await _dialogs.AskSaveChangesAsync(doc.FileName) switch
        {
            SaveChoice.Save => await SaveDocumentAsync(doc, saveAs: false),
            SaveChoice.Discard => true,
            _ => false,
        };
    }

    /// <summary>Asks about every unsaved document; false when the user cancelled closing the app.</summary>
    public async Task<bool> ConfirmExitAsync()
    {
        foreach (var doc in Documents.ToList())
        {
            if (!await ConfirmCloseAsync(doc))
                return false;
        }
        foreach (var doc in Documents)
            await doc.DisposeAsync();
        return true;
    }

    // ---- Execution (routed to the active document) ----

    [RelayCommand]
    public Task RunScriptAsync() => SelectedDocument?.RunAsync(RunMode.Script) ?? Task.CompletedTask;

    [RelayCommand]
    public Task RunQueryAsync() => SelectedDocument?.RunAsync(RunMode.Query) ?? Task.CompletedTask;

    [RelayCommand]
    public void Cancel() => SelectedDocument?.Cancel();

    [RelayCommand]
    public Task FormatAsync() => SelectedDocument?.FormatAsync() ?? Task.CompletedTask;

    [RelayCommand]
    public Task RunScriptToFileAsync() => SelectedDocument?.RunToFileAsync(RunMode.Script) ?? Task.CompletedTask;

    [RelayCommand]
    public Task RunQueryToFileAsync() => SelectedDocument?.RunToFileAsync(RunMode.Query) ?? Task.CompletedTask;

    [RelayCommand]
    public Task ExportResultsAsync() => SelectedDocument?.ExportResultsAsync() ?? Task.CompletedTask;

    [RelayCommand]
    public async Task ShowSettingsAsync() => await _dialogs.ShowSettingsAsync();

    // ---- IExplorerHost ----

    string IExplorerHost.Filter => Filter;

    public async Task OpenQueryAsync(ServerConnection server, string? database, string text, bool execute)
    {
        var doc = await NewQueryAsync(server, database, text);
        if (execute)
        {
            // Let the view attach its editor before running.
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Loaded);
            await doc.RunAsync(RunMode.Script);
        }
    }

    public Task CopyTextAsync(string text) => _dialogs.SetClipboardTextAsync(text);

    public void InsertIntoEditor(string text)
    {
        SelectedDocument?.Editor?.InsertAtCaret(text);
        SelectedDocument?.Editor?.Focus();
    }

    public Task ShowErrorAsync(string title, string message) => _dialogs.ShowErrorAsync(title, message);

    // ---- IHistoryHost ----

    /// <summary>
    /// Opens a history entry in a new tab. It reuses a connected server with the same server name;
    /// otherwise the tab opens unconnected (running it then asks for a connection).
    /// </summary>
    public async Task OpenHistoryAsync(QueryHistoryEntry entry, bool run)
    {
        var server = Servers.FirstOrDefault(s => s.Profile.Server.Equals(entry.Server, StringComparison.OrdinalIgnoreCase));
        var doc = await NewQueryAsync(server, server is null ? null : entry.Database, entry.Text);
        if (server is null)
            doc.StatusText = $"Not connected to {entry.ServerDisplayName}. Connect to run this query.";
        if (entry.TextTruncated)
            doc.StatusText = "This history entry was too long to store in full; only the first part is shown.";
        if (run && !entry.TextTruncated)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Loaded);
            await doc.RunAsync(RunMode.Script);
        }
    }

    public Task<bool> ConfirmAsync(string title, string message) => _dialogs.ConfirmAsync(title, message);
}
