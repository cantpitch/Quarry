using Avalonia.Controls;
using Avalonia.Input;
using Quarry.App.ViewModels;

namespace Quarry.App.Views;

public partial class ConnectDialog : Window
{
    public ConnectDialog()
    {
        InitializeComponent();
    }

    private void OnProfileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ConnectDialogViewModel vm && vm.ConnectCommand.CanExecute(null))
            vm.ConnectCommand.Execute(null);
    }
}
