using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Quarry.App.ViewModels;
using Quarry.Core.Execution;

namespace Quarry.App.Converters;

internal static class ResourceLookup
{
    public static object? Find(string key)
    {
        var app = Application.Current;
        if (app is null)
            return null;
        return app.TryGetResource(key, app.ActualThemeVariant, out var value) ? value : null;
    }
}

/// <summary>Maps a <see cref="NodeIcon"/> to its geometry resource ("Geo.*").</summary>
public sealed class IconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is NodeIcon icon ? ResourceLookup.Find($"Geo.{icon}") as Geometry : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps a <see cref="NodeIcon"/> to its themed colour ("Icon.*").</summary>
public sealed class IconBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not NodeIcon icon)
            return null;
        string key = icon switch
        {
            NodeIcon.Server => "Icon.Server",
            NodeIcon.Database => "Icon.Database",
            NodeIcon.Folder => "Icon.Folder",
            NodeIcon.Table => "Icon.Table",
            NodeIcon.View or NodeIcon.Synonym => "Icon.View",
            NodeIcon.Procedure or NodeIcon.Function => "Icon.Code",
            NodeIcon.Key => "Icon.Key",
            NodeIcon.Error => "Icon.Error",
            _ => "Icon.Column",
        };
        return ResourceLookup.Find(key) as IBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class MessageBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is MessageKind.Error ? ResourceLookup.Find("ErrorText") as IBrush
            : value is MessageKind.Status ? ResourceLookup.Find("StatusText") as IBrush
            : AvaloniaProperty.UnsetValue;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
