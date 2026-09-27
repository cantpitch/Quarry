using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Quarry.Parsing;

public sealed record AnalyzerOptions
{
    public static AnalyzerOptions Default { get; } = new();

    public string BatchSeparator { get; init; } = BatchSplitter.DefaultSeparator;

    public bool QuotedIdentifiers { get; init; } = true;
}

/// <summary>
/// Turns script text into a <see cref="ScriptModel"/>: batches split on GO lines, then each
/// batch parsed with ScriptDom to find its top-level statements. Statement boundaries do not
/// depend on semicolons. When a batch has syntax errors, a best-effort split is used instead.
/// </summary>
public static class ScriptAnalyzer
{
    public static ScriptModel Analyze(string text, AnalyzerOptions? options = null)
    {
        options ??= AnalyzerOptions.Default;
        var regions = SqlScanner.Scan(text);
        var batches = BatchSplitter.Split(text, regions, options.BatchSeparator);
        var lines = new LineIndex(text);
        var statements = new List<SqlStatement>();
        var errors = new List<SqlParseError>();

        for (int b = 0; b < batches.Count; b++)
        {
            var batch = batches[b];
            if (string.IsNullOrWhiteSpace(batch.Text))
                continue;

            int offset = batch.Span.Start;
            if (TryParse(batch.Text, options, out var parsed, out var parseErrors))
            {
                statements.AddRange(parsed.Select(s => new SqlStatement(s.Span.Shift(offset), b, s.Kind)));
            }
            else
            {
                foreach (var e in parseErrors)
                {
                    int docOffset = Math.Clamp(offset + e.Offset, 0, text.Length);
                    errors.Add(new SqlParseError(docOffset, lines.GetLine(docOffset), lines.GetColumn(docOffset), e.Message));
                }
                foreach (var span in FallbackSplit(batch.Text, options))
                    statements.Add(new SqlStatement(span.Span.Shift(offset), b, span.Kind));
            }
        }

        return new ScriptModel(text, regions, batches, statements, errors);
    }

    private readonly record struct LocalStatement(TextSpan Span, string Kind);

    private static bool TryParse(string sql, AnalyzerOptions options, out List<LocalStatement> statements, out IList<ParseError> errors)
    {
        var parser = new TSql170Parser(options.QuotedIdentifiers);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out errors);
        statements = [];
        if (errors.Count > 0 || fragment is not TSqlScript script)
            return false;

        foreach (var batch in script.Batches)
        {
            foreach (var statement in batch.Statements)
            {
                statements.Add(new LocalStatement(
                    new TextSpan(statement.StartOffset, statement.FragmentLength),
                    statement.GetType().Name));
            }
        }
        return true;
    }

    /// <summary>
    /// Best-effort statement split for text that does not parse. The text is cut into chunks at
    /// blank lines; chunks that parse on their own keep their real statements, and the rest are
    /// cut at semicolons.
    /// </summary>
    private static IEnumerable<LocalStatement> FallbackSplit(string sql, AnalyzerOptions options)
    {
        var regions = SqlScanner.Scan(sql);
        foreach (var chunk in SplitAtBlankLines(sql, regions))
        {
            string chunkText = sql.Substring(chunk.Start, chunk.Length);
            if (TryParse(chunkText, options, out var parsed, out _))
            {
                foreach (var s in parsed)
                    yield return s with { Span = s.Span.Shift(chunk.Start) };
                continue;
            }

            int pieceStart = chunk.Start;
            for (int i = chunk.Start; i < chunk.End; i++)
            {
                if (sql[i] == ';' && SqlScanner.RegionAt(regions, i) is null)
                {
                    if (Trim(sql, pieceStart, i + 1) is { } piece)
                        yield return new LocalStatement(piece, "Unparsed");
                    pieceStart = i + 1;
                }
            }
            if (Trim(sql, pieceStart, chunk.End) is { } last)
                yield return new LocalStatement(last, "Unparsed");
        }
    }

    private static IEnumerable<TextSpan> SplitAtBlankLines(string sql, IReadOnlyList<SqlRegion> regions)
    {
        int chunkStart = 0;
        int lineStart = 0;
        while (lineStart < sql.Length)
        {
            int lineEnd = sql.IndexOf('\n', lineStart);
            int next = lineEnd < 0 ? sql.Length : lineEnd + 1;
            if (lineEnd < 0)
                lineEnd = sql.Length;

            if (IsWhitespace(sql, lineStart, lineEnd) && !SqlScanner.IsInsideRegion(regions, lineStart))
            {
                if (Trim(sql, chunkStart, lineStart) is { } chunk)
                    yield return chunk;
                chunkStart = next;
            }
            lineStart = next;
        }
        if (Trim(sql, chunkStart, sql.Length) is { } lastChunk)
            yield return lastChunk;
    }

    private static bool IsWhitespace(string s, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            if (!char.IsWhiteSpace(s[i]))
                return false;
        }
        return true;
    }

    private static TextSpan? Trim(string s, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(s[start]))
            start++;
        while (end > start && char.IsWhiteSpace(s[end - 1]))
            end--;
        return end > start ? TextSpan.FromBounds(start, end) : null;
    }
}
