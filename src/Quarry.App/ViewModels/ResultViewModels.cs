using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Quarry.Core.Execution;
using Quarry.Core.Results;

namespace Quarry.App.ViewModels;

/// <summary>An ObservableCollection that can add many items with a single Reset notification.</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void AddRange(IEnumerable<T> items)
    {
        foreach (var item in items)
            Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>One grid row. The indexer formats lazily, so only visible cells pay for formatting.</summary>
public sealed class ResultRow(object?[] values, IReadOnlyList<ColumnInfo> columns, int number)
{
    public object?[] Values { get; } = values;

    public int Number { get; } = number;

    public string this[int index] => ValueFormatter.Format(Values[index], columns[index]);
}

public sealed class ResultSetViewModel(int index, IReadOnlyList<ColumnInfo> columns)
{
    public int Index { get; } = index;

    public ResultSet Model { get; } = new(columns);

    public BulkObservableCollection<ResultRow> Rows { get; } = [];

    public IReadOnlyList<ColumnInfo> Columns => Model.Columns;

    public void AddRows(IReadOnlyList<object?[]> rows)
    {
        int start = Model.Rows.Count;
        Model.Rows.AddRange(rows);
        Rows.AddRange(rows.Select((r, i) => new ResultRow(r, Model.Columns, start + i + 1)));
    }
}

public sealed record MessageViewModel(MessageKind Kind, string Text, int? DocumentLine)
{
    public bool IsNavigable => DocumentLine is not null;
}
