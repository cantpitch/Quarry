using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Quarry.App.ViewModels;

namespace Quarry.App.Views;

public partial class HistoryView : UserControl
{
    public HistoryView()
    {
        InitializeComponent();
    }

    private HistoryViewModel? ViewModel => DataContext as HistoryViewModel;

    private static HistoryItemViewModel? ItemFromSource(object? source)
        => (source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as HistoryItemViewModel;

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ItemFromSource(e.Source) is { } item && ViewModel is { } vm)
        {
            e.Handled = true;
            _ = vm.OpenAsync(item);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { SelectedItem: { } item } vm)
            return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = vm.OpenAsync(item);
        }
        else if (e.Key == Key.Delete)
        {
            e.Handled = true;
            _ = vm.DeleteAsync(item);
        }
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (ItemFromSource(e.Source) is not { } item || ViewModel is not { } vm)
            return;
        vm.SelectedItem = item;

        var menu = new ContextMenu();
        void Add(string header, Func<Task> action)
        {
            var menuItem = new MenuItem { Header = header };
            menuItem.Click += async (_, _) => await action();
            menu.Items.Add(menuItem);
        }

        Add("Open in New Tab", () => vm.OpenAsync(item));
        Add("Run in New Tab", () => vm.RunAsync(item));
        Add("Insert at Cursor", () =>
        {
            vm.Insert(item);
            return Task.CompletedTask;
        });
        Add("Copy SQL", () => vm.CopyAsync(item));
        menu.Items.Add(new Separator());
        Add("Delete from History", () => vm.DeleteAsync(item));
        menu.Open(List);
        e.Handled = true;
    }
}
