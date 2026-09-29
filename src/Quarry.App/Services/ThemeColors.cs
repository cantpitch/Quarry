using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Quarry.Core.Settings;

namespace Quarry.App.Services;

/// <summary>Pushes the configured pane colors into the application's theme resources.</summary>
public static class ThemeColors
{
    public static bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    public static Color Parse(string value, string fallback)
        => Color.TryParse(value, out var color) ? color : Color.Parse(fallback);

    public static string Format(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static void Apply(AppSettings s)
    {
        if (Application.Current is not { } app)
            return;
        Set(app, ThemeVariant.Light, s.EditorBackgroundLight, "#FFFFFF", s.PaneBackgroundLight, "#F5F5F7");
        Set(app, ThemeVariant.Dark, s.EditorBackgroundDark, "#1E1E1E", s.PaneBackgroundDark, "#202024");
    }

    private static void Set(Application app, ThemeVariant variant, string editor, string editorDefault, string pane, string paneDefault)
    {
        if (app.Resources.ThemeDictionaries[variant] is not ResourceDictionary dictionary)
            return;
        dictionary["EditorBackground"] = new SolidColorBrush(Parse(editor, editorDefault));
        dictionary["ContentPaneBackground"] = new SolidColorBrush(Parse(pane, paneDefault));
    }
}
