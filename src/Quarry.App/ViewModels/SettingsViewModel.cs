using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Quarry.App.Services;
using Quarry.Core.Results;
using Quarry.Core.Settings;

namespace Quarry.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel()
    {
        var s = AppServices.Settings;
        _fontSize = s.EditorFontSize;
        _maxRows = s.MaxRowsPerResultSet;
        _commandTimeout = s.CommandTimeoutSeconds;
        _stopOnError = s.StopOnError;
        _defaultOutputMode = s.DefaultOutputMode;
        _batchSeparator = s.BatchSeparator;
    }

    public IReadOnlyList<OutputMode> OutputModes { get; } = Enum.GetValues<OutputMode>();

    public bool Saved { get; private set; }

    public event EventHandler? CloseRequested;

    [ObservableProperty]
    private double _fontSize;

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
    private string _error = "";

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
            EditorFontSize = Math.Clamp(FontSize, 8, 40),
            MaxRowsPerResultSet = Math.Max(0, MaxRows),
            CommandTimeoutSeconds = Math.Max(0, CommandTimeout),
            StopOnError = StopOnError,
            DefaultOutputMode = DefaultOutputMode,
            BatchSeparator = separator,
        };
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
        Saved = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
