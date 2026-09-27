using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using Quarry.Parsing;

namespace Quarry.App.Editor;

/// <summary>
/// Keeps a <see cref="ScriptModel"/> for an editor up to date (debounced, parsed off the UI thread)
/// and drives the current-statement highlight and syntax error underlines from it.
/// </summary>
public sealed class SqlEditorController : IDisposable
{
    private readonly TextEditor _editor;
    private readonly Func<AnalyzerOptions> _options;
    private readonly CurrentStatementRenderer _statementRenderer = new();
    private readonly ErrorRenderer _errorRenderer = new();
    private readonly DispatcherTimer _timer;
    private int _version;
    private int _modelVersion = -1;
    private ScriptModel? _model;

    public SqlEditorController(TextEditor editor, Func<AnalyzerOptions> options)
    {
        _editor = editor;
        _options = options;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _ = AnalyzeInBackgroundAsync();
        };

        var textView = editor.TextArea.TextView;
        textView.BackgroundRenderers.Add(_statementRenderer);
        textView.BackgroundRenderers.Add(_errorRenderer);

        editor.Document.Changed += OnDocumentChanged;
        editor.TextArea.Caret.PositionChanged += (_, _) => UpdateStatementHighlight();
        editor.TextArea.SelectionChanged += (_, _) => UpdateStatementHighlight();
        _ = AnalyzeInBackgroundAsync();
    }

    public event EventHandler<ScriptModel>? ModelUpdated;

    public void ApplyTheme(ThemeVariant variant)
    {
        bool dark = variant == ThemeVariant.Dark;
        _editor.SyntaxHighlighting = SqlHighlighting.Get(dark);
        _statementRenderer.Brush = new SolidColorBrush(dark ? Color.FromArgb(34, 120, 170, 255) : Color.FromArgb(22, 40, 110, 230));
        _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    /// <summary>The model for the current text, analysing synchronously if the cached one is stale.</summary>
    public ScriptModel GetCurrentModel()
    {
        if (_model is null || _modelVersion != _version)
            SetModel(ScriptAnalyzer.Analyze(_editor.Document.Text, _options()), _version);
        return _model!;
    }

    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        _version++;
        // Offsets are stale until the next analysis; hide the decorations meanwhile.
        _statementRenderer.Span = null;
        _errorRenderer.Errors = [];
        _timer.Stop();
        _timer.Start();
    }

    private async Task AnalyzeInBackgroundAsync()
    {
        int version = _version;
        string text = _editor.Document.Text;
        var options = _options();
        var model = await Task.Run(() => ScriptAnalyzer.Analyze(text, options));
        if (version == _version)
            SetModel(model, version);
    }

    private void SetModel(ScriptModel model, int version)
    {
        _model = model;
        _modelVersion = version;
        _errorRenderer.Errors = model.Errors.Select(e => ErrorSpan(model.Text, e.Offset)).ToList();
        UpdateStatementHighlight();
        _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        ModelUpdated?.Invoke(this, model);
    }

    private static TextSpan ErrorSpan(string text, int offset)
    {
        int end = offset;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
            end++;
        return TextSpan.FromBounds(offset, Math.Max(end, Math.Min(offset + 1, text.Length)));
    }

    private void UpdateStatementHighlight()
    {
        TextSpan? span = null;
        if (_model is not null && _modelVersion == _version && _editor.SelectionLength == 0)
            span = ExecutionPlanner.StatementAt(_model, _editor.CaretOffset)?.Span;
        if (span != _statementRenderer.Span)
        {
            _statementRenderer.Span = span;
            _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _editor.Document.Changed -= OnDocumentChanged;
    }
}
