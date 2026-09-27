using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit;
using Quarry.App.Editor;
using Quarry.App.Services;
using Quarry.App.ViewModels;
using Quarry.Parsing;

namespace Quarry.App.Views;

public partial class QueryDocumentView : UserControl, IEditorAccessor
{
    private QueryDocumentViewModel? _vm;
    private SqlEditorController? _controller;
    private readonly List<(ResultSetViewModel Model, DataGrid Grid)> _grids = [];

    public QueryDocumentView()
    {
        InitializeComponent();
        ConfigureEditor(Editor);
        ConfigureEditor(TextOutput);
        TextOutput.Options.HighlightCurrentLine = false;
        GridHost.SizeChanged += (_, _) => LayoutGrids();
        ApplySettings();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        MainWindow.SettingsChanged += OnSettingsChanged;
        ApplySettings();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        MainWindow.SettingsChanged -= OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => ApplySettings();

    private static void ConfigureEditor(TextEditor editor)
    {
        editor.Options.ConvertTabsToSpaces = true;
        editor.Options.IndentationSize = 4;
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;
        editor.Options.HighlightCurrentLine = true;
    }

    private void ApplySettings()
    {
        Editor.FontSize = AppServices.Settings.EditorFontSize;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Detach();
        if (DataContext is QueryDocumentViewModel vm)
            Attach(vm);
    }

    private void Attach(QueryDocumentViewModel vm)
    {
        _vm = vm;
        vm.Editor = this;
        Editor.Document = vm.Document;
        _controller = new SqlEditorController(Editor, () => new AnalyzerOptions { BatchSeparator = AppServices.Settings.BatchSeparator });
        _ = new SqlCompletionController(Editor, vm);
        ApplyTheme();
        if (Application.Current is { } app)
            app.ActualThemeVariantChanged += OnThemeChanged;

        vm.Results.CollectionChanged += OnResultsChanged;
        vm.PropertyChanged += OnViewModelPropertyChanged;
        foreach (var rs in vm.Results)
            AddGrid(rs);
        TextOutput.Text = vm.TextOutput;
    }

    private void Detach()
    {
        if (_vm is null)
            return;
        _vm.Results.CollectionChanged -= OnResultsChanged;
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        if (_vm.Editor == this)
            _vm.Editor = null;
        if (Application.Current is { } app)
            app.ActualThemeVariantChanged -= OnThemeChanged;
        _controller?.Dispose();
        _controller = null;
        ClearGrids();
        _vm = null;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyTheme();

    private void ApplyTheme()
    {
        var variant = Application.Current?.ActualThemeVariant ?? Avalonia.Styling.ThemeVariant.Light;
        _controller?.ApplyTheme(variant);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QueryDocumentViewModel.TextOutput) && _vm is not null)
        {
            TextOutput.Text = _vm.TextOutput;
            TextOutput.ScrollToHome();
        }
    }

    // ---- Result grids ----

    private void OnResultsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            foreach (ResultSetViewModel rs in e.NewItems)
                AddGrid(rs);
        }
        else
        {
            ClearGrids();
            foreach (var rs in _vm?.Results ?? [])
                AddGrid(rs);
        }
    }

    private void AddGrid(ResultSetViewModel rs)
    {
        var grid = ResultGridFactory.Create(rs, () => _vm?.ExportResultsAsync(only: rs) ?? Task.CompletedTask);
        _grids.Add((rs, grid));
        GridStack.Children.Add(grid);
        LayoutGrids();
    }

    private void ClearGrids()
    {
        GridStack.Children.Clear();
        _grids.Clear();
    }

    /// <summary>One result set fills the pane; several share it equally, with a minimum height and scrolling.</summary>
    private void LayoutGrids()
    {
        if (_grids.Count == 0)
            return;
        double available = Math.Max(GridHost.Bounds.Height, 120);
        double spacing = GridStack.Spacing * (_grids.Count - 1);
        double height = Math.Max((available - spacing) / _grids.Count, _grids.Count == 1 ? available : 180);
        foreach (var (_, grid) in _grids)
            grid.Height = height;
    }

    private void OnMessageDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (MessageList.SelectedItem is MessageViewModel message)
            _vm?.NavigateToMessageCommand.Execute(message);
    }

    // ---- IEditorAccessor ----

    public int CaretOffset => Editor.CaretOffset;

    public TextSpan Selection => new(Editor.SelectionStart, Editor.SelectionLength);

    public ScriptModel GetModel()
        => _controller?.GetCurrentModel() ?? ScriptAnalyzer.Analyze(Editor.Document.Text);

    public void GoToLine(int zeroBasedLine)
    {
        int line = Math.Clamp(zeroBasedLine + 1, 1, Editor.Document.LineCount);
        var docLine = Editor.Document.GetLineByNumber(line);
        Editor.CaretOffset = docLine.Offset;
        Editor.Select(docLine.Offset, docLine.Length);
        Editor.ScrollToLine(line);
        Focus();
    }

    public void SetCaret(int offset)
    {
        Editor.SelectionLength = 0;
        Editor.CaretOffset = Math.Clamp(offset, 0, Editor.Document.TextLength);
        Editor.TextArea.Caret.BringCaretToView();
    }

    public void Select(int start, int length)
    {
        Editor.CaretOffset = start + length;
        Editor.Select(start, length);
    }

    public void InsertAtCaret(string text)
    {
        if (Editor.SelectionLength > 0)
            Editor.SelectedText = text;
        else
            Editor.Document.Insert(Editor.CaretOffset, text);
    }

    public void Focus()
        => Dispatcher.UIThread.Post(() => Editor.TextArea.Focus(), DispatcherPriority.Input);
}
