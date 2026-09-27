using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Quarry.App.Views;
using Quarry.Core.Connections;

namespace Quarry.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            window.Opened += async (_, _) => await window.StartupAsync();

            EntraAuthentication.Configure(message =>
            {
                Dispatcher.UIThread.Post(() => MessageDialog.ShowDeviceCode(desktop.MainWindow, message));
                return Task.CompletedTask;
            });
        }

        base.OnFrameworkInitializationCompleted();
    }
}
