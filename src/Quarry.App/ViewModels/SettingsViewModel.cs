using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Quarry.App.Services;
using Quarry.Core.Results;
using Quarry.Parsing.Formatting;

namespace Quarry.App.ViewModels;

/// <summary>A value with a friendly label, for ComboBoxes over enums.</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private const string PreviewSql =
        "select a.Id, a.Name, count(*) as Total from Table1 a join Table2 b on a.Id = b.Id and a.Type = b.Type " +
        "left join Table3 c on a.OtherId = c.OtherId where a.Something = 5 and not exists " +
        "(select 1 from Table4 d where d.Table4Id = a.Table4Id) group by a.Id, a.Name\n" +
        "if @Mode = 1 and @Debug = 0 begin update Table1 set Status = case when Total > 100 then 'Large' " +
        "when Total > 10 then 'Medium' else 'Small' end where Id = @Id end else print 'Skipped'\n" +
        "create table dbo.Table5 (Id int not null primary key, Name nvarchar(100) null, Created datetime2 not null default sysutcdatetime())";

    public SettingsViewModel()
    {
        var s = AppServices.Settings;
        _fontSize = s.EditorFontSize;
        _gridFontSize = s.GridFontSize;
        IsDarkTheme = ThemeColors.IsDark;
        _editorBackground = ThemeColors.Parse(IsDarkTheme ? s.EditorBackgroundDark : s.EditorBackgroundLight, IsDarkTheme ? "#1E1E1E" : "#FFFFFF");
        _paneBackground = ThemeColors.Parse(IsDarkTheme ? s.PaneBackgroundDark : s.PaneBackgroundLight, IsDarkTheme ? "#202024" : "#F5F5F7");
        EditorFonts = BuildFontChoices(FontCatalog.Monospace, "Default (monospace)");
        GridFonts = BuildFontChoices(FontCatalog.All, "Default");
        _selectedEditorFont = FindFont(EditorFonts, s.EditorFontFamily);
        _selectedGridFont = FindFont(GridFonts, s.GridFontFamily);
        _maxRows = s.MaxRowsPerResultSet;
        _commandTimeout = s.CommandTimeoutSeconds;
        _stopOnError = s.StopOnError;
        _defaultOutputMode = s.DefaultOutputMode;
        _batchSeparator = s.BatchSeparator;
        _saveQueryHistory = s.SaveQueryHistory;
        _exportIncludeHeaders = s.ExportIncludeHeaders;
        _exportUtf8Bom = s.ExportUtf8Bom;

        var f = s.Formatting;
        _selectedKeywordCase = KeywordCases.First(c => c.Value == f.KeywordCase);
        _selectedDataTypeCase = KeywordCases.First(c => c.Value == f.DataTypeCase);
        _alignColumnDefinitions = f.AlignColumnDefinitions;
        _tableParenthesisOnOwnLine = f.TableParenthesisOnOwnLine;
        _selectedClauseLayout = ClauseLayouts.First(c => c.Value == f.ClauseLayout);
        _selectedListLayout = ListLayouts.First(c => c.Value == f.ListLayout);
        _selectedCommaPlacement = CommaPlacements.First(c => c.Value == f.CommaPlacement);
        _selectedJoinConditions = JoinConditionLayouts.First(c => c.Value == f.JoinConditions);
        _selectedControlFlowConditions = ControlFlowConditionLayouts.First(c => c.Value == f.ControlFlowConditions);
        _selectedBlockLayout = BlockLayouts.First(c => c.Value == f.BlockLayout);
        _selectedCaseLayout = CaseLayouts.First(c => c.Value == f.CaseLayout);
        _singleWhenCaseOnOneLine = f.SingleWhenCaseOnOneLine;
        _whereConditionsOnNewLines = f.WhereConditionsOnNewLines;
        _indentSize = f.IndentSize;
        UpdatePreview();
    }

    public IReadOnlyList<OutputMode> OutputModes { get; } = Enum.GetValues<OutputMode>();

    public bool Saved { get; private set; }

    public event EventHandler? CloseRequested;

    // ---- General ----

    public IReadOnlyList<Choice<string>> EditorFonts { get; }

    public IReadOnlyList<Choice<string>> GridFonts { get; }

    private static IReadOnlyList<Choice<string>> BuildFontChoices(IEnumerable<string> names, string defaultLabel)
        => [new("", defaultLabel), .. names.Select(n => new Choice<string>(n, n))];

    private static Choice<string> FindFont(IReadOnlyList<Choice<string>> choices, string name)
        => choices.FirstOrDefault(c => string.Equals(c.Value, name, StringComparison.OrdinalIgnoreCase)) ?? choices[0];

    /// <summary>Only the colors for the active theme are shown and saved.</summary>
    public bool IsDarkTheme { get; }

    public string EditorBackgroundLabel => IsDarkTheme ? "Query editor background (dark mode)" : "Query editor background (light mode)";

    public string PaneBackgroundLabel => IsDarkTheme ? "Explorer and results background (dark mode)" : "Explorer and results background (light mode)";

    [ObservableProperty]
    private Avalonia.Media.Color _editorBackground;

    [ObservableProperty]
    private Avalonia.Media.Color _paneBackground;

    [ObservableProperty]
    private Choice<string> _selectedEditorFont;

    [ObservableProperty]
    private Choice<string> _selectedGridFont;

    [ObservableProperty]
    private double _fontSize;

    [ObservableProperty]
    private double _gridFontSize;

    [ObservableProperty]
    private int _maxRows;

    [ObservableProperty]
    private int _commandTimeout;

    [ObservableProperty]
    private bool _stopOnError;

    [ObservableProperty]
    private OutputMode _defaultOutputMode;

    [ObservableProperty]
    private string _batchSeparator;

    [ObservableProperty]
    private bool _saveQueryHistory;

    [ObservableProperty]
    private bool _exportIncludeHeaders;

    [ObservableProperty]
    private bool _exportUtf8Bom;

    [ObservableProperty]
    private string _error = "";

    // ---- Formatting ----

    public IReadOnlyList<Choice<KeywordCase>> KeywordCases { get; } =
    [
        new(KeywordCase.Upper, "UPPERCASE"),
        new(KeywordCase.Lower, "lowercase"),
        new(KeywordCase.Preserve, "Leave as typed"),
    ];

    public IReadOnlyList<Choice<ClauseLayout>> ClauseLayouts { get; } =
    [
        new(ClauseLayout.Aligned, "Aligned (bodies line up after the longest keyword)"),
        new(ClauseLayout.Compact, "Compact (one space after each keyword)"),
        new(ClauseLayout.Indented, "Indented (keyword on its own line)"),
    ];

    public IReadOnlyList<Choice<ListLayout>> ListLayouts { get; } =
    [
        new(ListLayout.OnePerLine, "One per line"),
        new(ListLayout.SingleLine, "Keep on one line"),
    ];

    public IReadOnlyList<Choice<CommaPlacement>> CommaPlacements { get; } =
    [
        new(CommaPlacement.Trailing, "Trailing (a,)"),
        new(CommaPlacement.Leading, "Leading (, a)"),
    ];

    public IReadOnlyList<Choice<ConditionLayout>> JoinConditionLayouts { get; } =
    [
        new(ConditionLayout.UnderFirstCondition, "New line, under the first condition"),
        new(ConditionLayout.BodyColumn, "New line, at the body column"),
        new(ConditionLayout.SameLine, "Same line"),
    ];

    public IReadOnlyList<Choice<ConditionLayout>> ControlFlowConditionLayouts { get; } =
    [
        new(ConditionLayout.BodyColumn, "New line, indented"),
        new(ConditionLayout.UnderFirstCondition, "New line, under the first condition"),
        new(ConditionLayout.SameLine, "Same line"),
    ];

    public IReadOnlyList<Choice<BlockLayout>> BlockLayouts { get; } =
    [
        new(BlockLayout.Aligned, "Own line, lined up with IF / ELSE / WHILE"),
        new(BlockLayout.SameLine, "End of the IF / ELSE / WHILE line (END ELSE BEGIN)"),
        new(BlockLayout.Indented, "Own line, indented"),
    ];

    public IReadOnlyList<Choice<CaseLayout>> CaseLayouts { get; } =
    [
        new(CaseLayout.Indented, "WHEN on new lines, indented"),
        new(CaseLayout.Aligned, "WHEN on new lines, under the first WHEN"),
        new(CaseLayout.SingleLine, "Keep on one line"),
    ];

    [ObservableProperty]
    private Choice<KeywordCase> _selectedKeywordCase;

    [ObservableProperty]
    private Choice<KeywordCase> _selectedDataTypeCase;

    [ObservableProperty]
    private bool _alignColumnDefinitions;

    [ObservableProperty]
    private bool _tableParenthesisOnOwnLine;

    [ObservableProperty]
    private Choice<ClauseLayout> _selectedClauseLayout;

    [ObservableProperty]
    private Choice<ListLayout> _selectedListLayout;

    [ObservableProperty]
    private Choice<CommaPlacement> _selectedCommaPlacement;

    [ObservableProperty]
    private Choice<ConditionLayout> _selectedJoinConditions;

    [ObservableProperty]
    private Choice<ConditionLayout> _selectedControlFlowConditions;

    [ObservableProperty]
    private Choice<BlockLayout> _selectedBlockLayout;

    [ObservableProperty]
    private Choice<CaseLayout> _selectedCaseLayout;

    [ObservableProperty]
    private bool _singleWhenCaseOnOneLine;

    [ObservableProperty]
    private bool _whereConditionsOnNewLines;

    [ObservableProperty]
    private int _indentSize;

    [ObservableProperty]
    private string _preview = "";

    private SqlFormatOptions FormatOptions => new()
    {
        KeywordCase = SelectedKeywordCase.Value,
        DataTypeCase = SelectedDataTypeCase.Value,
        AlignColumnDefinitions = AlignColumnDefinitions,
        TableParenthesisOnOwnLine = TableParenthesisOnOwnLine,
        ClauseLayout = SelectedClauseLayout.Value,
        ListLayout = SelectedListLayout.Value,
        CommaPlacement = SelectedCommaPlacement.Value,
        JoinConditions = SelectedJoinConditions.Value,
        WhereConditionsOnNewLines = WhereConditionsOnNewLines,
        ControlFlowConditions = SelectedControlFlowConditions.Value,
        BlockLayout = SelectedBlockLayout.Value,
        CaseLayout = SelectedCaseLayout.Value,
        SingleWhenCaseOnOneLine = SingleWhenCaseOnOneLine,
        IndentSize = Math.Clamp(IndentSize, 1, 16),
    };

    partial void OnSelectedKeywordCaseChanged(Choice<KeywordCase> value) => UpdatePreview();

    partial void OnSelectedDataTypeCaseChanged(Choice<KeywordCase> value) => UpdatePreview();

    partial void OnAlignColumnDefinitionsChanged(bool value) => UpdatePreview();

    partial void OnTableParenthesisOnOwnLineChanged(bool value) => UpdatePreview();

    partial void OnSelectedClauseLayoutChanged(Choice<ClauseLayout> value) => UpdatePreview();

    partial void OnSelectedListLayoutChanged(Choice<ListLayout> value) => UpdatePreview();

    partial void OnSelectedCommaPlacementChanged(Choice<CommaPlacement> value) => UpdatePreview();

    partial void OnSelectedJoinConditionsChanged(Choice<ConditionLayout> value) => UpdatePreview();

    partial void OnWhereConditionsOnNewLinesChanged(bool value) => UpdatePreview();

    partial void OnSelectedControlFlowConditionsChanged(Choice<ConditionLayout> value) => UpdatePreview();

    partial void OnSelectedBlockLayoutChanged(Choice<BlockLayout> value) => UpdatePreview();

    partial void OnSelectedCaseLayoutChanged(Choice<CaseLayout> value) => UpdatePreview();

    partial void OnSingleWhenCaseOnOneLineChanged(bool value) => UpdatePreview();

    partial void OnIndentSizeChanged(int value) => UpdatePreview();

    private void UpdatePreview()
    {
        // The constructor runs this before every choice is assigned.
        if (SelectedKeywordCase is null || SelectedDataTypeCase is null || SelectedClauseLayout is null || SelectedListLayout is null || SelectedCommaPlacement is null || SelectedJoinConditions is null
            || SelectedControlFlowConditions is null || SelectedBlockLayout is null || SelectedCaseLayout is null)
            return;
        Preview = SqlFormatter.Format(PreviewSql, FormatOptions).Text;
    }

    [RelayCommand]
    private void Save()
    {
        string separator = BatchSeparator.Trim();
        if (separator.Length == 0 || separator.Any(char.IsWhiteSpace))
        {
            Error = "The batch separator must be a single word.";
            return;
        }

        var settings = AppServices.Settings with
        {
            EditorFontFamily = SelectedEditorFont.Value,
            EditorFontSize = Math.Clamp(FontSize, 8, 40),
            GridFontFamily = SelectedGridFont.Value,
            GridFontSize = Math.Clamp(GridFontSize, 8, 40),
            MaxRowsPerResultSet = Math.Max(0, MaxRows),
            CommandTimeoutSeconds = Math.Max(0, CommandTimeout),
            StopOnError = StopOnError,
            DefaultOutputMode = DefaultOutputMode,
            BatchSeparator = separator,
            SaveQueryHistory = SaveQueryHistory,
            ExportIncludeHeaders = ExportIncludeHeaders,
            ExportUtf8Bom = ExportUtf8Bom,
            Formatting = FormatOptions,
        };
        string editorColor = ThemeColors.Format(EditorBackground);
        string paneColor = ThemeColors.Format(PaneBackground);
        settings = IsDarkTheme
            ? settings with { EditorBackgroundDark = editorColor, PaneBackgroundDark = paneColor }
            : settings with { EditorBackgroundLight = editorColor, PaneBackgroundLight = paneColor };
        try
        {
            settings.Save();
        }
        catch (Exception ex)
        {
            Error = $"Could not save settings: {ex.Message}";
            return;
        }
        AppServices.Settings = settings;
        ThemeColors.Apply(settings);
        Saved = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
