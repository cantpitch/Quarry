using System.Text.RegularExpressions;

namespace Quarry.Parsing;

/// <summary>
/// Splits a script into batches on separator lines, following the sqlcmd/SSMS rules:
/// the separator must be alone on its line (surrounding whitespace allowed), may carry a
/// repeat count ("GO 5") and a trailing line comment, and is ignored inside strings,
/// comments and delimited identifiers.
/// </summary>
public static partial class BatchSplitter
{
    public const string DefaultSeparator = "GO";

    public static IReadOnlyList<Batch> Split(string text, string separator = DefaultSeparator)
        => Split(text, SqlScanner.Scan(text), separator);

    public static IReadOnlyList<Batch> Split(string text, IReadOnlyList<SqlRegion> regions, string separator = DefaultSeparator)
    {
        var pattern = string.Equals(separator, DefaultSeparator, StringComparison.OrdinalIgnoreCase)
            ? GoLine()
            : BuildPattern(separator);
        var batches = new List<Batch>();
        int batchStart = 0;
        int lineStart = 0;

        while (true)
        {
            int lineEnd = lineStart;
            while (lineEnd < text.Length && text[lineEnd] != '\n' && text[lineEnd] != '\r')
                lineEnd++;
            int nextLineStart = lineEnd;
            if (nextLineStart < text.Length && text[nextLineStart] == '\r')
                nextLineStart++;
            if (nextLineStart < text.Length && text[nextLineStart] == '\n')
                nextLineStart++;

            if (!SqlScanner.IsInsideRegion(regions, lineStart))
            {
                var match = pattern.Match(text[lineStart..lineEnd]);
                if (match.Success)
                {
                    int repeat = 1;
                    if (match.Groups["count"].Success && !int.TryParse(match.Groups["count"].Value, out repeat))
                        repeat = 1;
                    batches.Add(MakeBatch(text, batchStart, lineStart, Math.Max(repeat, 1))
                        with { SeparatorSpan = TextSpan.FromBounds(lineStart, nextLineStart) });
                    batchStart = nextLineStart;
                }
            }

            if (nextLineStart == lineEnd)
                break; // no line break: this was the last line
            lineStart = nextLineStart;
        }

        if (batchStart < text.Length || batches.Count == 0)
            batches.Add(MakeBatch(text, batchStart, text.Length, 1));

        return batches;
    }

    private static Batch MakeBatch(string text, int start, int end, int repeat)
        => new(TextSpan.FromBounds(start, end), text[start..end], repeat);

    private static Regex BuildPattern(string separator)
        => new($@"^[ \t]*{Regex.Escape(separator)}(?:[ \t]+(?<count>\d+))?[ \t]*(?:--.*)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [GeneratedRegex(@"^[ \t]*GO(?:[ \t]+(?<count>\d+))?[ \t]*(?:--.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GoLine();
}
