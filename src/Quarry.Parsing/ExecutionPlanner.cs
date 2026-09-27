namespace Quarry.Parsing;

/// <summary>
/// One round trip to the server: the text of a batch (or single statement) and where it came from.
/// </summary>
/// <param name="Span">Document range of <paramref name="Text"/>.</param>
/// <param name="StartLine">Zero-based document line of the first character, used to map server error line numbers.</param>
/// <param name="RepeatCount">How many times to execute (from "GO n").</param>
public sealed record ExecutionUnit(string Text, TextSpan Span, int StartLine, int RepeatCount)
{
    /// <summary>Converts a 1-based line number reported by SQL Server for this unit into a zero-based document line.</summary>
    public int MapServerLine(int serverLine) => StartLine + Math.Max(serverLine, 1) - 1;
}

/// <summary>Decides what text to send for Run Script, Run Query and selections.</summary>
public static class ExecutionPlanner
{
    /// <summary>Every batch, in order. Batches holding only whitespace or comments are skipped.</summary>
    public static IReadOnlyList<ExecutionUnit> ForScript(ScriptModel model)
    {
        var units = new List<ExecutionUnit>();
        for (int i = 0; i < model.Batches.Count; i++)
        {
            var batch = model.Batches[i];
            if (!model.StatementsIn(i).Any())
                continue;
            units.Add(new ExecutionUnit(batch.Text, batch.Span, model.Lines.GetLine(batch.Span.Start), batch.RepeatCount));
        }
        return units;
    }

    /// <summary>The selected text, still split on GO lines.</summary>
    public static IReadOnlyList<ExecutionUnit> ForSelection(ScriptModel model, TextSpan selection, AnalyzerOptions? options = null)
    {
        options ??= AnalyzerOptions.Default;
        string text = model.Text.Substring(selection.Start, selection.Length);
        var units = new List<ExecutionUnit>();
        foreach (var batch in BatchSplitter.Split(text, options.BatchSeparator))
        {
            if (string.IsNullOrWhiteSpace(batch.Text))
                continue;
            var span = batch.Span.Shift(selection.Start);
            units.Add(new ExecutionUnit(batch.Text, span, model.Lines.GetLine(span.Start), batch.RepeatCount));
        }
        return units;
    }

    /// <summary>The statement Run Query would execute for the caret, or null when there is none.</summary>
    public static ExecutionUnit? ForQuery(ScriptModel model, int caret)
    {
        if (StatementAt(model, caret) is not { } statement)
            return null;
        var span = statement.Span;
        return new ExecutionUnit(model.Text.Substring(span.Start, span.Length), span, model.Lines.GetLine(span.Start), 1);
    }

    /// <summary>
    /// Finds the top-level statement for the caret. A statement containing the caret (or ending
    /// right at it) wins. Otherwise the preceding statement is used when no blank line separates
    /// it from the caret, then the following statement under the same rule, then whichever exists.
    /// Only statements in the caret's own batch are considered.
    /// </summary>
    public static SqlStatement? StatementAt(ScriptModel model, int caret)
    {
        if (model.Batches.Count == 0)
            return null;
        caret = Math.Clamp(caret, 0, model.Text.Length);
        var statements = model.StatementsIn(model.BatchIndexAt(caret)).ToList();
        if (statements.Count == 0)
            return null;

        foreach (var s in statements)
        {
            if (s.Span.Contains(caret))
                return s;
        }
        foreach (var s in statements)
        {
            if (s.Span.End == caret)
                return s;
        }

        var previous = statements.LastOrDefault(s => s.Span.End <= caret);
        var next = statements.FirstOrDefault(s => s.Span.Start > caret);

        if (previous is not null && !ContainsBlankLine(model.Text, previous.Span.End, caret))
            return previous;
        if (next is not null && !ContainsBlankLine(model.Text, caret, next.Span.Start))
            return next;
        return next ?? previous;
    }

    /// <summary>True when [from, to) contains a whitespace-only line, i.e. two line breaks with only whitespace between.</summary>
    internal static bool ContainsBlankLine(string text, int from, int to)
    {
        int breaks = 0;
        for (int i = from; i < to; i++)
        {
            char c = text[i];
            if (c == '\n')
            {
                if (++breaks == 2)
                    return true;
            }
            else if (!char.IsWhiteSpace(c))
            {
                breaks = 0;
            }
        }
        return false;
    }
}
