using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using AvaloniaEdit;
using Avalonia.VisualTree;
using Microsoft.Data.SqlClient;
using Quarry.App.ViewModels;
using Quarry.App.Views;
using Quarry.Core;
using Quarry.Core.Connections;
using Quarry.Core.Credentials;
using Quarry.Core.Results;
using Quarry.Core.Session;
using Quarry.App.Services;

[assembly: AvaloniaTestApplication(typeof(Quarry.App.Tests.TestAppBuilder))]

namespace Quarry.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        // Keep tests away from the user's real profiles and settings.
        AppPaths.DataDirectory = Path.Combine(Path.GetTempPath(), $"quarry-ui-tests-{Environment.ProcessId}");
        return AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}

public class UiTests
{
    /// <summary>Where rendered frames are saved for inspection (QUARRY_UI_SNAPSHOTS, else a temp folder).</summary>
    private static readonly string SnapshotDir =
        Environment.GetEnvironmentVariable("QUARRY_UI_SNAPSHOTS") is { Length: > 0 } dir ? dir : Path.Combine(Path.GetTempPath(), "quarry-ui-snapshots");

    private static ServerConnection? TestServer()
        => TestProfile() is var (profile, secret) ? new ServerConnection(profile, secret) : null;

    private static (ConnectionProfile Profile, string? Secret)? TestProfile()
    {
        string? cs = Environment.GetEnvironmentVariable("QUARRY_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
            return null;
        var b = new SqlConnectionStringBuilder(cs);
        var profile = new ConnectionProfile
        {
            Server = b.DataSource,
            Authentication = b.IntegratedSecurity ? AuthenticationKind.WindowsIntegrated : AuthenticationKind.SqlPassword,
            UserName = b.IntegratedSecurity ? null : b.UserID,
            TrustServerCertificate = b.TrustServerCertificate,
        };
        return (profile, b.IntegratedSecurity ? null : b.Password);
    }

    private static void Snapshot(Window window, string name)
    {
        Directory.CreateDirectory(SnapshotDir);
        Dispatcher.UIThread.RunJobs();
#pragma warning disable CS0618 // the replacement overload needs BitmapEncoderOptions, which has no public constructor
        window.CaptureRenderedFrame()?.Save(Path.Combine(SnapshotDir, name + ".png"));
#pragma warning restore CS0618
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 20000)
    {
        var start = DateTime.UtcNow;
        while (!condition())
        {
            if ((DateTime.UtcNow - start).TotalMilliseconds > timeoutMs)
                throw new TimeoutException("Condition not met.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static TextEditor EditorOf(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return FindEditor(window);
    }

    private static TextEditor FindEditor(Window window)
        => window.GetVisualDescendants().OfType<QueryDocumentView>().First(v => v.IsVisible).FindControl<TextEditor>("Editor")!;

    [AvaloniaFact]
    public async Task EmptyWindow_Renders()
    {
        var window = new MainWindow { Width = 1200, Height = 760 };
        window.Show();
        await window.ViewModel.NewQueryAsync(null, null);
        EditorOf(window).Document.Text = "SELECT name\nFROM sys.databases\nWHERE database_id > 4\n\nSELECT 1 AS one\n";
        Snapshot(window, "01-empty");
        Assert.Single(window.ViewModel.Documents);
        window.Close();
    }

    [AvaloniaFact]
    public async Task RunQueryAndScript_ShowResultsInEveryOutputMode()
    {
        if (TestServer() is not { } server)
            return; // needs QUARRY_TEST_CONNECTION
        await server.ConnectAsync();

        var window = new MainWindow { Width = 1300, Height = 820 };
        window.Show();
        var vm = window.ViewModel;
        vm.ExplorerRoots.Add(new ServerNode(server, vm) { IsExpanded = true });
        vm.Servers.Add(server);
        var doc = await vm.NewQueryAsync(server, "master");
        var editor = EditorOf(window);
        Assert.Equal("master", doc.Database);
        Assert.Equal($"{server.Profile.DisplayName}/master", doc.ServerAndDatabase);

        const string script = """
            -- Two statements without semicolons, then a batch separator
            SELECT TOP (25) name, database_id, create_date, compatibility_level
            FROM sys.databases
            ORDER BY database_id

            SELECT 42 AS answer, N'text' AS label, CAST(NULL AS int) AS nothing
            GO
            PRINT 'second batch'
            SELECT 1/0 AS boom
            """;
        editor.Document.Text = script;

        // Run Query with the caret inside the second statement: only that statement runs.
        editor.CaretOffset = script.IndexOf("42", StringComparison.Ordinal);
        await vm.RunQueryAsync();
        await WaitUntilAsync(() => !doc.IsExecuting);
        var only = Assert.Single(doc.Results);
        Assert.Equal(["answer", "label", "nothing"], only.Columns.Select(c => c.Name));
        Snapshot(window, "02-run-query");

        // Run Script: both batches; the last statement fails with divide by zero.
        await vm.RunScriptAsync();
        await WaitUntilAsync(() => !doc.IsExecuting);
        Assert.InRange(doc.Results.Count, 2, 3); // SELECT 1/0 may send column metadata before failing
        Assert.Contains(doc.Messages, m => m.Text == "second batch");
        var error = Assert.Single(doc.Messages, m => m.Kind == Core.Execution.MessageKind.Error);
        Assert.Equal(8, error.DocumentLine); // "SELECT 1/0" is on line 9 (zero-based 8)
        Assert.Equal(doc.Results.Count, window.GetVisualDescendants().OfType<DataGrid>().Count());
        Snapshot(window, "03-run-script-grid");

        doc.SelectedOutputTab = 1;
        Snapshot(window, "04-messages");
        doc.SelectedOutputTab = 0;

        foreach (var mode in new[] { OutputMode.Text, OutputMode.Csv, OutputMode.Tsv })
        {
            doc.OutputMode = mode;
            await WaitUntilAsync(() => doc.TextOutput.Length > 0);
            Snapshot(window, $"05-output-{mode}");
        }
        Assert.Contains("answer\tlabel\tnothing", doc.TextOutput);

        // Explorer: databases load under the server node. A fresh server (as in CI) has only
        // system databases, which live in the "System Databases" folder.
        await WaitUntilAsync(() => vm.ExplorerRoots[0].Children.OfType<StaticFolderNode>().Any());
        var systemFolder = vm.ExplorerRoots[0].Children.OfType<StaticFolderNode>().First();
        var master = systemFolder.Children.OfType<DatabaseNode>().First(d => d.Database == "master");
        systemFolder.IsExpanded = true;
        master.IsExpanded = true;
        await WaitUntilAsync(() => master.Children.Count > 1);
        var views = master.Children.OfType<ObjectFolderNode>().First(f => f.Text.StartsWith("Views"));
        views.IsExpanded = true;
        Snapshot(window, "06-explorer");

        window.Close();
    }

    [AvaloniaFact]
    public async Task ExportResults_AndRunToFile()
    {
        if (TestServer() is not { } server)
            return; // needs QUARRY_TEST_CONNECTION
        await server.ConnectAsync();
        string dir = Path.Combine(Path.GetTempPath(), $"quarry-ui-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var window = new MainWindow { Width = 1300, Height = 820 };
            window.Show();
            var vm = window.ViewModel;
            vm.Servers.Add(server);
            var doc = await vm.NewQueryAsync(server, "master");
            var editor = EditorOf(window);
            editor.Document.Text = "SELECT TOP (10) name, database_id FROM sys.databases\nSELECT 'x' AS only_one";
            Assert.False(doc.CanExport);

            await vm.RunScriptAsync();
            await WaitUntilAsync(() => !doc.IsExecuting);
            Assert.True(doc.CanExport);

            // Export everything that is loaded: one CSV per result set.
            string csv = Path.Combine(dir, "loaded.csv");
            await doc.ExportResultsAsync(csv);
            Assert.True(File.Exists(csv));
            Assert.Equal(["only_one", "x"], File.ReadAllLines(Path.Combine(dir, "loaded_2.csv")));
            Assert.Contains(doc.Messages, m => m.Text.StartsWith("Wrote") && m.Text.Contains("loaded_2.csv"));

            // Export a single result set to Excel.
            string xlsx = Path.Combine(dir, "second.xlsx");
            await doc.ExportResultsAsync(xlsx, only: doc.Results[1]);
            Assert.True(new FileInfo(xlsx).Length > 0);

            // Run to file: nothing goes to the grid, messages still show.
            string json = Path.Combine(dir, "streamed.json");
            await doc.RunToFileAsync(RunMode.Script, json);
            await WaitUntilAsync(() => !doc.IsExecuting);
            Assert.Empty(doc.Results);
            Assert.False(doc.CanExport);
            Assert.True(File.Exists(Path.Combine(dir, "streamed_2.json")));
            Assert.Contains("Results written to streamed.json (+1)", doc.StatusText);
            Assert.Equal(1, doc.SelectedOutputTab);
            Snapshot(window, "08-run-to-file");
            window.Close();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task QueryHistory_RecordsFiltersAndReopens()
    {
        if (TestServer() is not { } server)
            return; // needs QUARRY_TEST_CONNECTION
        await server.ConnectAsync();

        var window = new MainWindow { Width = 1300, Height = 820 };
        window.Show();
        var vm = window.ViewModel;
        vm.Servers.Add(server);
        var doc = await vm.NewQueryAsync(server, "master");
        var editor = EditorOf(window);
        string marker = $"hist_{Guid.NewGuid():N}"[..13];

        // Run Script records the whole script, batches joined with GO.
        editor.Document.Text = $"SELECT 7 AS {marker}\nGO\nSELECT 8 AS eight";
        await vm.RunScriptAsync();
        await WaitUntilAsync(() => !doc.IsExecuting && vm.History.Items.FirstOrDefault()?.Entry.Text.Contains(marker) == true);
        var entry = vm.History.Items[0].Entry;
        Assert.Equal($"SELECT 7 AS {marker}\nGO\nSELECT 8 AS eight", entry.Text);
        Assert.Equal(Core.History.HistoryRunKind.Script, entry.Kind);
        Assert.Equal(Core.History.HistoryOutcome.Succeeded, entry.Outcome);
        Assert.Equal("master", entry.Database);
        Assert.Equal(2, entry.RowCount);
        Assert.Equal(2, entry.ResultSetCount);

        // Run Query records just the statement, and failures are marked.
        editor.Document.Text = "SELECT 1\n\nSELECT * FROM dbo.no_such_table_quarry";
        editor.CaretOffset = editor.Document.TextLength;
        await vm.RunQueryAsync();
        await WaitUntilAsync(() => !doc.IsExecuting && vm.History.Items[0].Entry.Text.Contains("no_such_table_quarry"));
        Assert.Equal("SELECT * FROM dbo.no_such_table_quarry", vm.History.Items[0].Entry.Text);
        Assert.Equal(Core.History.HistoryOutcome.Failed, vm.History.Items[0].Outcome);
        Assert.Equal(Core.History.HistoryRunKind.Query, vm.History.Items[0].Entry.Kind);

        // Persisted to disk.
        Assert.Contains(new Core.History.QueryHistoryStore().Load(), e => e.Id == entry.Id);

        // Filter.
        vm.History.Filter = marker;
        var match = Assert.Single(vm.History.Items);
        Assert.Equal(entry.Id, match.Entry.Id);
        vm.SelectedSidePanel = 1;
        Snapshot(window, "09-history");

        // Reopen: new tab with the SQL, same server and database.
        int tabs = vm.Documents.Count;
        await vm.History.OpenAsync(match);
        Assert.Equal(tabs + 1, vm.Documents.Count);
        Assert.Equal(entry.Text, vm.SelectedDocument!.Document.Text);
        Assert.Same(server, vm.SelectedDocument.Server);
        Assert.Equal("master", vm.SelectedDocument.Database);

        // Turning history off stops recording.
        var original = Services.AppServices.Settings;
        try
        {
            Services.AppServices.Settings = original with { SaveQueryHistory = false };
            vm.History.Filter = "";
            int count = vm.History.Items.Count;
            var reopened = vm.SelectedDocument;
            await vm.RunScriptAsync();
            await WaitUntilAsync(() => !reopened.IsExecuting);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(count, vm.History.Items.Count);
        }
        finally
        {
            Services.AppServices.Settings = original;
        }
        window.Close();
    }

    [AvaloniaFact]
    public async Task FormatSql_WholeDocumentSelectionAndUndo()
    {
        var window = new MainWindow { Width = 1200, Height = 760 };
        window.Show();
        var vm = window.ViewModel;
        var doc = await vm.NewQueryAsync(null, null);
        var editor = EditorOf(window);

        const string messy = "select a.* from Table1 a join Table2 b on a.Id = b.Id where a.Something = 5\ngo\nselect x from y";
        editor.Document.Text = messy;
        editor.CaretOffset = messy.IndexOf("Something", StringComparison.Ordinal);

        await vm.FormatAsync();
        Assert.Equal(
            "SELECT a.*\nFROM   Table1 a\nJOIN   Table2 b ON a.Id = b.Id\nWHERE  a.Something = 5\nGO\nSELECT x\nFROM   y",
            editor.Document.Text);
        Assert.Equal("Formatted.", doc.StatusText);
        // The caret stays on the same code.
        Assert.StartsWith("Something", editor.Document.Text[editor.CaretOffset..]);
        Snapshot(window, "10-formatted");

        // One undo restores the original.
        editor.Undo();
        Assert.Equal(messy, editor.Document.Text);

        // Only the selection is formatted.
        int start = messy.IndexOf("select x", StringComparison.Ordinal);
        editor.Select(start, messy.Length - start);
        await vm.FormatAsync();
        Assert.Equal(messy[..start] + "SELECT x\nFROM   y", editor.Document.Text);

        // A batch with a syntax error is left alone and reported.
        editor.Document.Text = "select from where\ngo\nselect 1";
        editor.SelectionLength = 0;
        await vm.FormatAsync();
        Assert.Equal("select from where\nGO\nSELECT 1", editor.Document.Text);
        Assert.StartsWith("Formatted, except: Batch 1: syntax error", doc.StatusText);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SettingsDialog_FormattingPreviewFollowsOptions()
    {
        var owner = new Window { Width = 900, Height = 700 };
        owner.Show();
        var vm = new SettingsViewModel();
        var dialog = new SettingsDialog { DataContext = vm };
        _ = dialog.ShowDialog(owner);
        dialog.FindDescendantOfType<TabControl>()!.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.StartsWith("SELECT    a.Id,\n", vm.Preview.Replace("\r\n", "\n"));
        Snapshot(dialog, "11-settings-formatting");

        vm.SelectedClauseLayout = vm.ClauseLayouts.First(c => c.Value == Parsing.Formatting.ClauseLayout.Indented);
        vm.SelectedKeywordCase = vm.KeywordCases.First(c => c.Value == Parsing.Formatting.KeywordCase.Lower);
        Assert.StartsWith("select\n    a.Id,\n", vm.Preview.Replace("\r\n", "\n"));
        Assert.Contains("= case\n", vm.Preview.Replace("\r\n", "\n"));

        vm.SelectedBlockLayout = vm.BlockLayouts.First(c => c.Value == Parsing.Formatting.BlockLayout.SameLine);
        vm.SelectedControlFlowConditions = vm.ControlFlowConditionLayouts.First(c => c.Value == Parsing.Formatting.ConditionLayout.SameLine);
        Assert.Contains("if @Mode = 1 and @Debug = 0 begin\n", vm.Preview.Replace("\r\n", "\n"));
        Assert.Contains("end else\n", vm.Preview.Replace("\r\n", "\n"));

        Assert.Contains("create table dbo.Table5\n(\n    Id      int           not null primary key,\n", vm.Preview.Replace("\r\n", "\n"));
        vm.TableParenthesisOnOwnLine = false;
        vm.AlignColumnDefinitions = false;
        vm.SelectedDataTypeCase = vm.KeywordCases.First(c => c.Value == Parsing.Formatting.KeywordCase.Upper);
        Assert.Contains("create table dbo.Table5 (\n    Id INT not null primary key,\n", vm.Preview.Replace("\r\n", "\n"));
        dialog.Close();
        owner.Close();
        await Task.CompletedTask;
    }

    [AvaloniaFact]
    public async Task ConnectDialog_Renders()
    {
        var owner = new Window { Width = 900, Height = 700 };
        owner.Show();
        var dialog = new ConnectDialog { DataContext = new ConnectDialogViewModel() };
        _ = dialog.ShowDialog(owner);
        Dispatcher.UIThread.RunJobs();
        Snapshot(dialog, "07-connect-dialog");
        dialog.Close();
        owner.Close();
        await Task.CompletedTask;
    }

    [AvaloniaFact]
    public async Task Session_ReopensTabsAndReportsWhatItCannot()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"quarry-session-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SessionStore(Path.Combine(dir, "session.json"), Path.Combine(dir, "session"));
            var profiles = new ProfileStore(new InMemoryCredentialStore(), Path.Combine(dir, "connections.json"));
            var noPassword = new ConnectionProfile { Name = "No password", Server = "nowhere.invalid", UserName = "sa" };
            profiles.Save(noPassword, null);
            string clean = Path.Combine(dir, "clean.sql");
            string modified = Path.Combine(dir, "modified.sql");
            string deleted = Path.Combine(dir, "deleted.sql");
            File.WriteAllText(clean, "select 1");
            File.WriteAllText(modified, "select 2");
            File.WriteAllText(deleted, "select 3");

            var before = new MainWindowViewModel(new TestDialogs(), sessionStore: store, profiles: profiles);
            var untitled = await before.NewQueryAsync(null, null, "select 'untitled'");
            untitled.OutputMode = OutputMode.Text;
            untitled.CaretOffset = 7;
            var cleanDoc = await before.NewQueryAsync(null, null);
            await cleanDoc.LoadFileAsync(clean);
            var modifiedDoc = await before.NewQueryAsync(null, null);
            await modifiedDoc.LoadFileAsync(modified);
            modifiedDoc.Document.Insert(modifiedDoc.Document.TextLength, " -- edited");
            await (await before.NewQueryAsync(null, null)).LoadFileAsync(deleted);
            var waiting = await before.NewQueryAsync(null, null, "select db_name()");
            waiting.RememberedConnection = new RememberedConnection(noPassword.Id, "No password", "tempdb");
            var orphan = await before.NewQueryAsync(null, null);
            orphan.RememberedConnection = new RememberedConnection(Guid.NewGuid(), "Old server", null);
            before.SelectedDocument = modifiedDoc;
            Assert.True(before.SaveSession());

            File.WriteAllText(clean, "select 10"); // saved tabs are read again from their file
            File.Delete(modified);                 // unsaved changes outlive their file
            File.Delete(deleted);

            var dialogs = new TestDialogs();
            var after = new MainWindowViewModel(dialogs, sessionStore: store, profiles: profiles);
            Assert.True(await after.RestoreSessionAsync());
            var docs = after.Documents.ToList();
            Assert.Equal(5, docs.Count);

            Assert.Equal(untitled.FileName, docs[0].FileName);
            Assert.Equal("select 'untitled'", docs[0].Document.Text);
            Assert.True(docs[0].IsDirty);
            Assert.Equal(OutputMode.Text, docs[0].OutputMode);
            Assert.Equal(7, docs[0].CaretOffset);

            Assert.Equal(clean, docs[1].FilePath);
            Assert.Equal("select 10", docs[1].Document.Text);
            Assert.False(docs[1].IsDirty);

            Assert.Equal(modified, docs[2].FilePath);
            Assert.Equal("select 2 -- edited", docs[2].Document.Text);
            Assert.True(docs[2].IsDirty);
            Assert.Contains("no longer exists", docs[2].StatusText);
            Assert.Same(docs[2], after.SelectedDocument);

            Assert.Null(docs[3].Server);
            Assert.Equal("Could not reconnect to No password. No password is saved for it; connect again to enter one.", docs[3].StatusText);
            Assert.Equal("Could not reconnect to Old server. The saved connection no longer exists.", docs[4].StatusText);

            var (title, message) = Assert.Single(dialogs.Errors);
            Assert.Equal("Reopen Tabs", title);
            Assert.Contains($"{deleted} no longer exists.", message);

            // New tabs do not reuse a restored untitled name.
            var added = await after.NewQueryAsync(null, null);
            Assert.DoesNotContain(docs, d => d.FileName == added.FileName);
            await after.CloseDocumentAsync(added);

            // A tab that could not reconnect keeps its connection for the next start.
            after.SelectedDocument = docs[2];
            Assert.True(after.SaveSession());
            var saved = store.Load()!;
            Assert.Equal(noPassword.Id, saved.Documents[3].ProfileId);
            Assert.Equal("tempdb", saved.Documents[3].Database);
            Assert.Equal(2, saved.SelectedIndex);

            // Saving the modified tab to its file drops its backup.
            await docs[2].SaveFileAsync(modified);
            Assert.True(after.SaveSession());
            Assert.False(store.Load()!.Documents[2].HasBackup);
            Assert.Null(store.ReadBackup(docs[2].SessionId));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Session_ReconnectsToTheSavedDatabase()
    {
        if (TestProfile() is not var (profile, secret))
            return; // needs QUARRY_TEST_CONNECTION
        string dir = Path.Combine(Path.GetTempPath(), $"quarry-session-db-{Guid.NewGuid():N}");
        try
        {
            var store = new SessionStore(Path.Combine(dir, "session.json"), Path.Combine(dir, "session"));
            var profiles = new ProfileStore(new InMemoryCredentialStore(), Path.Combine(dir, "connections.json"));
            profiles.Save(profile, secret);
            var server = new ServerConnection(profile, secret);
            await server.ConnectAsync();

            var before = new MainWindowViewModel(new TestDialogs(), sessionStore: store, profiles: profiles);
            await before.NewQueryAsync(server, "tempdb", "select db_name()");
            await before.NewQueryAsync(server, "no_such_database_" + Guid.NewGuid().ToString("N"));
            Assert.True(await before.ConfirmExitAsync());

            var after = new MainWindowViewModel(new TestDialogs(), sessionStore: store, profiles: profiles);
            Assert.True(await after.RestoreSessionAsync());
            var connected = Assert.Single(after.Servers);
            Assert.Equal(profile.Id, connected.Profile.Id);
            Assert.All(after.Documents, d => Assert.Same(connected, d.Server));
            Assert.Equal("tempdb", after.Documents[0].Database);
            Assert.Equal($"{profile.DisplayName}/tempdb", after.Documents[0].ServerAndDatabase);
            Assert.StartsWith("Reconnected to", after.Documents[0].StatusText);
            Assert.Equal("master", after.Documents[1].Database);
            Assert.Contains("is not available", after.Documents[1].StatusText);
            Assert.True(await after.ConfirmExitAsync());
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Records errors; every other dialog is dismissed.</summary>
    private sealed class TestDialogs : IDialogService
    {
        public List<(string Title, string Message)> Errors { get; } = [];

        public Task<ServerConnection?> ShowConnectDialogAsync() => Task.FromResult<ServerConnection?>(null);

        public Task<SaveChoice> AskSaveChangesAsync(string documentName) => Task.FromResult(SaveChoice.Discard);

        public Task ShowErrorAsync(string title, string message)
        {
            Errors.Add((title, message));
            return Task.CompletedTask;
        }

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);

        public Task<bool> ShowSettingsAsync() => Task.FromResult(false);

        public Task<string?> PickOpenFileAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string suggestedName) => Task.FromResult<string?>(null);

        public Task<string?> PickExportFileAsync(string suggestedName) => Task.FromResult<string?>(null);

        public Task SetClipboardTextAsync(string text) => Task.CompletedTask;
    }
}
