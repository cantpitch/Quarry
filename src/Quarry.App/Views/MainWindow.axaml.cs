using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Quarry.App.Services;
using Quarry.App.ViewModels;
using Quarry.Core.Connections;

namespace Quarry.App.Views;

public partial class MainWindow : Window, IDialogService
{
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new MainWindowViewModel(this);
        DataContext = ViewModel;
        // Tunnel so shortcuts win over the editor's own key handling (e.g. Ctrl+Enter).
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        _ = ViewModel.History.LoadAsync();
        SettingsChanged += (_, _) => ViewModel.History.SettingsChanged();
    }

    /// <summary>
    /// Reopens the last session's tabs; when there were none, opens an empty query tab and the
    /// connect dialog.
    /// </summary>
    public async Task StartupAsync()
    {
        if (await ViewModel.RestoreSessionAsync())
            return;
        await ViewModel.NewQueryAsync(null, null);
        await ViewModel.ConnectAsync();
    }

    public MainWindowViewModel ViewModel { get; }

    private KeyModifiers CommandModifier
        => Avalonia.Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        var cmd = CommandModifier;
        bool command = mods == cmd;
        Func<Task>? action = e.Key switch
        {
            Key.F5 when mods == KeyModifiers.None => ViewModel.RunScriptAsync,
            Key.E when command => ViewModel.RunScriptAsync,
            Key.Enter when command => ViewModel.RunQueryAsync,
            Key.N when command => ViewModel.NewQueryAsync,
            Key.O when command => ViewModel.OpenFileAsync,
            Key.S when command => ViewModel.SaveAsync,
            Key.S when mods == (cmd | KeyModifiers.Shift) => ViewModel.SaveAsAsync,
            Key.W when command => () => ViewModel.CloseDocumentAsync(null),
            Key.E when mods == (cmd | KeyModifiers.Shift) => ViewModel.ExportResultsAsync,
            Key.F5 when mods == KeyModifiers.Shift => ViewModel.RunScriptToFileAsync,
            Key.F when mods == (cmd | KeyModifiers.Shift) => ViewModel.FormatAsync,
            _ => null,
        };
        if (action is null && e.Key == Key.Escape && ViewModel.SelectedDocument is { IsExecuting: true })
            action = () =>
            {
                ViewModel.Cancel();
                return Task.CompletedTask;
            };

        if (action is not null)
        {
            e.Handled = true;
            _ = action();
        }
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed)
            return;
        e.Cancel = true;
        if (await ViewModel.ConfirmExitAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnServerPickerChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ServerPicker.SelectedItem is ServerConnection server)
            await ViewModel.ChangeDocumentServerAsync(server);
    }

    // ---- Object explorer ----

    private static ExplorerNode? NodeFromSource(object? source)
        => (source as Control)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext as ExplorerNode;

    private void OnExplorerContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (NodeFromSource(e.Source) is not { } node)
            return;
        ViewModel.SelectedNode = node;
        var actions = node.GetActions().ToList();
        if (node.InsertText is { } text)
        {
            actions.Add(new NodeAction("Insert Name into Editor", () =>
            {
                ViewModel.InsertIntoEditor(text);
                return Task.CompletedTask;
            }));
        }
        if (actions.Count == 0)
            return;

        var menu = new ContextMenu();
        foreach (var action in actions)
        {
            var item = new MenuItem { Header = action.Header };
            item.Click += async (_, _) =>
            {
                try
                {
                    await action.Execute();
                }
                catch (Exception ex)
                {
                    await ShowErrorAsync(action.Header, ex.Message);
                }
            };
            menu.Items.Add(item);
        }
        menu.Open(Explorer);
        e.Handled = true;
    }

    private void OnExplorerDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Double-clicking a column, object or database inserts its name, like dragging in SSMS.
        if (NodeFromSource(e.Source) is { InsertText: { } text } node && node is LeafNode or ObjectNode or DatabaseNode)
        {
            ViewModel.InsertIntoEditor(text);
            e.Handled = true;
        }
    }

    // ---- IDialogService ----

    public async Task<ServerConnection?> ShowConnectDialogAsync()
    {
        var vm = new ConnectDialogViewModel();
        var dialog = new ConnectDialog { DataContext = vm };
        vm.CloseRequested += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
        return vm.Result;
    }

    public Task<SaveChoice> AskSaveChangesAsync(string documentName)
        => MessageDialog.AskSaveAsync(this, documentName);

    public Task ShowErrorAsync(string title, string message)
        => MessageDialog.ShowAsync(this, title, message);

    public Task<bool> ConfirmAsync(string title, string message)
        => MessageDialog.ConfirmAsync(this, title, message);

    public async Task<bool> ShowSettingsAsync()
    {
        var vm = new SettingsViewModel();
        var dialog = new SettingsDialog { DataContext = vm };
        vm.CloseRequested += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
        if (vm.Saved)
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        return vm.Saved;
    }

    public static event EventHandler? SettingsChanged;

    private static readonly FilePickerFileType SqlFiles = new("SQL files") { Patterns = ["*.sql"] };
    private static readonly FilePickerFileType AllFiles = new("All files") { Patterns = ["*"] };

    public async Task<string?> PickOpenFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open SQL file",
            AllowMultiple = false,
            FileTypeFilter = [SqlFiles, AllFiles],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save SQL file",
            SuggestedFileName = suggestedName,
            DefaultExtension = "sql",
            FileTypeChoices = [SqlFiles, AllFiles],
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    private static readonly FilePickerFileType[] ExportTypes =
    [
        new("CSV (comma-separated)") { Patterns = ["*.csv"] },
        new("Excel workbook") { Patterns = ["*.xlsx"] },
        new("TSV (tab-separated)") { Patterns = ["*.tsv"] },
        new("JSON") { Patterns = ["*.json"] },
        new("Text (fixed width)") { Patterns = ["*.txt"] },
    ];

    /// <summary>The extension used for the last export, suggested again next time.</summary>
    private static string _lastExportExtension = ".csv";

    public async Task<string?> PickExportFileAsync(string suggestedName)
    {
        var suggestedType = ExportTypes.FirstOrDefault(t => t.Patterns![0].EndsWith(_lastExportExtension, StringComparison.Ordinal));
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export results",
            SuggestedFileName = suggestedName + _lastExportExtension,
            DefaultExtension = _lastExportExtension.TrimStart('.'),
            FileTypeChoices = ExportTypes,
            SuggestedFileType = suggestedType,
            ShowOverwritePrompt = true,
        });
        if (file?.TryGetLocalPath() is not { } path)
            return null;
        // An unknown extension (e.g. typed by hand) gets the last-used format's extension.
        if (Core.Export.ResultExporter.FormatFromPath(path) is null)
            path += _lastExportExtension;
        _lastExportExtension = Path.GetExtension(path).ToLowerInvariant();
        return path;
    }

    public async Task SetClipboardTextAsync(string text)
    {
        if (Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }
}
