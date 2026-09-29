using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Quarry.App.Services;

/// <summary>Enumerates installed font families and applies the font settings.</summary>
public static class FontCatalog
{
    private static readonly Lazy<IReadOnlyList<string>> _all = new(() =>
        FontManager.Current.SystemFonts
            .Select(f => f.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList());

    private static readonly Lazy<IReadOnlyList<string>> _monospace = new(() => _all.Value.Where(IsMonospace).ToList());

    public static IReadOnlyList<string> All => _all.Value;

    public static IReadOnlyList<string> Monospace => _monospace.Value;

    /// <summary>The editor and text output font: the chosen family, or the built-in monospace stack.</summary>
    public static FontFamily Editor(string name)
        => name.Length > 0 ? new FontFamily(name)
            : Avalonia.Application.Current is { } app && app.Resources.TryGetResource("MonoFont", null, out var mono) && mono is FontFamily family ? family : FontFamily.Default;

    /// <summary>The grid font: the chosen family, or the application default.</summary>
    public static FontFamily Grid(string name)
        => name.Length == 0 ? FontFamily.Default : new FontFamily(name);

    /// <summary>Monospaced when narrow and wide glyphs share an advance width.</summary>
    private static bool IsMonospace(string name)
    {
        try
        {
            var typeface = new Typeface(name);
            double Width(string text) => new TextLayout(text, typeface, 16, null).Width;
            double narrow = Width("iiiiiiiiii");
            return narrow > 0 && narrow == Width("WWWWWWWWWW") && narrow == Width("mmmmmmmmmm") && narrow == Width("..........");
        }
        catch (Exception)
        {
            return false;
        }
    }
}
