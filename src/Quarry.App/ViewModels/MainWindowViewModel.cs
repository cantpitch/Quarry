using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Quarry.App.Services;
using Quarry.Core.Connections;
using Quarry.Core.History;
using Quarry.Core.Results;
using Quarry.Core.Session;

namespace Quarry.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IExplorerHost, IHistoryHost
{
    private readonly IDialogService _dialogs;
    private readonly SessionStore _session;
    private readonly ProfileStore _profiles;
    private readonly DispatcherTimer _sessionTimer;

    /// <summary>The <see cref="QueryDocumentViewModel.TextVersion"/> each tab's backup was written at.</summary>
    private readonly Dictionary<Guid, int> _backupVersions = [];

    private bool _restoring;

    public MainWindowViewModel(IDialogService dialogs, QueryHistoryStore? historyStore = null, SessionStore? sessionStore = null, ProfileStore? profiles = null)
    {
        _dialogs = dialogs;
        _session = sessionStore ?? new SessionStore();
        _profiles = profiles ?? AppServices.Profiles;
        History = new HistoryViewModel(historyStore ?? new QueryHistoryStore(), this);

        // Saves the open tabs a moment after a change, so they survive a crash as well as a normal exit.
        _sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _sessionTimer.Tick += (_, _) =>
        {
            _sessionTimer.Stop();
            SaveSession();
        };
        Documents.CollectionChanged += OnDocumentsChanged;
        Servers.CollectionChanged += (_, _) => ScheduleSessionSave();
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
            Dispatcher.UIThread.Post(() => newValue.Editor?.Focus(), DispatcherPriority.Loaded);
        }
        ScheduleSessionSave();
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

    private void AddServer(ServerConnection server, bool select = true)
    {
        Servers.Add(server);
        var node = new ServerNode(server, this) { IsExpanded = true };
        ExplorerRoots.Add(node);
        if (select)
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
        var doc = CreateDocument();
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

    private QueryDocumentViewModel CreateDocument(Guid? sessionId = null, string? untitledName = null) => new(RequestConnectionAsync, sessionId, untitledName)
    {
        PickExportFile = _dialogs.PickExportFileAsync,
        ExecutionRecorded = History.RecordAsync,
    };

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

    /// <summary>
    /// Saves the open tabs for the next start. Only when that fails does it ask about every unsaved
    /// document; false when the user cancelled closing the app.
    /// </summary>
    public async Task<bool> ConfirmExitAsync()
    {
        _sessionTimer.Stop();
        if (!SaveSession())
        {
            foreach (var doc in Documents.ToList())
            {
                if (!await ConfirmCloseAsync(doc))
                    return false;
            }
        }
        foreach (var doc in Documents)
            await doc.DisposeAsync();
        return true;
    }

    // ---- Session ----

    private void OnDocumentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var doc in e.OldItems?.OfType<QueryDocumentViewModel>() ?? [])
        {
            doc.PropertyChanged -= OnDocumentPropertyChanged;
            doc.Document.TextChanged -= OnDocumentTextChanged;
        }
        foreach (var doc in e.NewItems?.OfType<QueryDocumentViewModel>() ?? [])
        {
            doc.PropertyChanged += OnDocumentPropertyChanged;
            doc.Document.TextChanged += OnDocumentTextChanged;
        }
        ScheduleSessionSave();
    }

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(QueryDocumentViewModel.FilePath) or nameof(QueryDocumentViewModel.Database)
            or nameof(QueryDocumentViewModel.Server) or nameof(QueryDocumentViewModel.OutputMode) or nameof(QueryDocumentViewModel.IsDirty))
            ScheduleSessionSave();
    }

    private void OnDocumentTextChanged(object? sender, EventArgs e) => ScheduleSessionSave();

    private void ScheduleSessionSave()
    {
        if (!_restoring && !_sessionTimer.IsEnabled)
            _sessionTimer.Start();
    }

    /// <summary>
    /// Writes the open tabs to the session store: saved files by path, and the text of untitled or
    /// modified tabs to backups. False when it could not be written.
    /// </summary>
    public bool SaveSession()
    {
        var backups = new Dictionary<Guid, string>();
        var documents = new List<SessionDocument>();
        foreach (var doc in Documents)
        {
            bool needsBackup = doc.FilePath is null ? doc.Document.TextLength > 0 : doc.IsDirty;
            if (needsBackup && (!_backupVersions.TryGetValue(doc.SessionId, out int version) || version != doc.TextVersion))
                backups[doc.SessionId] = doc.Document.Text;
            var remembered = doc.Server is null ? doc.RememberedConnection : null;
            documents.Add(new SessionDocument
            {
                Id = doc.SessionId,
                FilePath = doc.FilePath,
                UntitledName = doc.FilePath is null ? doc.UntitledName : null,
                HasBackup = needsBackup,
                ProfileId = doc.Server?.Profile.Id ?? remembered?.ProfileId,
                ServerDisplayName = doc.Server?.Profile.DisplayName ?? remembered?.ServerDisplayName,
                Database = doc.Server is not null ? doc.Database : remembered?.Database,
                CaretOffset = doc.CaretOffset,
                OutputMode = doc.OutputMode,
            });
        }
        var state = new SessionState
        {
            Documents = documents,
            SelectedIndex = SelectedDocument is null ? 0 : Math.Max(0, Documents.IndexOf(SelectedDocument)),
            Servers = Servers.Select(s => s.Profile.Id).Distinct().ToList(),
        };

        try
        {
            _session.Save(state, backups);
        }
        catch (Exception)
        {
            return false;
        }

        var versions = Documents.Where(d => documents.First(s => s.Id == d.SessionId).HasBackup).ToDictionary(d => d.SessionId, d => d.TextVersion);
        _backupVersions.Clear();
        foreach (var (id, version) in versions)
            _backupVersions[id] = version;
        return true;
    }

    /// <summary>
    /// Reopens the tabs of the last session: saved files from where they were saved, unsaved text from
    /// its backup. Then reconnects each tab to its server and database in the background; a tab whose
    /// connection fails stays open, unconnected, with the reason in its status. False when nothing was
    /// restored.
    /// </summary>
    public async Task<bool> RestoreSessionAsync()
    {
        if (_session.Load() is not { Documents.Count: > 0 } state)
            return false;

        var problems = new List<string>();
        QueryDocumentViewModel? selected = null;
        _restoring = true;
        try
        {
            for (int i = 0; i < state.Documents.Count; i++)
            {
                if (await RestoreDocumentAsync(state.Documents[i], problems) is not { } doc)
                    continue;
                Documents.Add(doc);
                if (i <= state.SelectedIndex || selected is null)
                    selected = doc;
            }
            SelectedDocument = selected;
        }
        finally
        {
            _restoring = false;
        }

        var profileIds = state.Servers
            .Concat(state.Documents.Where(d => d.ProfileId is not null).Select(d => d.ProfileId!.Value))
            .Distinct()
            .ToList();
        var reconnect = ReconnectAsync(profileIds);
        if (problems.Count > 0)
        {
            await _dialogs.ShowErrorAsync("Reopen Tabs",
                "Some tabs from the last session could not be reopened:\n\n" + string.Join("\n", problems.Select(p => "• " + p)));
        }
        await reconnect;
        ScheduleSessionSave();
        return Documents.Count > 0;
    }

    private async Task<QueryDocumentViewModel?> RestoreDocumentAsync(SessionDocument saved, List<string> problems)
    {
        string? text = saved.HasBackup ? _session.ReadBackup(saved.Id) : null;
        bool unsaved = text is not null;
        string? status = null;
        if (saved.FilePath is { } path)
        {
            if (text is null)
            {
                try
                {
                    text = await QueryDocumentViewModel.ReadFileAsync(path);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    problems.Add($"{path} no longer exists.");
                    return null;
                }
                catch (Exception ex)
                {
                    problems.Add($"{path}: {ex.Message}");
                    return null;
                }
            }
            else if (!File.Exists(path))
            {
                status = $"{Path.GetFileName(path)} no longer exists where it was saved; save the tab to create it again.";
            }
        }
        else if (text is null)
        {
            if (saved.HasBackup)
            {
                problems.Add($"The unsaved text of {saved.UntitledName ?? "an untitled tab"} could not be found.");
                return null;
            }
            text = "";
        }

        var doc = CreateDocument(saved.Id, saved.UntitledName);
        doc.Restore(text, saved.FilePath, unsaved: unsaved || (saved.FilePath is null && text.Length > 0));
        doc.OutputMode = saved.OutputMode;
        doc.CaretOffset = Math.Clamp(saved.CaretOffset, 0, text.Length);
        if (saved.ProfileId is { } profileId)
        {
            string name = saved.ServerDisplayName ?? "the server";
            doc.RememberedConnection = new RememberedConnection(profileId, name, saved.Database);
            doc.StatusText = $"Reconnecting to {name}…";
        }
        if (status is not null)
            doc.StatusText = status;
        // Its backup is current, so the next save need not rewrite it.
        if (unsaved)
            _backupVersions[saved.Id] = doc.TextVersion;
        return doc;
    }

    /// <summary>Connects each saved profile (in parallel) and gives the waiting tabs their connection back.</summary>
    private async Task ReconnectAsync(IReadOnlyList<Guid> profileIds)
    {
        if (profileIds.Count == 0)
            return;
        var profiles = _profiles.Load();
        var attempts = profileIds.Select(id => (Id: id, Attempt: ConnectSavedProfileAsync(profiles.FirstOrDefault(p => p.Id == id)))).ToList();
        foreach (var (id, attempt) in attempts)
        {
            var (connected, error) = await attempt;
            // The user may have connected this profile from the dialog in the meantime.
            var server = Servers.FirstOrDefault(s => s.Profile.Id == id);
            if (server is null && connected is not null)
            {
                AddServer(connected, select: false);
                server = connected;
            }

            foreach (var doc in Documents.Where(d => d.Server is null && d.RememberedConnection?.ProfileId == id).ToList())
            {
                var remembered = doc.RememberedConnection!;
                if (server is null)
                {
                    doc.StatusText = $"Could not reconnect to {remembered.ServerDisplayName}. {error}";
                    continue;
                }
                await ReconnectDocumentAsync(doc, server, remembered.Database);
            }
        }
    }

    private async Task<(ServerConnection? Server, string? Error)> ConnectSavedProfileAsync(ConnectionProfile? profile)
    {
        if (profile is null)
            return (null, "The saved connection no longer exists.");
        string? secret;
        try
        {
            secret = _profiles.GetSecret(profile);
        }
        catch (Exception ex)
        {
            return (null, $"The saved password could not be read: {ex.Message}");
        }
        if (profile.Authentication.RequiresSecret() && string.IsNullOrEmpty(secret))
            return (null, "No password is saved for it; connect again to enter one.");

        var server = new ServerConnection(profile, secret);
        try
        {
            await server.ConnectAsync();
            return (server, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private static async Task ReconnectDocumentAsync(QueryDocumentViewModel doc, ServerConnection server, string? database)
    {
        bool available = true;
        if (database is not null)
        {
            try
            {
                available = (await server.Metadata.GetDatabasesAsync()).Contains(database, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                // Let the tab try it; running a query reports the real problem.
            }
        }
        await doc.SetServerAsync(server, available ? database : null);
        doc.StatusText = available
            ? $"Reconnected to {doc.ServerAndDatabase}."
            : $"Database {database} is not available; connected to {doc.ServerAndDatabase} instead.";
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
