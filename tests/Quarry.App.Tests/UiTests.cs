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
using Quarry.Core.Results;

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
        return new ServerConnection(profile, b.IntegratedSecurity ? null : b.Password);
    }

    private static void Snapshot(Window window, string name)
    {
        Directory.CreateDirectory(SnapshotDir);
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(SnapshotDir, name + ".png"));
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

        // Explorer: databases load under the server node.
        await WaitUntilAsync(() => vm.ExplorerRoots[0].Children.Any(c => c is DatabaseNode));
        var master = vm.ExplorerRoots[0].Children.OfType<StaticFolderNode>().First().Children.OfType<DatabaseNode>().First(d => d.Database == "master");
        vm.ExplorerRoots[0].Children.OfType<StaticFolderNode>().First().IsExpanded = true;
        master.IsExpanded = true;
        await WaitUntilAsync(() => master.Children.Count > 1);
        var views = master.Children.OfType<ObjectFolderNode>().First(f => f.Text.StartsWith("Views"));
        views.IsExpanded = true;
        Snapshot(window, "06-explorer");

        window.Close();
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
}
