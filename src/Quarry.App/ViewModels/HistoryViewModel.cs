using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Data.Converters;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Quarry.App.Services;
using Quarry.Core.History;

namespace Quarry.App.ViewModels;

/// <summary>What the History panel needs from the rest of the app.</summary>
public interface IHistoryHost
{
    /// <summary>Opens the entry's SQL in a new tab on its server and database, optionally running it.</summary>
    Task OpenHistoryAsync(QueryHistoryEntry entry, bool run);

    void InsertIntoEditor(string text);

    Task CopyTextAsync(string text);

    Task<bool> ConfirmAsync(string title, string message);

    Task ShowErrorAsync(string title, string message);
}

public sealed partial class HistoryItemViewModel(QueryHistoryEntry entry)
{
    public QueryHistoryEntry Entry { get; } = entry;

    /// <summary>First line of SQL that is not blank or a comment, whitespace collapsed.</summary>
    public string Summary { get; } = Summarize(entry.Text);

    public string Details { get; } = Describe(entry);

    public string ToolTip { get; } = entry.Text.Length > 1500 ? entry.Text[..1500] + "\n…" : entry.Text;

    public HistoryOutcome Outcome => Entry.Outcome;

    public bool Matches(string filter)
        => Entry.Text.Contains(filter, StringComparison.OrdinalIgnoreCase)
           || Entry.ServerDisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
           || (Entry.Database?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);

    private static string Summarize(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("--", StringComparison.Ordinal))
                continue;
            line = Whitespace().Replace(line, " ");
            return line.Length > 200 ? line[..200] + "…" : line;
        }
        return "(comments only)";
    }

    private static string Describe(QueryHistoryEntry e)
    {
        var local = e.Timestamp.ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        string when = local.Date == today ? local.ToString("HH:mm:ss", CultureInfo.CurrentCulture)
            : local.Date == today.AddDays(-1) ? "Yesterday " + local.ToString("HH:mm", CultureInfo.CurrentCulture)
            : local.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

        var parts = new List<string> { when, e.ServerDisplayName };
        if (!string.IsNullOrEmpty(e.Database))
            parts.Add(e.Database);
        parts.Add(e.DurationMs < 1000
            ? string.Create(CultureInfo.CurrentCulture, $"{e.DurationMs} ms")
            : string.Create(CultureInfo.CurrentCulture, $"{e.DurationMs / 1000.0:0.0} s"));
        parts.Add(e.Outcome switch
        {
            HistoryOutcome.Cancelled => "cancelled",
            HistoryOutcome.Failed => "failed",
            _ => string.Create(CultureInfo.CurrentCulture, $"{e.RowCount:N0} row{(e.RowCount == 1 ? "" : "s")}"),
        });
        if (e.ExportPath is not null)
            parts.Add("→ " + Path.GetFileName(e.ExportPath));
        return string.Join(" · ", parts);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>Outcome tests for styling the status dot.</summary>
public static class HistoryConverters
{
    public static readonly IValueConverter IsSucceeded = new FuncValueConverter<HistoryOutcome, bool>(o => o == HistoryOutcome.Succeeded);
    public static readonly IValueConverter IsFailed = new FuncValueConverter<HistoryOutcome, bool>(o => o == HistoryOutcome.Failed);
    public static readonly IValueConverter IsCancelled = new FuncValueConverter<HistoryOutcome, bool>(o => o == HistoryOutcome.Cancelled);
}

/// <summary>The History panel: every execution, newest first, searchable.</summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly QueryHistoryStore _store;
    private readonly IHistoryHost _host;
    private readonly List<HistoryItemViewModel> _all = [];

    public HistoryViewModel(QueryHistoryStore store, IHistoryHost host)
    {
        _store = store;
        _host = host;
    }

    public ObservableCollection<HistoryItemViewModel> Items { get; } = [];

    [ObservableProperty]
    private string _filter = "";

    [ObservableProperty]
    private HistoryItemViewModel? _selectedItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText))]
    private bool _isEmpty = true;

    public string EmptyText => _all.Count == 0
        ? AppServices.Settings.SaveQueryHistory ? "Queries you run will appear here." : "Query history is turned off in Settings."
        : "No queries match the filter.";

    partial void OnFilterChanged(string value) => ApplyFilter();

    public async Task LoadAsync()
    {
        try
        {
            var entries = await Task.Run(_store.Load);
            _all.Clear();
            _all.AddRange(entries.Select(e => new HistoryItemViewModel(e)));
        }
        catch (Exception)
        {
            // An unreadable history file must not stop the app; start with an empty list.
            _all.Clear();
        }
        ApplyFilter();
    }

    /// <summary>Records an execution (when history is enabled). Safe to call from the UI thread.</summary>
    public async Task RecordAsync(QueryHistoryEntry entry)
    {
        if (!AppServices.Settings.SaveQueryHistory)
            return;
        QueryHistoryEntry stored;
        try
        {
            stored = await Task.Run(() => _store.Append(entry));
        }
        catch (Exception)
        {
            stored = entry; // keep it for this session even if the file is not writable
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var item = new HistoryItemViewModel(stored);
            _all.Insert(0, item);
            if (_all.Count > QueryHistoryStore.MaxEntries)
                _all.RemoveAt(_all.Count - 1);
            if (string.IsNullOrWhiteSpace(Filter) || item.Matches(Filter.Trim()))
                Items.Insert(0, item);
            while (Items.Count > _all.Count)
                Items.RemoveAt(Items.Count - 1);
            UpdateEmpty();
        });
    }

    private void ApplyFilter()
    {
        string filter = Filter.Trim();
        Items.Clear();
        foreach (var item in filter.Length == 0 ? _all : _all.Where(i => i.Matches(filter)))
            Items.Add(item);
        UpdateEmpty();
    }

    private void UpdateEmpty()
    {
        IsEmpty = Items.Count == 0;
        OnPropertyChanged(nameof(EmptyText));
    }

    [RelayCommand]
    public Task OpenAsync(HistoryItemViewModel? item)
        => (item ?? SelectedItem) is { } i ? _host.OpenHistoryAsync(i.Entry, run: false) : Task.CompletedTask;

    [RelayCommand]
    public Task RunAsync(HistoryItemViewModel? item)
        => (item ?? SelectedItem) is { } i ? _host.OpenHistoryAsync(i.Entry, run: true) : Task.CompletedTask;

    [RelayCommand]
    public void Insert(HistoryItemViewModel? item)
    {
        if ((item ?? SelectedItem) is { } i)
            _host.InsertIntoEditor(i.Entry.Text);
    }

    [RelayCommand]
    public Task CopyAsync(HistoryItemViewModel? item)
        => (item ?? SelectedItem) is { } i ? _host.CopyTextAsync(i.Entry.Text) : Task.CompletedTask;

    [RelayCommand]
    public async Task DeleteAsync(HistoryItemViewModel? item)
    {
        if ((item ?? SelectedItem) is not { } i)
            return;
        _all.Remove(i);
        Items.Remove(i);
        UpdateEmpty();
        try
        {
            await Task.Run(() => _store.Delete(i.Entry.Id));
        }
        catch (Exception ex)
        {
            await _host.ShowErrorAsync("Delete from History", ex.Message);
        }
    }

    [RelayCommand]
    public async Task ClearAsync()
    {
        if (_all.Count == 0 || !await _host.ConfirmAsync("Clear History", "Delete all query history? This cannot be undone."))
            return;
        _all.Clear();
        Items.Clear();
        UpdateEmpty();
        try
        {
            await Task.Run(_store.Clear);
        }
        catch (Exception ex)
        {
            await _host.ShowErrorAsync("Clear History", ex.Message);
        }
    }

    /// <summary>Refreshes the empty-state text after the history setting changes.</summary>
    public void SettingsChanged() => OnPropertyChanged(nameof(EmptyText));
}
