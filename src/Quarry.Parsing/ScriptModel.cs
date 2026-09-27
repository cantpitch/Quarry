namespace Quarry.Parsing;

/// <summary>A top-level statement of a batch, in document offsets.</summary>
/// <param name="Kind">The ScriptDom statement type name (e.g. "SelectStatement"), or "Unparsed" for fallback splits.</param>
public sealed record SqlStatement(TextSpan Span, int BatchIndex, string Kind);

/// <summary>A syntax error, positioned in the document. Line and column are zero-based.</summary>
public sealed record SqlParseError(int Offset, int Line, int Column, string Message);

/// <summary>The analysis of a whole script: batches, top-level statements and syntax errors.</summary>
public sealed class ScriptModel
{
    internal ScriptModel(
        string text,
        IReadOnlyList<SqlRegion> regions,
        IReadOnlyList<Batch> batches,
        IReadOnlyList<SqlStatement> statements,
        IReadOnlyList<SqlParseError> errors)
    {
        Text = text;
        Regions = regions;
        Batches = batches;
        Statements = statements;
        Errors = errors;
        Lines = new LineIndex(text);
    }

    public string Text { get; }

    public IReadOnlyList<SqlRegion> Regions { get; }

    public IReadOnlyList<Batch> Batches { get; }

    /// <summary>All top-level statements, in document order.</summary>
    public IReadOnlyList<SqlStatement> Statements { get; }

    public IReadOnlyList<SqlParseError> Errors { get; }

    public LineIndex Lines { get; }

    public IEnumerable<SqlStatement> StatementsIn(int batchIndex)
        => Statements.Where(s => s.BatchIndex == batchIndex);

    /// <summary>Index of the batch that owns <paramref name="offset"/>, including its separator line.</summary>
    public int BatchIndexAt(int offset)
    {
        for (int i = 0; i < Batches.Count; i++)
        {
            if (offset < Batches[i].OwnedSpan.End)
                return i;
        }
        return Batches.Count - 1;
    }
}
