using System.Collections;
using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Quarry.App.ViewModels;
using Quarry.Core.Results;

namespace Quarry.App.Views;

/// <summary>Builds a read-only DataGrid for a result set, with typed sorting and TSV copy.</summary>
public static class ResultGridFactory
{
    public static DataGrid Create(ResultSetViewModel rs)
    {
        var grid = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            CanUserSortColumns = true,
            CanUserResizeColumns = true,
            CanUserReorderColumns = true,
            SelectionMode = DataGridSelectionMode.Extended,
            HeadersVisibility = DataGridHeadersVisibility.All,
            GridLinesVisibility = DataGridGridLinesVisibility.All,
            ClipboardCopyMode = DataGridClipboardCopyMode.None,
            VerticalAlignment = VerticalAlignment.Top,
            RowHeaderWidth = 48,
            ItemsSource = rs.Rows,
        };
        grid.Classes.Add("results");

        for (int i = 0; i < rs.Columns.Count; i++)
        {
            var column = rs.Columns[i];
            var header = new TextBlock { Text = column.DisplayName };
            ToolTip.SetTip(header, $"{column.DisplayName}  ({column.SqlTypeName}{(column.AllowsNull ? ", null" : ", not null")})");
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new ReflectionBinding($"[{i}]") { Mode = BindingMode.OneWay },
                IsReadOnly = true,
                CustomSortComparer = new ValueComparer(i),
                MaxWidth = 480,
                MinWidth = 40,
            });
        }

        grid.LoadingRow += (_, e) =>
        {
            if (e.Row.DataContext is ResultRow row)
                e.Row.Header = row.Number.ToString(CultureInfo.CurrentCulture);
        };

        grid.KeyDown += async (_, e) =>
        {
            var command = Avalonia.Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
            if (e.Key == Key.C && e.KeyModifiers.HasFlag(command))
            {
                e.Handled = true;
                await CopyAsync(grid, rs, includeHeaders: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            }
        };

        var menu = new ContextMenu();
        AddItem(menu, "Copy", () => CopyAsync(grid, rs, includeHeaders: false));
        AddItem(menu, "Copy with Headers", () => CopyAsync(grid, rs, includeHeaders: true));
        AddItem(menu, "Copy Cell", () => CopyCellAsync(grid, rs));
        menu.Items.Add(new Separator());
        AddItem(menu, "Select All", () =>
        {
            grid.SelectAll();
            return Task.CompletedTask;
        });
        grid.ContextMenu = menu;
        return grid;
    }

    private static void AddItem(ContextMenu menu, string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await action();
        menu.Items.Add(item);
    }

    /// <summary>Copies the selected rows (in result order, full values) as TSV.</summary>
    private static async Task CopyAsync(DataGrid grid, ResultSetViewModel rs, bool includeHeaders)
    {
        var rows = grid.SelectedItems.OfType<ResultRow>().OrderBy(r => r.Number).ToList();
        if (rows.Count == 0)
            return;
        var sb = new StringBuilder();
        using (var writer = new StringWriter(sb, CultureInfo.InvariantCulture))
        {
            if (includeHeaders)
            {
                writer.Write(string.Join('\t', rs.Columns.Select(c => DelimitedFormatter.Quote(c.Name, '\t'))));
                writer.Write("\r\n");
            }
            for (int i = 0; i < rows.Count; i++)
            {
                if (i > 0)
                    writer.Write("\r\n");
                DelimitedFormatter.WriteRow(writer, rows[i].Values, rs.Columns, '\t', FormatOptions.Export);
            }
        }
        await SetClipboardAsync(grid, sb.ToString());
    }

    private static async Task CopyCellAsync(DataGrid grid, ResultSetViewModel rs)
    {
        if (grid.SelectedItem is not ResultRow row || grid.CurrentColumn is not { } column)
            return;
        int index = grid.Columns.IndexOf(column);
        if (index < 0 || index >= rs.Columns.Count)
            return;
        await SetClipboardAsync(grid, ValueFormatter.Format(row.Values[index], rs.Columns[index], FormatOptions.Export));
    }

    private static async Task SetClipboardAsync(Control control, string text)
    {
        if (TopLevel.GetTopLevel(control)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    /// <summary>Sorts by the underlying value rather than its display text; NULLs first.</summary>
    private sealed class ValueComparer(int index) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            var a = (x as ResultRow)?.Values[index];
            var b = (y as ResultRow)?.Values[index];
            if (a is null)
                return b is null ? 0 : -1;
            if (b is null)
                return 1;
            if (a is byte[] ba && b is byte[] bb)
                return ((ReadOnlySpan<byte>)ba).SequenceCompareTo(bb);
            if (a.GetType() == b.GetType() && a is IComparable comparable)
                return comparable.CompareTo(b);
            return string.Compare(a.ToString(), b.ToString(), StringComparison.CurrentCulture);
        }
    }
}
