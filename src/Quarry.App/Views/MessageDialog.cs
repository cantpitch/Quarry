using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Quarry.App.Services;

namespace Quarry.App.Views;

/// <summary>Small code-built dialogs: errors, save prompts, and the Entra device code notice.</summary>
public static class MessageDialog
{
    public static async Task ShowAsync(Window owner, string title, string message)
    {
        var dialog = CreateWindow(title);
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) => dialog.Close();
        dialog.Content = Layout(new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, ok);
        await dialog.ShowDialog(owner);
    }

    public static async Task<bool> ConfirmAsync(Window owner, string title, string message)
    {
        var dialog = CreateWindow(title);
        bool confirmed = false;
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80 };
        ok.Click += (_, _) =>
        {
            confirmed = true;
            dialog.Close();
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = Layout(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, cancel, ok);
        await dialog.ShowDialog(owner);
        return confirmed;
    }

    public static async Task<SaveChoice> AskSaveAsync(Window owner, string documentName)
    {
        var dialog = CreateWindow("Quarry");
        var choice = SaveChoice.Cancel;
        Button Make(string text, SaveChoice value, bool isDefault = false, bool isCancel = false)
        {
            var button = new Button { Content = text, IsDefault = isDefault, IsCancel = isCancel, MinWidth = 80 };
            button.Click += (_, _) =>
            {
                choice = value;
                dialog.Close();
            };
            return button;
        }

        dialog.Content = Layout(
            new TextBlock { Text = $"Save changes to {documentName}?", TextWrapping = TextWrapping.Wrap },
            Make("Save", SaveChoice.Save, isDefault: true),
            Make("Don't Save", SaveChoice.Discard),
            Make("Cancel", SaveChoice.Cancel, isCancel: true));
        await dialog.ShowDialog(owner);
        return choice;
    }

    /// <summary>Non-modal window with the device-code sign-in instructions from Entra ID.</summary>
    public static void ShowDeviceCode(Window? owner, string message)
    {
        var dialog = CreateWindow("Sign in to Microsoft Entra ID");
        var copy = new Button { Content = "Copy" };
        copy.Click += async (_, _) =>
        {
            if (dialog.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(message);
        };
        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 80 };
        close.Click += (_, _) => dialog.Close();
        dialog.Content = Layout(new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, copy, close);
        if (owner is not null)
            dialog.Show(owner);
        else
            dialog.Show();
    }

    private static Window CreateWindow(string title) => new()
    {
        Title = title,
        Width = 460,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
    };

    private static Control Layout(Control body, params Button[] buttons)
    {
        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        foreach (var b in buttons)
            buttonRow.Children.Add(b);
        return new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 20,
            Children = { body, buttonRow },
        };
    }
}
