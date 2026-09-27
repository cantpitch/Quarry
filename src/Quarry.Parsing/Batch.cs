namespace Quarry.Parsing;

/// <summary>
/// One batch of a script: the text between batch separators (GO lines).
/// <see cref="Span"/> is in document offsets and excludes the separator line.
/// </summary>
public sealed record Batch(TextSpan Span, string Text, int RepeatCount)
{
    /// <summary>The separator line that ends this batch, if any.</summary>
    public TextSpan? SeparatorSpan { get; init; }

    /// <summary>The batch plus its trailing separator line: the part of the document the batch owns.</summary>
    public TextSpan OwnedSpan => SeparatorSpan is { } sep ? TextSpan.FromBounds(Span.Start, sep.End) : Span;
}
