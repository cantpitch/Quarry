using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Quarry.Parsing;

namespace Quarry.App.Editor;

/// <summary>Tints the statement that Run Query would execute.</summary>
public sealed class CurrentStatementRenderer : IBackgroundRenderer
{
    public TextSpan? Span { get; set; }

    public IBrush Brush { get; set; } = new SolidColorBrush(Color.FromArgb(24, 64, 128, 255));

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Span is not { } span || span.IsEmpty || textView.Document is null || span.End > textView.Document.TextLength)
            return;

        var builder = new BackgroundGeometryBuilder { CornerRadius = 2, AlignToWholePixels = true };
        builder.AddSegment(textView, new TextSegment { StartOffset = span.Start, Length = span.Length });
        if (builder.CreateGeometry() is { } geometry)
            drawingContext.DrawGeometry(Brush, null, geometry);
    }
}

/// <summary>Draws wavy underlines under syntax errors.</summary>
public sealed class ErrorRenderer : IBackgroundRenderer
{
    private readonly Pen _pen = new(new SolidColorBrush(Color.FromRgb(0xE5, 0x3E, 0x30)), 1);

    public IReadOnlyList<TextSpan> Errors { get; set; } = [];

    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Errors.Count == 0 || textView.Document is null || !textView.VisualLinesValid)
            return;

        foreach (var span in Errors)
        {
            if (span.End > textView.Document.TextLength)
                continue;
            var segment = new TextSegment { StartOffset = span.Start, Length = Math.Max(span.Length, 1) };
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment, false))
                DrawWave(drawingContext, rect.BottomLeft, Math.Max(rect.Width, 4));
        }
    }

    private void DrawWave(DrawingContext context, Point start, double width)
    {
        const double step = 2;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(start.X, start.Y - 1), false);
            bool up = true;
            for (double x = start.X + step; x <= start.X + width; x += step)
            {
                ctx.LineTo(new Point(x, start.Y - (up ? 3 : 1)));
                up = !up;
            }
            ctx.EndFigure(false);
        }
        context.DrawGeometry(null, _pen, geometry);
    }
}
