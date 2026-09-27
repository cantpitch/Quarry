using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Quarry.Parsing.Formatting;

/// <summary>
/// Lays out a range of tokens: clause-based layout for SELECT / INSERT / UPDATE / DELETE query
/// blocks (recursing into subqueries), and inline layout with normalised spacing for everything else.
/// Every significant token is emitted exactly once and in order; only whitespace and keyword case change.
/// </summary>
internal sealed class QueryFormatter
{
    private enum ClauseKind
    {
        Other,
        With,
        Select,
        Into,
        From,
        Join,
        Where,
        GroupBy,
        Having,
        OrderBy,
        Offset,
        Option,
        For,
        SetOperator,
        Insert,
        Values,
        Update,
        Set,
        Delete,
        Output,
    }

    private sealed record Clause(ClauseKind Kind, int KeywordStart, int KeywordEnd, int BodyEnd)
    {
        public int BodyStart => KeywordEnd;
    }

    /// <summary>Keywords that behave like functions: no space before their "(".</summary>
    private static readonly HashSet<string> FunctionKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "COALESCE", "CONVERT", "NULLIF", "LEFT", "RIGHT", "CONTAINS", "FREETEXT", "CONTAINSTABLE", "FREETEXTTABLE",
        "OPENQUERY", "OPENROWSET", "OPENDATASOURCE", "OPENXML", "IDENTITY", "USER_NAME", "SESSION_USER", "SYSTEM_USER",
        "TRY_CONVERT", "OBJECT_ID",
    };

    private static readonly HashSet<string> JoinHints = new(StringComparer.OrdinalIgnoreCase) { "HASH", "MERGE", "LOOP", "REMOTE" };

    private readonly List<FormatToken> _t;
    private readonly SqlFormatOptions _o;
    private readonly LineWriter _w;
    private readonly IReadOnlySet<TSqlParserToken>? _dataTypes;

    /// <param name="allKeywords">
    /// Treat every word in the range as a keyword, for ranges known to contain only keywords
    /// (BEGIN TRY, SET NOCOUNT ON, …) where the lexer reports some of them as identifiers.
    /// </param>
    /// <param name="keywords">Tokens known from the syntax tree to be keywords, though they lex as identifiers.</param>
    public QueryFormatter(IList<TSqlParserToken> stream, int first, int last, SqlFormatOptions options, LineWriter writer, bool allKeywords = false,
        IReadOnlySet<TSqlParserToken>? keywords = null, IReadOnlySet<TSqlParserToken>? dataTypes = null)
    {
        _o = options;
        _dataTypes = dataTypes;
        _w = writer;
        _t = BuildTokens(stream, first, last);
        foreach (var token in _t.Where(t => t.Type == TSqlTokenType.Identifier))
        {
            if (allKeywords || keywords?.Contains(token.Source) == true)
                token.ForceKeyword = true;
        }
    }

    public int Count => _t.Count;

    /// <summary>The cased text of every significant token in the range, for statements written verbatim.</summary>
    public Dictionary<TSqlParserToken, string> CasedTexts()
    {
        var texts = new Dictionary<TSqlParserToken, string>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < _t.Count; i++)
            texts[_t[i].Source] = Cased(i);
        return texts;
    }

    private static List<FormatToken> BuildTokens(IList<TSqlParserToken> stream, int first, int last)
    {
        var tokens = new List<FormatToken>();
        bool newline = false;
        bool adjacent = first == 0 || stream[first - 1].TokenType != TSqlTokenType.WhiteSpace;
        for (int i = first; i <= last && i < stream.Count; i++)
        {
            var token = stream[i];
            if (token.TokenType == TSqlTokenType.EndOfFile)
                continue;
            if (token.TokenType == TSqlTokenType.WhiteSpace)
            {
                if (token.Text.Contains('\n'))
                    newline = true;
                adjacent = false;
                continue;
            }
            tokens.Add(new FormatToken(token) { NewlineBefore = tokens.Count > 0 && newline, AdjacentBefore = adjacent });
            newline = false;
            adjacent = true;
        }

        var open = new Stack<int>();
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Text == "(")
                open.Push(i);
            else if (tokens[i].Text == ")" && open.Count > 0)
            {
                int o = open.Pop();
                tokens[o].Match = i;
                tokens[i].Match = o;
            }
        }
        return tokens;
    }

    // ---------------------------------------------------------------- query blocks

    /// <summary>Formats all tokens as a query (statement or subquery) whose lines start at <paramref name="baseColumn"/>.</summary>
    public void FormatQuery(int baseColumn) => FormatQuery(0, _t.Count, baseColumn);

    /// <summary>Formats all tokens inline (normalised spacing, subqueries laid out).</summary>
    public void FormatInline(int continuation) => WriteInline(0, _t.Count, continuation);

    /// <summary>
    /// Writes the tokens before <paramref name="predicateStart"/> inline (e.g. "IF", "WHILE") and the rest
    /// as a condition split at top-level AND / OR.
    /// </summary>
    public void FormatConditions(TSqlParserToken predicateStart, ConditionLayout layout, int bodyColumn)
    {
        int p = IndexOf(predicateStart);
        if (p < 0)
        {
            WriteInline(0, _t.Count, bodyColumn);
            return;
        }
        WriteInline(0, p, bodyColumn);
        int conditionColumn = _w.Column + (_w.HasContent ? 1 : 0);
        WriteConditions(p, _t.Count, layout, conditionColumn, bodyColumn);
    }

    /// <summary>
    /// Writes the tokens before <paramref name="listStart"/> inline (e.g. "DECLARE") and the rest as a
    /// comma-separated list lined up after them.
    /// </summary>
    public void FormatList(TSqlParserToken listStart, bool onePerLine)
    {
        int p = IndexOf(listStart);
        if (p < 0)
        {
            WriteInline(0, _t.Count, _w.Column);
            return;
        }
        int continuation = _w.Column;
        WriteInline(0, p, continuation);
        int itemColumn = _w.Column + (_w.HasContent ? 1 : 0);
        WriteList(p, _t.Count, itemColumn, onePerLine);
    }

    private int IndexOf(TSqlParserToken token) => _t.FindIndex(t => ReferenceEquals(t.Source, token));

    private void FormatQuery(int start, int end, int baseColumn)
    {
        // A trailing semicolon is written straight after the query.
        int semicolon = -1;
        if (end > start && _t[end - 1].Text == ";")
        {
            semicolon = end - 1;
            end--;
        }

        var clauses = SplitClauses(start, end);
        int width = clauses.Where(c => c.Kind is not (ClauseKind.Other or ClauseKind.With or ClauseKind.SetOperator))
            .Select(c => KeywordText(c).Length).DefaultIfEmpty(0).Max();

        bool firstLine = true;
        foreach (var clause in clauses)
        {
            _w.Continuation = baseColumn;
            switch (clause.Kind)
            {
                case ClauseKind.Other:
                    if (!firstLine)
                        _w.NewLine(baseColumn);
                    WriteInline(clause.KeywordStart, clause.BodyEnd, baseColumn);
                    break;

                case ClauseKind.SetOperator:
                    _w.NewLine(baseColumn);
                    WriteKeyword(clause);
                    _w.NewLine(baseColumn);
                    if (clause.BodyEnd > clause.BodyStart)
                    {
                        // e.g. UNION ALL (SELECT …)
                        WriteInline(clause.BodyStart, clause.BodyEnd, baseColumn);
                        break;
                    }
                    firstLine = true;
                    continue;

                case ClauseKind.With:
                    if (!firstLine)
                        _w.NewLine(baseColumn);
                    WriteKeyword(clause);
                    WriteCtes(clause, _w.Column + 1);
                    break;

                case ClauseKind.Join when _o.ClauseLayout == ClauseLayout.Indented:
                    _w.NewLine(baseColumn + _o.IndentSize);
                    WriteKeyword(clause);
                    WriteJoinBody(clause, baseColumn + _o.IndentSize);
                    break;

                default:
                    if (!firstLine)
                        _w.NewLine(baseColumn);
                    WriteKeyword(clause);
                    int bodyColumn, continuation;
                    bool hasBody = clause.BodyEnd > clause.BodyStart;
                    switch (_o.ClauseLayout)
                    {
                        case ClauseLayout.Indented:
                            bodyColumn = continuation = baseColumn + _o.IndentSize;
                            if (hasBody)
                                _w.NewLine(bodyColumn);
                            break;
                        case ClauseLayout.Compact:
                            bodyColumn = _w.Column + 1;
                            continuation = baseColumn + _o.IndentSize;
                            break;
                        default:
                            bodyColumn = continuation = baseColumn + width + 1;
                            if (hasBody)
                                _w.PadTo(bodyColumn);
                            break;
                    }
                    if (hasBody)
                        WriteClauseBody(clause, continuation);
                    break;
            }
            firstLine = false;
        }

        if (semicolon >= 0)
            Emit(semicolon, baseColumn);
    }

    private List<Clause> SplitClauses(int start, int end)
    {
        var clauses = new List<Clause>();
        var current = ClauseKind.Other;
        var blockFirst = ClauseKind.Other;
        int clauseStart = start, keywordEnd = start;
        int caseDepth = 0;

        void Close(int at)
        {
            if (at > clauseStart)
                clauses.Add(new Clause(current, clauseStart, keywordEnd, at));
        }

        for (int i = start; i < end; i++)
        {
            var tok = _t[i];
            if (tok.Text == "(" && tok.Match > i)
            {
                i = Math.Min(tok.Match, end - 1);
                continue;
            }
            if (tok.IsComment)
                continue;
            if (tok.Upper == "CASE")
                caseDepth++;
            else if (tok.Upper == "END" && caseDepth > 0)
                caseDepth--;
            if (caseDepth > 0)
                continue;

            var (kind, length) = MatchClause(i, end, current, blockFirst, isBlockStart: i == FirstSignificant(start, end));
            if (kind == ClauseKind.Other)
                continue;

            Close(i);
            current = kind;
            if (kind is ClauseKind.Select or ClauseKind.Insert or ClauseKind.Update or ClauseKind.Delete
                && blockFirst is ClauseKind.Other or ClauseKind.With)
                blockFirst = kind;
            if (kind == ClauseKind.SetOperator)
                blockFirst = ClauseKind.Other;
            if (kind == ClauseKind.With)
                blockFirst = ClauseKind.With;
            clauseStart = i;
            keywordEnd = i + length;
            for (int k = i; k < keywordEnd; k++)
                _t[k].ForceKeyword = true;
            i = keywordEnd - 1;
        }
        Close(end);
        return clauses;
    }

    private int FirstSignificant(int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            if (!_t[i].IsComment)
                return i;
        }
        return end;
    }

    private string UpperAt(int i, int end) => i < end ? _t[i].Upper : "";

    private (ClauseKind Kind, int Length) MatchClause(int i, int end, ClauseKind current, ClauseKind blockFirst, bool isBlockStart)
    {
        string u = _t[i].Upper;
        string next = UpperAt(i + 1, end);
        switch (u)
        {
            case "WITH" when isBlockStart:
                return (ClauseKind.With, 1);
            case "SELECT":
                return (ClauseKind.Select, 1);
            case "INTO" when current == ClauseKind.Select:
                return (ClauseKind.Into, 1);
            case "FROM":
                return (ClauseKind.From, 1);
            case "WHERE":
                return (ClauseKind.Where, 1);
            case "HAVING":
                return (ClauseKind.Having, 1);
            case "GROUP" when next == "BY":
                return (ClauseKind.GroupBy, 2);
            case "ORDER" when next == "BY":
                return (ClauseKind.OrderBy, 2);
            case "OPTION" when next == "(":
                return (ClauseKind.Option, 1);
            case "OFFSET" when current == ClauseKind.OrderBy:
                return (ClauseKind.Offset, 1);
            case "FOR" when next is "XML" or "JSON" or "BROWSE":
                return (ClauseKind.For, 1);
            case "UNION":
                return (ClauseKind.SetOperator, next == "ALL" ? 2 : 1);
            case "EXCEPT" or "INTERSECT":
                return (ClauseKind.SetOperator, 1);
            case "INSERT":
                return (ClauseKind.Insert, next == "INTO" ? 2 : 1);
            case "VALUES" when blockFirst == ClauseKind.Insert:
                return (ClauseKind.Values, 1);
            case "UPDATE" when isBlockStart || blockFirst == ClauseKind.With:
                return (ClauseKind.Update, 1);
            case "SET" when blockFirst == ClauseKind.Update:
                return (ClauseKind.Set, 1);
            case "DELETE":
                return (ClauseKind.Delete, next == "FROM" ? 2 : 1);
            case "OUTPUT" when blockFirst is ClauseKind.Insert or ClauseKind.Update or ClauseKind.Delete:
                return (ClauseKind.Output, 1);
            case "JOIN":
                return (ClauseKind.Join, 1);
            case "CROSS" when next is "JOIN" or "APPLY":
                return (ClauseKind.Join, 2);
            case "OUTER" when next == "APPLY":
                return (ClauseKind.Join, 2);
            case "INNER" or "LEFT" or "RIGHT" or "FULL":
            {
                int j = i + 1;
                if (u != "INNER" && UpperAt(j, end) == "OUTER")
                    j++;
                if (JoinHints.Contains(UpperAt(j, end)))
                    j++;
                return UpperAt(j, end) == "JOIN" ? (ClauseKind.Join, j - i + 1) : (ClauseKind.Other, 0);
            }
            default:
                return (ClauseKind.Other, 0);
        }
    }

    private string KeywordText(Clause clause)
        => string.Join(' ', Enumerable.Range(clause.KeywordStart, clause.KeywordEnd - clause.KeywordStart).Select(i => Cased(i)));

    private void WriteKeyword(Clause clause)
    {
        for (int i = clause.KeywordStart; i < clause.KeywordEnd; i++)
            Emit(i, _w.Continuation);
    }

    private void WriteClauseBody(Clause clause, int continuation)
    {
        int s = clause.BodyStart, e = clause.BodyEnd;
        switch (clause.Kind)
        {
            case ClauseKind.Select:
                s = WriteSelectModifiers(s, e, continuation);
                WriteList(s, e, continuation, _o.ListLayout == ListLayout.OnePerLine);
                break;
            case ClauseKind.Set:
                WriteList(s, e, continuation, _o.ListLayout == ListLayout.OnePerLine);
                break;
            case ClauseKind.Values:
            case ClauseKind.From:
                WriteList(s, e, continuation, onePerLine: true);
                break;
            case ClauseKind.Where:
            case ClauseKind.Having:
                WriteConditions(s, e, _o.WhereConditionsOnNewLines ? ConditionLayout.BodyColumn : ConditionLayout.SameLine, continuation, continuation);
                break;
            case ClauseKind.Join:
                WriteJoinBody(clause, continuation);
                break;
            default:
                WriteInline(s, e, continuation);
                break;
        }
    }

    /// <summary>DISTINCT / ALL / TOP (n) [PERCENT] [WITH TIES] stay on the SELECT line.</summary>
    private int WriteSelectModifiers(int s, int e, int continuation)
    {
        int i = s;
        if (i < e && _t[i].Upper is "DISTINCT" or "ALL")
            i++;
        if (i < e && _t[i].Upper == "TOP")
        {
            i++;
            if (i < e && _t[i].Text == "(" && _t[i].Match > i)
                i = _t[i].Match + 1;
            else if (i < e)
                i++;
            if (i < e && _t[i].Upper == "PERCENT")
                i++;
            if (i + 1 < e && _t[i].Upper == "WITH" && _t[i + 1].Upper == "TIES")
                i += 2;
        }
        if (i > s)
            WriteInline(s, i, continuation);
        return i;
    }

    private void WriteJoinBody(Clause clause, int continuation)
    {
        int s = clause.BodyStart, e = clause.BodyEnd;
        int on = FindTopLevel(s, e, "ON");
        if (on < 0)
        {
            WriteInline(s, e, continuation);
            return;
        }
        WriteInline(s, on, continuation);
        Emit(on, continuation);
        int conditionColumn = _w.Column + 1;
        WriteConditions(on + 1, e, _o.JoinConditions, conditionColumn, continuation);
    }

    private int FindTopLevel(int s, int e, string word)
    {
        int caseDepth = 0;
        for (int i = s; i < e; i++)
        {
            var tok = _t[i];
            if (tok.Text == "(" && tok.Match > i)
            {
                i = tok.Match;
                continue;
            }
            if (tok.Upper == "CASE")
                caseDepth++;
            else if (tok.Upper == "END" && caseDepth > 0)
                caseDepth--;
            else if (caseDepth == 0 && tok.Upper == word)
                return i;
        }
        return -1;
    }

    private void WriteCtes(Clause clause, int cteColumn)
    {
        var items = SplitTopLevel(clause.BodyStart, clause.BodyEnd, isSeparator: i => _t[i].Text == ",");
        for (int n = 0; n < items.Count; n++)
        {
            var (s, e, separator) = items[n];
            WriteInline(s, e, cteColumn);
            if (separator >= 0)
            {
                Emit(separator, cteColumn);
                WriteTrailingComments(items, n);
                _w.NewLine(cteColumn);
            }
        }
    }

    // ---------------------------------------------------------------- lists and conditions

    /// <summary>Splits [s, e) at top-level separator tokens (outside parentheses and CASE … END).</summary>
    private List<(int Start, int End, int Separator)> SplitTopLevel(int s, int e, Func<int, bool> isSeparator)
    {
        var parts = new List<(int, int, int)>();
        int partStart = s, caseDepth = 0;
        for (int i = s; i < e; i++)
        {
            var tok = _t[i];
            if (tok.Text == "(" && tok.Match > i)
            {
                i = tok.Match;
                continue;
            }
            if (tok.Upper == "CASE")
                caseDepth++;
            else if (tok.Upper == "END" && caseDepth > 0)
                caseDepth--;
            else if (caseDepth == 0 && isSeparator(i))
            {
                parts.Add((partStart, i, i));
                partStart = i + 1;
            }
        }
        parts.Add((partStart, e, -1));
        return parts;
    }

    private void WriteList(int s, int e, int itemColumn, bool onePerLine)
    {
        var items = SplitTopLevel(s, e, i => _t[i].Text == ",");
        bool leading = onePerLine && _o.CommaPlacement == CommaPlacement.Leading;
        for (int n = 0; n < items.Count; n++)
        {
            var (start, end, separator) = items[n];
            WriteInline(start, end, itemColumn);
            if (separator < 0)
                continue;

            if (leading)
            {
                // Comments after the comma stay after it: ", -- note" then the item on the next line.
                _w.NewLine(Math.Max(itemColumn - 2, 0));
                Emit(separator, itemColumn);
            }
            else
            {
                Emit(separator, itemColumn);
                WriteTrailingComments(items, n);
                if (onePerLine)
                    _w.NewLine(itemColumn);
            }
        }
    }

    /// <summary>
    /// Comments that follow a separator on the same source line stay on that line
    /// (e.g. "a, -- note"). They are moved out of the next item.
    /// </summary>
    private void WriteTrailingComments(List<(int Start, int End, int Separator)> items, int n)
    {
        if (n + 1 >= items.Count)
            return;
        var (start, end, separator) = items[n + 1];
        int i = start;
        while (i < end && _t[i].IsComment && !_t[i].NewlineBefore)
        {
            Emit(i, _w.Continuation);
            i++;
        }
        items[n + 1] = (i, end, separator);
    }

    /// <summary>
    /// Writes a boolean expression split at top-level AND / OR. The AND of BETWEEN … AND … and
    /// anything inside parentheses or CASE stays put.
    /// </summary>
    private void WriteConditions(int s, int e, ConditionLayout layout, int conditionColumn, int bodyColumn)
    {
        bool betweenPending = false;
        var parts = SplitTopLevel(s, e, i =>
        {
            string u = _t[i].Upper;
            if (u == "BETWEEN")
                betweenPending = true;
            if (u is not ("AND" or "OR"))
                return false;
            if (u == "AND" && betweenPending)
            {
                betweenPending = false;
                return false;
            }
            return true;
        });

        for (int n = 0; n < parts.Count; n++)
        {
            var (start, end, op) = parts[n];
            WriteInline(start, end, n == 0 ? conditionColumn : _w.Column);
            if (op < 0)
                continue;
            switch (layout)
            {
                case ConditionLayout.UnderFirstCondition:
                    _w.NewLine(Math.Max(conditionColumn - _t[op].Text.Length - 1, bodyColumn));
                    break;
                case ConditionLayout.BodyColumn:
                    _w.NewLine(bodyColumn);
                    break;
            }
            Emit(op, bodyColumn);
        }
    }

    // ---------------------------------------------------------------- inline

    private void WriteInline(int s, int e, int continuation)
    {
        for (int i = s; i < e; i++)
        {
            var tok = _t[i];
            if (tok.Upper == "CASE" && tok.IsKeyword && FindCaseEnd(i, e) is var caseEnd and >= 0)
            {
                WriteCase(i, caseEnd, continuation);
                i = caseEnd;
                continue;
            }
            if (tok.Text == "(" && tok.Match > i && tok.Match < e)
            {
                Emit(i, continuation);
                int inner = i + 1, close = tok.Match;
                if (IsSubquery(inner, close))
                    FormatQuery(inner, close, _w.Column);
                else
                    WriteInline(inner, close, continuation);
                Emit(close, continuation);
                i = close;
                continue;
            }
            Emit(i, continuation);
        }
    }

    /// <summary>Index of the END that closes the CASE at <paramref name="caseIndex"/>, or -1.</summary>
    private int FindCaseEnd(int caseIndex, int e)
    {
        int depth = 0;
        for (int i = caseIndex; i < e; i++)
        {
            var tok = _t[i];
            if (tok.Text == "(" && tok.Match > i)
            {
                i = tok.Match;
                continue;
            }
            if (!tok.IsKeyword)
                continue;
            if (tok.Upper == "CASE")
                depth++;
            else if (tok.Upper == "END" && --depth == 0)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Writes CASE … END with each WHEN and the ELSE on their own lines (unless the layout or a
    /// single WHEN keeps it on one line). Nested CASE expressions are laid out relative to where they start.
    /// </summary>
    private void WriteCase(int caseIndex, int end, int continuation)
    {
        // WHEN / ELSE of this CASE (not of nested ones, and not inside parentheses).
        var branches = new List<int>();
        bool nested = false, subquery = false;
        int depth = 0;
        for (int i = caseIndex + 1; i < end; i++)
        {
            var tok = _t[i];
            if (tok.Text == "(" && tok.Match > i)
            {
                subquery |= IsSubquery(i + 1, tok.Match);
                i = tok.Match;
                continue;
            }
            if (!tok.IsKeyword)
                continue;
            if (tok.Upper == "CASE")
            {
                nested = true;
                depth++;
            }
            else if (tok.Upper == "END")
                depth--;
            else if (depth == 0 && tok.Upper is "WHEN" or "ELSE")
                branches.Add(i);
        }

        int whens = branches.Count(b => _t[b].Upper == "WHEN");
        bool oneLine = _o.CaseLayout == CaseLayout.SingleLine || branches.Count == 0
                       || (_o.SingleWhenCaseOnOneLine && whens == 1 && !nested && !subquery);

        Emit(caseIndex, continuation);
        int caseColumn = _w.Column - Cased(caseIndex).Length;
        if (oneLine)
        {
            WriteInline(caseIndex + 1, end, continuation);
            Emit(end, continuation);
            return;
        }

        // Comments on their own lines before a WHEN / ELSE go with it.
        for (int n = 0; n < branches.Count; n++)
        {
            int floor = n == 0 ? caseIndex + 1 : branches[n - 1] + 1;
            while (branches[n] - 1 >= floor && _t[branches[n] - 1].IsComment && _t[branches[n] - 1].NewlineBefore)
                branches[n]--;
        }

        WriteInline(caseIndex + 1, branches[0], caseColumn + _o.IndentSize); // simple CASE input expression
        int branchColumn = caseColumn + _o.IndentSize;
        for (int n = 0; n < branches.Count; n++)
        {
            int from = branches[n], to = n + 1 < branches.Count ? branches[n + 1] : end;
            if (_o.CaseLayout == CaseLayout.Aligned && n == 0 && !_t[from].IsComment && !_w.PendingNewline)
            {
                Emit(from, continuation);
                branchColumn = _w.Column - Cased(from).Length;
                WriteInline(from + 1, to, branchColumn + _o.IndentSize);
                continue;
            }
            _w.NewLine(branchColumn);
            for (; from < to && _t[from].IsComment; from++)
            {
                Emit(from, branchColumn);
                if (_w.PendingNewline)
                    _w.NewLine(branchColumn);
            }
            WriteInline(from, to, branchColumn + _o.IndentSize);
        }
        _w.NewLine(caseColumn);
        Emit(end, continuation);
    }

    private bool IsSubquery(int s, int e)
    {
        int first = FirstSignificant(s, e);
        return first < e && _t[first].Upper is "SELECT" or "WITH";
    }

    private void Emit(int index, int continuation)
    {
        var tok = _t[index];
        _w.Continuation = continuation;
        if (tok.IsComment && tok.NewlineBefore && _w.HasContent)
            _w.NewLine(continuation);
        else if (_w.PendingNewline)
            _w.NewLine(continuation);

        var previous = _w.Previous;
        tok.IsUnary = tok.Text is "-" or "+" or "~" && IsUnaryPosition(previous);
        _w.Write(Cased(index), _w.HasContent && NeedsSpace(previous, tok));
        _w.Previous = tok;
        if (tok.IsLineComment)
            _w.PendingNewline = true;
    }

    private static bool IsUnaryPosition(FormatToken? previous)
        => previous is null || previous.Text is "(" or "," || (previous.IsSymbol && !previous.IsUnary)
           || (previous.IsKeyword && !FunctionKeywords.Contains(previous.Text) && previous.Upper is not ("END" or "NULL"));

    private static bool NeedsSpace(FormatToken? previous, FormatToken current)
    {
        if (previous is null)
            return false;
        if (current.IsComment)
            return true;
        if (current.Text is "," or ")" or ";" or "." or "::" || previous.Text is "(" or "." or "::")
            return false;
        if (previous.IsUnary)
            return false;
        if (current.Text == "(")
        {
            if (previous.IsComment)
                return true;
            if (previous.IsIdentifier)
                return !current.AdjacentBefore; // function call vs. "INSERT INTO t (cols)": keep as written
            if (previous.IsKeyword && FunctionKeywords.Contains(previous.Text))
                return false;
            return true;
        }
        // Multi-character operators the lexer may split ("> =", "! =") keep their characters together.
        if (previous.IsSymbol && current.IsSymbol && current.AdjacentBefore)
            return false;
        return true;
    }

    private string Cased(int index)
    {
        var tok = _t[index];
        if (_dataTypes?.Contains(tok.Source) == true)
            return Apply(_o.DataTypeCase, tok.Text);
        if (_o.KeywordCase == KeywordCase.Preserve || tok.IsComment)
            return tok.Text;
        // Never recase names in a multi-part identifier (a.b), whatever they look like.
        bool afterDot = index > 0 && _t[index - 1].Text == "." && tok.AdjacentBefore;
        bool beforeDot = index + 1 < _t.Count && _t[index + 1].Text == "." && _t[index + 1].AdjacentBefore;
        if (afterDot || beforeDot)
            return tok.Text;

        bool keyword = tok.IsKeyword || tok.ForceKeyword
            || (tok.Type == TSqlTokenType.Identifier && index + 1 < _t.Count && _t[index + 1].Text == "("
                && BuiltInFunctions.Contains(tok.Text));
        if (!keyword)
            return tok.Text;
        return Apply(_o.KeywordCase, tok.Text);
    }

    private static string Apply(KeywordCase casing, string text) => casing switch
    {
        KeywordCase.Upper => text.ToUpperInvariant(),
        KeywordCase.Lower => text.ToLowerInvariant(),
        _ => text,
    };
}
