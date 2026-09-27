using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Quarry.Parsing.Formatting;

/// <param name="Text">The formatted script.</param>
/// <param name="SkippedBatches">Batches left unchanged because they have syntax errors (or could not be formatted safely).</param>
public sealed record SqlFormatResult(string Text, int SkippedBatches, IReadOnlyList<string> Problems)
{
    public bool Changed { get; init; }
}

/// <summary>
/// Formats T-SQL scripts. Only whitespace and keyword case ever change: every batch is re-lexed
/// and re-parsed after formatting, and a batch whose tokens differ in any other way is left as it was.
/// </summary>
public static class SqlFormatter
{
    public static SqlFormatResult Format(string text, SqlFormatOptions? options = null)
    {
        options ??= SqlFormatOptions.Default;
        string newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var problems = new List<string>();
        int skipped = 0;
        var sb = new StringBuilder();
        var batches = BatchSplitter.Split(text, options.BatchSeparator);

        for (int b = 0; b < batches.Count; b++)
        {
            var batch = batches[b];
            string formatted = "";
            if (!string.IsNullOrWhiteSpace(batch.Text))
            {
                if (TryFormatBatch(batch.Text, options, newline, out var result, out var problem))
                {
                    formatted = result;
                }
                else
                {
                    formatted = batch.Text.Trim();
                    skipped++;
                    problems.Add(batches.Count > 1 ? $"Batch {b + 1}: {problem}" : problem);
                }
            }

            if (formatted.Length > 0)
            {
                if (sb.Length > 0 && LeadingWhitespaceHasBlankLine(batch.Text))
                    sb.Append(newline);
                sb.Append(formatted);
            }

            if (batch.SeparatorSpan is { } separator)
            {
                if (formatted.Length > 0)
                    sb.Append(newline);
                sb.Append(CaseSeparator(text.Substring(separator.Start, separator.Length).Trim(), options));
                sb.Append(newline);
            }
        }

        string output = sb.ToString().TrimEnd();
        if (text.EndsWith('\n'))
            output += newline;
        return new SqlFormatResult(output, skipped, problems) { Changed = output != text };
    }

    /// <summary>Applies keyword case to the separator word of a "GO [n] [-- comment]" line.</summary>
    private static string CaseSeparator(string line, SqlFormatOptions options)
    {
        int length = options.BatchSeparator.Length;
        if (options.KeywordCase == KeywordCase.Preserve || line.Length < length)
            return line;
        string word = line[..length];
        return (options.KeywordCase == KeywordCase.Upper ? word.ToUpperInvariant() : word.ToLowerInvariant()) + line[length..];
    }

    private static bool LeadingWhitespaceHasBlankLine(string batchText)
    {
        for (int i = 0; i < batchText.Length && char.IsWhiteSpace(batchText[i]); i++)
        {
            if (batchText[i] == '\n')
                return true;
        }
        return false;
    }

    private static TSqlParser NewParser() => new TSql170Parser(initialQuotedIdentifiers: true);

    private static bool TryFormatBatch(string sql, SqlFormatOptions options, string newline, out string formatted, out string problem)
    {
        formatted = "";
        problem = "";
        using (var reader = new StringReader(sql))
        {
            var fragment = NewParser().Parse(reader, out var errors);
            if (errors.Count > 0 || fragment is not TSqlScript script)
            {
                problem = errors.Count > 0
                    ? $"syntax error at line {errors[0].Line}: {errors[0].Message} It was left unchanged."
                    : "could not be parsed. It was left unchanged.";
                return false;
            }

            try
            {
                var writer = new LineWriter(newline);
                var stream = script.ScriptTokenStream;
                var statements = script.Batches.SelectMany(b => b.Statements).ToList();
                new StatementFormatter(stream, options, writer).FormatList(statements, 0, stream.Count - 1, 0);
                formatted = writer.ToString().Trim();
            }
            catch (Exception ex)
            {
                problem = $"could not be formatted ({ex.Message}). It was left unchanged.";
                return false;
            }
        }

        if (!SameTokens(sql, formatted))
        {
            problem = "could not be formatted safely. It was left unchanged.";
            return false;
        }
        return true;
    }

    /// <summary>True when both texts lex to the same tokens (ignoring whitespace and keyword case) and the second parses.</summary>
    internal static bool SameTokens(string original, string formatted)
    {
        var a = Significant(original, out _);
        var b = Significant(formatted, out var errors);
        if (errors > 0 || a.Count != b.Count)
            return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].TokenType != b[i].TokenType)
                return false;
            bool caseInsensitive = a[i].TokenType is not (TSqlTokenType.AsciiStringLiteral or TSqlTokenType.UnicodeStringLiteral
                or TSqlTokenType.AsciiStringOrQuotedIdentifier or TSqlTokenType.QuotedIdentifier
                or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment);
            if (!string.Equals(a[i].Text, b[i].Text, caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return false;
        }

        using var reader = new StringReader(formatted);
        NewParser().Parse(reader, out var parseErrors);
        return parseErrors.Count == 0;
    }

    private static List<TSqlParserToken> Significant(string sql, out int errorCount)
    {
        using var reader = new StringReader(sql);
        var tokens = NewParser().GetTokenStream(reader, out var errors);
        errorCount = errors.Count;
        return tokens.Where(t => t.TokenType is not (TSqlTokenType.WhiteSpace or TSqlTokenType.EndOfFile)).ToList();
    }
}

/// <summary>Formats statements: DML through <see cref="QueryFormatter"/>, blocks with indentation, the rest verbatim.</summary>
internal sealed class StatementFormatter(IList<TSqlParserToken> stream, SqlFormatOptions options, LineWriter writer)
{
    private int Indent(int indent) => indent + options.IndentSize;

    /// <summary>Formats statements inside the token region [regionStart, regionEnd], keeping comments and blank lines between them.</summary>
    public void FormatList(IList<TSqlStatement> statements, int regionStart, int regionEnd, int indent)
    {
        int cursor = regionStart;
        foreach (var statement in statements)
        {
            WriteGap(cursor, statement.FirstTokenIndex - 1, indent, beforeStatement: true);
            FormatStatement(statement, indent);
            cursor = statement.LastTokenIndex + 1;
        }
        WriteGap(cursor, regionEnd, indent, beforeStatement: false);
    }

    /// <summary>
    /// Writes the comments between statements. A comment on the same line as the previous code stays
    /// there; others get their own line. One blank line is kept where the source had any.
    /// </summary>
    private void WriteGap(int from, int to, int indent, bool beforeStatement)
    {
        int newlines = 0;
        for (int i = from; i <= to && i < stream.Count; i++)
        {
            var token = stream[i];
            switch (token.TokenType)
            {
                case TSqlTokenType.EndOfFile:
                    continue;
                case TSqlTokenType.WhiteSpace:
                    newlines += token.Text.Count(c => c == '\n');
                    continue;
            }

            bool comment = token.TokenType is TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment;
            if (!(comment && newlines == 0 && writer.HasContent))
                writer.StartLine(indent, blank: newlines >= 2);
            new QueryFormatter(stream, i, i, options, writer).FormatInline(indent);
            newlines = 0;
        }
        if (beforeStatement)
            writer.StartLine(indent, blank: newlines >= 2);
    }

    private void FormatStatement(TSqlStatement statement, int indent)
    {
        switch (statement)
        {
            case SelectStatementSnippet:
                Verbatim(statement.FirstTokenIndex, statement.LastTokenIndex, indent);
                break;
            // SET NOCOUNT ON, SET STATISTICS IO ON, SET TRANSACTION ISOLATION LEVEL READ COMMITTED:
            // every word is a keyword, though several lex as identifiers.
            case PredicateSetStatement or SetStatisticsStatement or SetTransactionIsolationLevelStatement:
                Verbatim(statement.FirstTokenIndex, statement.LastTokenIndex, indent, allKeywords: true);
                break;
            case SelectStatement or InsertStatement or UpdateStatement or DeleteStatement:
                Query(statement.FirstTokenIndex, statement.LastTokenIndex);
                break;
            case BeginEndBlockStatement block:
                FormatBlock(block, indent);
                break;
            case IfStatement ifStatement:
                FormatIf(ifStatement, indent);
                break;
            case WhileStatement loop:
                Inline(loop.FirstTokenIndex, loop.Predicate.LastTokenIndex, indent);
                WriteChild(loop.Statement, loop.Predicate.LastTokenIndex + 1, indent);
                Inline(loop.Statement.LastTokenIndex + 1, loop.LastTokenIndex, indent);
                break;
            case TryCatchStatement tryCatch:
                FormatTryCatch(tryCatch, indent);
                break;
            case ProcedureStatementBody { StatementList.Statements.Count: > 0 } procedure:
                FormatModule(procedure, procedure.StatementList.Statements, indent);
                break;
            case FunctionStatementBody { StatementList.Statements.Count: > 0 } function:
                FormatModule(function, function.StatementList.Statements, indent);
                break;
            case TriggerStatementBody { StatementList.Statements.Count: > 0 } trigger:
                FormatModule(trigger, trigger.StatementList.Statements, indent);
                break;
            case ViewStatementBody { SelectStatement: { } select } view:
                Verbatim(view.FirstTokenIndex, LastSignificantBefore(select.FirstTokenIndex, view.FirstTokenIndex), indent);
                WriteGap(LastSignificantBefore(select.FirstTokenIndex, view.FirstTokenIndex) + 1, select.FirstTokenIndex - 1, indent, beforeStatement: true);
                Query(select.FirstTokenIndex, select.LastTokenIndex);
                if (view.LastTokenIndex > select.LastTokenIndex)
                {
                    int next = NextSignificant(select.LastTokenIndex + 1, view.LastTokenIndex);
                    if (next >= 0 && stream[next].Text != ";")
                        writer.StartLine(indent, blank: false);
                    Inline(select.LastTokenIndex + 1, view.LastTokenIndex, indent);
                }
                break;
            default:
                Verbatim(statement.FirstTokenIndex, statement.LastTokenIndex, indent);
                break;
        }
    }

    private void FormatBlock(BeginEndBlockStatement block, int indent)
    {
        var statements = block.StatementList?.Statements ?? (IList<TSqlStatement>)[];
        int end = FindBlockEnd(block);
        int headerEnd = LastSignificantBefore(statements.Count > 0 ? statements[0].FirstTokenIndex : end, block.FirstTokenIndex);
        Inline(block.FirstTokenIndex, headerEnd, indent); // BEGIN [ATOMIC WITH (…)]
        FormatList(statements, headerEnd + 1, end - 1, Indent(indent));
        writer.StartLine(indent, blank: false);
        Inline(end, block.LastTokenIndex, indent); // END [;]
    }

    /// <summary>Index of the block's closing END (the last END, ignoring a trailing semicolon).</summary>
    private int FindBlockEnd(TSqlStatement block)
    {
        for (int i = block.LastTokenIndex; i >= block.FirstTokenIndex; i--)
        {
            if (stream[i].TokenType == TSqlTokenType.End)
                return i;
        }
        throw new InvalidOperationException("BEGIN without END.");
    }

    private void FormatIf(IfStatement statement, int indent)
    {
        Inline(statement.FirstTokenIndex, statement.Predicate.LastTokenIndex, indent);
        WriteChild(statement.ThenStatement, statement.Predicate.LastTokenIndex + 1, indent);
        int last = statement.ThenStatement.LastTokenIndex;

        if (statement.ElseStatement is { } elseStatement)
        {
            int elseToken = FindToken(TSqlTokenType.Else, last + 1, elseStatement.FirstTokenIndex - 1);
            WriteGap(last + 1, elseToken - 1, indent, beforeStatement: false);
            writer.StartLine(indent, blank: false);
            Inline(elseToken, elseToken, indent);
            if (elseStatement is IfStatement elseIf)
            {
                WriteGap(elseToken + 1, elseIf.FirstTokenIndex - 1, indent, beforeStatement: false);
                FormatIf(elseIf, indent); // ELSE IF … on one line
            }
            else
            {
                WriteChild(elseStatement, elseToken + 1, indent);
            }
            last = elseStatement.LastTokenIndex;
        }
        Inline(last + 1, statement.LastTokenIndex, indent);
    }

    /// <summary>The body of IF / ELSE / WHILE: a BEGIN…END block lines up with its owner, a single statement is indented.</summary>
    private void WriteChild(TSqlStatement child, int gapFrom, int indent)
    {
        int childIndent = child is BeginEndBlockStatement ? indent : Indent(indent);
        WriteGap(gapFrom, child.FirstTokenIndex - 1, childIndent, beforeStatement: true);
        FormatStatement(child, childIndent);
    }

    private void FormatTryCatch(TryCatchStatement statement, int indent)
    {
        var tryStatements = statement.TryStatements?.Statements ?? (IList<TSqlStatement>)[];
        var catchStatements = statement.CatchStatements?.Statements ?? (IList<TSqlStatement>)[];

        int beginTry = statement.FirstTokenIndex;
        int tryKeyword = NextSignificant(beginTry + 1, statement.LastTokenIndex);
        int afterTry = tryStatements.Count > 0 ? tryStatements[^1].LastTokenIndex + 1 : tryKeyword + 1;
        int endTry = FindToken(TSqlTokenType.End, afterTry, statement.LastTokenIndex);
        int endTryKeyword = NextSignificant(endTry + 1, statement.LastTokenIndex);
        int beginCatch = FindToken(TSqlTokenType.Begin, endTryKeyword + 1, statement.LastTokenIndex);
        int catchKeyword = NextSignificant(beginCatch + 1, statement.LastTokenIndex);
        int endCatch = FindBlockEnd(statement);

        Inline(beginTry, tryKeyword, indent, allKeywords: true);
        FormatList(tryStatements, tryKeyword + 1, endTry - 1, Indent(indent));
        writer.StartLine(indent, blank: false);
        Inline(endTry, endTryKeyword, indent, allKeywords: true);
        WriteGap(endTryKeyword + 1, beginCatch - 1, indent, beforeStatement: true);
        Inline(beginCatch, catchKeyword, indent, allKeywords: true);
        FormatList(catchStatements, catchKeyword + 1, endCatch - 1, Indent(indent));
        writer.StartLine(indent, blank: false);
        Inline(endCatch, statement.LastTokenIndex, indent, allKeywords: true);
    }

    /// <summary>CREATE/ALTER PROCEDURE, FUNCTION or TRIGGER: header as written, body statements formatted.</summary>
    private void FormatModule(TSqlStatement module, IList<TSqlStatement> body, int indent)
    {
        int headerEnd = LastSignificantBefore(body[0].FirstTokenIndex, module.FirstTokenIndex);
        Verbatim(module.FirstTokenIndex, headerEnd, indent);
        FormatList(body, headerEnd + 1, module.LastTokenIndex, indent);
    }

    private void Query(int first, int last)
        => new QueryFormatter(stream, first, last, options, writer).FormatQuery(writer.Column);

    private void Inline(int first, int last, int continuation, bool allKeywords = false)
    {
        if (last >= first)
            new QueryFormatter(stream, first, last, options, writer, allKeywords).FormatInline(continuation);
    }

    /// <summary>
    /// Writes a statement as it was typed, with keyword case applied and its lines shifted to the
    /// current indentation. Line breaks inside strings and comments are never touched.
    /// </summary>
    private void Verbatim(int first, int last, int indent, bool allKeywords = false)
    {
        var cased = new QueryFormatter(stream, first, last, options, writer, allKeywords).CasedTexts();
        int shift = indent - (stream[first].Column - 1);
        var sb = new StringBuilder();
        for (int i = first; i <= last; i++)
        {
            var token = stream[i];
            if (token.TokenType == TSqlTokenType.EndOfFile)
                continue;
            if (token.TokenType == TSqlTokenType.WhiteSpace && token.Text.Contains('\n'))
            {
                int lastBreak = token.Text.LastIndexOf('\n');
                string tail = token.Text[(lastBreak + 1)..];
                int width = tail.Sum(c => c == '\t' ? 4 : 1);
                sb.Append(token.Text, 0, lastBreak + 1).Append(' ', Math.Max(0, width + shift));
            }
            else
            {
                sb.Append(cased.TryGetValue(token, out var text) ? text : token.Text);
            }
        }

        if (writer.PendingNewline)
            writer.StartLine(indent, blank: false);
        writer.Write(sb.ToString(), spaceBefore: writer.HasContent);
        writer.Previous = new FormatToken(stream[last]);
        if (stream[last].TokenType == TSqlTokenType.SingleLineComment)
            writer.PendingNewline = true;
    }

    private int FindToken(TSqlTokenType type, int from, int to)
    {
        for (int i = from; i <= to; i++)
        {
            if (stream[i].TokenType == type)
                return i;
        }
        throw new InvalidOperationException($"Expected {type}.");
    }

    private int NextSignificant(int from, int to)
    {
        for (int i = from; i <= to && i < stream.Count; i++)
        {
            if (stream[i].TokenType is not (TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment or TSqlTokenType.EndOfFile))
                return i;
        }
        return -1;
    }

    /// <summary>The last code token (not whitespace or comment) before <paramref name="index"/>, at or after <paramref name="floor"/>.</summary>
    private int LastSignificantBefore(int index, int floor)
    {
        for (int i = index - 1; i > floor; i--)
        {
            if (stream[i].TokenType is not (TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment))
                return i;
        }
        return floor;
    }
}
