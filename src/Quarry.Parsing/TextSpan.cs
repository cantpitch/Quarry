namespace Quarry.Parsing;

/// <summary>A half-open range [Start, End) of character offsets in a document.</summary>
public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;

    public bool IsEmpty => Length == 0;

    public static TextSpan FromBounds(int start, int end) => new(start, end - start);

    public bool Contains(int offset) => offset >= Start && offset < End;

    public TextSpan Shift(int delta) => new(Start + delta, Length);
}
