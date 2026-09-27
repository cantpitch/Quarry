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
                var dataTypes = DataTypeNames.Collect(script);
                new StatementFormatter(stream, options, writer, dataTypes).FormatList(statements, 0, stream.Count - 1, 0);
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
internal sealed class StatementFormatter(IList<TSqlParserToken> stream, SqlFormatOptions options, LineWriter writer,
    IReadOnlySet<TSqlParserToken> dataTypes)
{
    private QueryFormatter Formatter(int first, int last, LineWriter? output = null, bool allKeywords = false, IReadOnlySet<TSqlParserToken>? keywords = null)
        => new(stream, first, last, options, output ?? writer, allKeywords, keywords, dataTypes);

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
            Formatter(i, i).FormatInline(indent);
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
            case CreateTableStatement { Definition: { } definition } create:
                FormatTable(statement, create.SchemaObjectName, definition, indent);
                break;
            case DeclareTableVariableStatement { Body.Definition: { } definition } declareTable:
                FormatTable(statement, declareTable.Body.VariableName, definition, indent);
                break;
            case CreateTypeTableStatement { Definition: { } definition } createType:
                FormatTable(statement, createType.Name, definition, indent);
                break;
            case DeclareVariableStatement { Declarations.Count: > 0 } declare:
                Formatter(declare.FirstTokenIndex, declare.LastTokenIndex)
                    .FormatList(stream[declare.Declarations[0].FirstTokenIndex], options.ListLayout == ListLayout.OnePerLine);
                break;
            // Written inline so that CASE expressions and subqueries in them are laid out.
            case SetVariableStatement { CursorDefinition: null } or ReturnStatement or PrintStatement:
                Inline(statement.FirstTokenIndex, statement.LastTokenIndex, Indent(indent));
                break;
            case BeginEndBlockStatement block:
                FormatBlock(block, indent);
                break;
            case IfStatement ifStatement:
                FormatIf(ifStatement, indent);
                break;
            case WhileStatement loop:
            {
                int line = writer.LineNumber;
                Conditions(loop.FirstTokenIndex, loop.Predicate, indent);
                WriteChild(loop.Statement, loop.Predicate.LastTokenIndex + 1, indent, ownerLine: line);
                Inline(loop.Statement.LastTokenIndex + 1, loop.LastTokenIndex, indent);
                break;
            }
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
        int line = writer.LineNumber;
        Conditions(statement.FirstTokenIndex, statement.Predicate, indent);
        WriteChild(statement.ThenStatement, statement.Predicate.LastTokenIndex + 1, indent, ownerLine: line);
        int last = statement.ThenStatement.LastTokenIndex;

        if (statement.ElseStatement is { } elseStatement)
        {
            int elseToken = FindToken(TSqlTokenType.Else, last + 1, elseStatement.FirstTokenIndex - 1);
            bool sameLine = options.BlockLayout == BlockLayout.SameLine && statement.ThenStatement is BeginEndBlockStatement
                            && !HasComment(last + 1, elseToken - 1);
            WriteGap(last + 1, elseToken - 1, indent, beforeStatement: false);
            if (!sameLine)
                writer.StartLine(indent, blank: false);
            Inline(elseToken, elseToken, indent);
            if (elseStatement is IfStatement elseIf)
            {
                WriteGap(elseToken + 1, elseIf.FirstTokenIndex - 1, indent, beforeStatement: false);
                FormatIf(elseIf, indent); // ELSE IF … on one line
            }
            else
            {
                WriteChild(elseStatement, elseToken + 1, indent, ownerLine: writer.LineNumber);
            }
            last = elseStatement.LastTokenIndex;
        }
        Inline(last + 1, statement.LastTokenIndex, indent);
    }

    /// <summary>The body of IF / ELSE / WHILE: a BEGIN…END block is placed per <see cref="BlockLayout"/>, a single statement is indented.</summary>
    /// <param name="ownerLine">The line the IF / ELSE / WHILE started on: BEGIN only joins it when the condition fitted on it.</param>
    private void WriteChild(TSqlStatement child, int gapFrom, int indent, int ownerLine)
    {
        bool block = child is BeginEndBlockStatement;
        if (block && options.BlockLayout == BlockLayout.SameLine && writer.LineNumber == ownerLine && !writer.PendingNewline
            && !HasComment(gapFrom, child.FirstTokenIndex - 1))
        {
            FormatStatement(child, indent); // IF … BEGIN
            return;
        }
        int childIndent = block && options.BlockLayout != BlockLayout.Indented ? indent : Indent(indent);
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

    /// <summary>
    /// CREATE TABLE, DECLARE @t TABLE and CREATE TYPE … AS TABLE: one column, constraint or index per
    /// line inside the parentheses, with column names, data types and the rest lined up.
    /// </summary>
    private void FormatTable(TSqlStatement statement, TSqlFragment name, TableDefinition definition, int indent)
    {
        var elements = new List<(TSqlFragment Fragment, int First, int Last)>();
        foreach (var fragment in definition.ColumnDefinitions.Concat<TSqlFragment>(definition.TableConstraints).Concat(definition.Indexes))
            elements.Add((fragment, fragment.FirstTokenIndex, fragment.LastTokenIndex));
        if (definition.SystemTimePeriod is { } period)
        {
            // The fragment starts at its first column; "PERIOD FOR SYSTEM_TIME (" comes before it.
            int periodWord = period.FirstTokenIndex;
            while (periodWord > statement.FirstTokenIndex && !stream[periodWord].Text.Equals("PERIOD", StringComparison.OrdinalIgnoreCase))
                periodWord--;
            elements.Add((period, periodWord, period.LastTokenIndex));
        }
        elements.Sort((a, b) => a.First.CompareTo(b.First));

        int open = elements.Count > 0 ? LastSignificantBefore(elements[0].First, statement.FirstTokenIndex) : -1;
        int close = elements.Count > 0 ? NextSignificant(elements[^1].Last + 1, statement.LastTokenIndex) : -1;
        var commas = new int[elements.Count];
        bool understood = open > statement.FirstTokenIndex && stream[open].Text == "(" && close >= 0 && stream[close].Text == ")";
        for (int i = 1; understood && i < elements.Count; i++)
        {
            // Between two elements there must be exactly one comma (and whitespace or comments).
            int comma = NextSignificant(elements[i - 1].Last + 1, elements[i].First - 1);
            understood = comma >= 0 && stream[comma].Text == ","
                         && NextSignificant(comma + 1, elements[i].First - 1) < 0;
            commas[i] = comma;
        }
        if (!understood)
        {
            Verbatim(statement.FirstTokenIndex, statement.LastTokenIndex, indent);
            return;
        }

        // Widths of the name and data type columns, from their formatted text.
        int nameWidth = 0, typeWidth = 0;
        if (options.AlignColumnDefinitions)
        {
            foreach (var column in definition.ColumnDefinitions)
            {
                nameWidth = Math.Max(nameWidth, Width(column.ColumnIdentifier.FirstTokenIndex, column.ColumnIdentifier.LastTokenIndex));
                if (column.DataType is { } type)
                    typeWidth = Math.Max(typeWidth, Width(type.FirstTokenIndex, type.LastTokenIndex));
            }
        }

        Inline(statement.FirstTokenIndex, name.FirstTokenIndex - 1, indent, allKeywords: true); // CREATE TABLE / CREATE TYPE / DECLARE
        Inline(name.FirstTokenIndex, open - 1, indent); // name [AS TABLE]
        if (options.TableParenthesisOnOwnLine)
            writer.StartLine(indent, blank: false);
        else if (writer.PendingNewline)
            writer.StartLine(indent, blank: false);
        writer.Write("(", spaceBefore: writer.HasContent);
        writer.Previous = new FormatToken(stream[open]);

        int item = Indent(indent);
        bool leading = options.CommaPlacement == CommaPlacement.Leading && item >= 2;
        for (int i = 0; i < elements.Count; i++)
        {
            var element = elements[i];
            if (i == 0)
            {
                WriteGap(open + 1, element.First - 1, item, beforeStatement: true);
            }
            else if (leading)
            {
                WriteGap(elements[i - 1].Last + 1, commas[i] - 1, item, beforeStatement: true);
                writer.StartLine(item - 2, blank: false);
                Inline(commas[i], commas[i], item);
                WriteGap(commas[i] + 1, element.First - 1, item, beforeStatement: false);
            }
            else
            {
                Inline(elements[i - 1].Last + 1, commas[i], item); // comments before the comma, then the comma
                WriteGap(commas[i] + 1, element.First - 1, item, beforeStatement: true);
            }

            if (element.Fragment is ColumnDefinition column)
                WriteColumn(column, item, nameWidth, typeWidth);
            else if (element.Fragment is SystemTimePeriodDefinition)
            {
                int columns = FindToken(TSqlTokenType.LeftParenthesis, element.First, element.Last);
                Inline(element.First, columns - 1, Indent(item), allKeywords: true); // PERIOD FOR SYSTEM_TIME
                Inline(columns, element.Last, Indent(item));
            }
            else
                Inline(element.First, element.Last, Indent(item));
        }

        WriteGap(elements[^1].Last + 1, close - 1, item, beforeStatement: false);
        writer.StartLine(indent, blank: false);
        Inline(close, statement.LastTokenIndex, indent); // ) [WITH (…)] [ON …] [;]
    }

    /// <summary>Name, data type and the rest of a column definition, each starting at its own column.</summary>
    private void WriteColumn(ColumnDefinition column, int item, int nameWidth, int typeWidth)
    {
        var name = column.ColumnIdentifier;
        Inline(name.FirstTokenIndex, name.LastTokenIndex, Indent(item));
        int restStart = (column.DataType?.LastTokenIndex ?? name.LastTokenIndex) + 1;
        bool hasRest = restStart <= column.LastTokenIndex && NextNonWhitespace(restStart, column.LastTokenIndex) >= 0;

        bool align = options.AlignColumnDefinitions;
        if (column.DataType is { } type)
        {
            if (align)
                writer.PadTo(item + nameWidth + 1);
            Inline(type.FirstTokenIndex, type.LastTokenIndex, Indent(item));
            if (hasRest && align)
                writer.PadTo(item + nameWidth + 1 + typeWidth + 1);
        }
        else if (hasRest && align)
        {
            writer.PadTo(item + nameWidth + 1); // computed column: AS (…)
        }
        if (hasRest)
            Formatter(restStart, column.LastTokenIndex, keywords: ColumnOptionKeywords(column, restStart))
                .FormatInline(Indent(item));
    }

    /// <summary>
    /// Column options that lex as identifiers (PERSISTED, SPARSE, GENERATED ALWAYS AS ROW START, …).
    /// A word only counts when the syntax tree has that option, and it is not a name: not inside
    /// parentheses, not after CONSTRAINT or REFERENCES, and not part of a dotted name.
    /// </summary>
    private HashSet<TSqlParserToken> ColumnOptionKeywords(ColumnDefinition column, int restStart)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (column.IsPersisted)
            words.Add("PERSISTED");
        if (column.IsRowGuidCol)
            words.Add("ROWGUIDCOL");
        if (column.IsHidden)
            words.Add("HIDDEN");
        if (column.IsMasked)
            words.Add("MASKED");
        if (column.StorageOptions is { } storage)
        {
            if (storage.SparseOption != SparseColumnOption.None)
                words.UnionWith(["SPARSE", "COLUMN_SET", "ALL_SPARSE_COLUMNS"]);
            if (storage.IsFileStream)
                words.Add("FILESTREAM");
        }
        if (column.GeneratedAlways is not null)
            words.UnionWith(["GENERATED", "ALWAYS", "ROW", "START", "TRANSACTION_ID", "SEQUENCE_NUMBER"]);

        var keywords = new HashSet<TSqlParserToken>(ReferenceEqualityComparer.Instance);
        int depth = 0;
        TSqlParserToken? previous = null;
        for (int i = restStart; i <= column.LastTokenIndex; i++)
        {
            var token = stream[i];
            if (token.TokenType is TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment)
                continue;
            if (token.Text == "(")
                depth++;
            else if (token.Text == ")")
                depth--;
            else if (depth == 0 && token.TokenType == TSqlTokenType.Identifier && words.Contains(token.Text)
                     && previous?.TokenType is not (TSqlTokenType.Constraint or TSqlTokenType.References)
                     && previous?.Text != "." && stream[i + 1].Text != ".")
                keywords.Add(token);
            previous = token;
        }
        return keywords;
    }

    /// <summary>Length of the formatted text of a token range, or 0 if it spans lines.</summary>
    private int Width(int first, int last)
    {
        var scratch = new LineWriter("\n");
        Formatter(first, last, scratch).FormatInline(0);
        string text = scratch.ToString();
        return text.Contains('\n') ? 0 : text.Length;
    }

    private int NextNonWhitespace(int from, int to)
    {
        for (int i = from; i <= to && i < stream.Count; i++)
        {
            if (stream[i].TokenType is not (TSqlTokenType.WhiteSpace or TSqlTokenType.EndOfFile))
                return i;
        }
        return -1;
    }

    /// <summary>CREATE/ALTER PROCEDURE, FUNCTION or TRIGGER: header as written, body statements formatted.</summary>
    private void FormatModule(TSqlStatement module, IList<TSqlStatement> body, int indent)
    {
        int headerEnd = LastSignificantBefore(body[0].FirstTokenIndex, module.FirstTokenIndex);
        Verbatim(module.FirstTokenIndex, headerEnd, indent);
        FormatList(body, headerEnd + 1, module.LastTokenIndex, indent);
    }

    private void Query(int first, int last)
        => Formatter(first, last).FormatQuery(writer.Column);

    /// <summary>IF / WHILE and their condition, split at AND / OR per <see cref="SqlFormatOptions.ControlFlowConditions"/>.</summary>
    private void Conditions(int first, BooleanExpression predicate, int indent)
        => Formatter(first, predicate.LastTokenIndex)
            .FormatConditions(stream[predicate.FirstTokenIndex], options.ControlFlowConditions, Indent(indent));

    private bool HasComment(int from, int to)
    {
        for (int i = from; i <= to && i < stream.Count; i++)
        {
            if (stream[i].TokenType is TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment)
                return true;
        }
        return false;
    }

    private void Inline(int first, int last, int continuation, bool allKeywords = false)
    {
        if (last >= first)
            Formatter(first, last, allKeywords: allKeywords).FormatInline(continuation);
    }

    /// <summary>
    /// Writes a statement as it was typed, with keyword case applied and its lines shifted to the
    /// current indentation. Line breaks inside strings and comments are never touched.
    /// </summary>
    private void Verbatim(int first, int last, int indent, bool allKeywords = false)
    {
        var cased = Formatter(first, last, allKeywords: allKeywords).CasedTexts();
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

/// <summary>Finds the tokens that name built-in data types (int, varchar, max, …), which are cased per <see cref="SqlFormatOptions.DataTypeCase"/>.</summary>
internal sealed class DataTypeNames : TSqlFragmentVisitor
{
    private readonly IList<TSqlParserToken> _stream;
    private readonly HashSet<TSqlParserToken> _tokens = new(ReferenceEqualityComparer.Instance);

    private DataTypeNames(IList<TSqlParserToken> stream) => _stream = stream;

    public static IReadOnlySet<TSqlParserToken> Collect(TSqlScript script)
    {
        var visitor = new DataTypeNames(script.ScriptTokenStream);
        script.Accept(visitor);
        return visitor._tokens;
    }

    public override void ExplicitVisit(SqlDataTypeReference node)
    {
        if (node.SqlDataTypeOption != SqlDataTypeOption.None && node.Name is { Count: 1 } name)
        {
            AddWords(name.FirstTokenIndex, name.LastTokenIndex); // e.g. "double precision"
            foreach (var parameter in node.Parameters.OfType<MaxLiteral>())
                AddWords(parameter.FirstTokenIndex, parameter.LastTokenIndex);
        }
        base.ExplicitVisit(node);
    }

    private void AddWords(int first, int last)
    {
        for (int i = first; i <= last && i < _stream.Count; i++)
        {
            if (_stream[i].TokenType is not (TSqlTokenType.WhiteSpace or TSqlTokenType.QuotedIdentifier
                    or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment))
                _tokens.Add(_stream[i]);
        }
    }
}
