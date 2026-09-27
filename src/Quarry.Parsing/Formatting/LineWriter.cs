using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Quarry.Parsing.Formatting;

/// <summary>A significant (non-whitespace) token with the context the formatter needs.</summary>
internal sealed class FormatToken(TSqlParserToken token)
{
    public TSqlParserToken Source { get; } = token;

    public string Text => Source.Text;

    public string Upper { get; } = token.Text.ToUpperInvariant();

    public TSqlTokenType Type => Source.TokenType;

    public bool IsLineComment => Type == TSqlTokenType.SingleLineComment;

    public bool IsComment => Type is TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment;

    /// <summary>A line break separated this token from the previous significant token in the source.</summary>
    public bool NewlineBefore { get; init; }

    /// <summary>No whitespace separated this token from the previous one in the source.</summary>
    public bool AdjacentBefore { get; init; }

    /// <summary>Index of the matching parenthesis within the formatter's token list, or -1.</summary>
    public int Match { get; set; } = -1;

    /// <summary>Treat as a keyword for casing (set for recognised clause keywords that lex as identifiers).</summary>
    public bool ForceKeyword { get; set; }

    /// <summary>Set while emitting: a unary + or - (no space after it).</summary>
    public bool IsUnary { get; set; }

    /// <summary>A word the lexer reports as a keyword (reserved words are never identifiers, so recasing is safe).</summary>
    public bool IsKeyword => Type is not (TSqlTokenType.Identifier or TSqlTokenType.QuotedIdentifier or TSqlTokenType.Variable)
                             && Text.Length > 0 && (char.IsLetter(Text[0]) || Text[0] == '_')
                             && Text.All(c => char.IsLetterOrDigit(c) || c == '_');

    public bool IsIdentifier => Type is TSqlTokenType.Identifier or TSqlTokenType.QuotedIdentifier;

    public bool IsSymbol => Text.Length > 0 && Text.All(c => "<>=!+-*/%&|^~".Contains(c));
}

/// <summary>Builds formatted text line by line, tracking the column and deferring indentation.</summary>
internal sealed class LineWriter(string newline)
{
    private readonly StringBuilder _sb = new();
    private int _lineStart;
    private int? _pendingIndent;
    private bool _hasContent;
    private bool _justPadded;

    /// <summary>A line comment was just written; the next text must start a new line.</summary>
    public bool PendingNewline { get; set; }

    /// <summary>Column to continue at when a line comment forces a line break.</summary>
    public int Continuation { get; set; }

    public FormatToken? Previous { get; set; }

    public bool HasContent => _hasContent;

    /// <summary>Number of line breaks written so far.</summary>
    public int LineNumber { get; private set; }

    public int Column => _pendingIndent ?? (_sb.Length - _lineStart);

    public bool IsEmpty => _sb.Length == 0 && _pendingIndent is null or 0;

    public void NewLine(int indent)
    {
        TrimTrailingSpaces();
        _sb.Append(newline);
        LineNumber++;
        _lineStart = _sb.Length;
        _pendingIndent = Math.Max(indent, 0);
        _hasContent = false;
        _justPadded = false;
        PendingNewline = false;
        Previous = null;
    }

    /// <summary>Ends the current line and leaves one empty line.</summary>
    public void BlankLine(int indent)
    {
        NewLine(0);
        NewLine(indent);
    }

    /// <summary>
    /// Makes sure the next text starts on a fresh line at <paramref name="indent"/>, optionally after
    /// one blank line. At the very start of the output no line break is added.
    /// </summary>
    public void StartLine(int indent, bool blank)
    {
        if (_sb.Length == 0)
        {
            _pendingIndent = indent;
            PendingNewline = false;
            return;
        }
        if (_hasContent)
        {
            if (blank)
                BlankLine(indent);
            else
                NewLine(indent);
            return;
        }
        _pendingIndent = indent;
        PendingNewline = false;
        if (blank && !EndsWithBlankLine())
            NewLine(indent);
    }

    private bool EndsWithBlankLine()
    {
        // The current (empty) line starts at _lineStart; the previous line is blank if it is empty too.
        int prevEnd = _lineStart - newline.Length;
        return prevEnd >= newline.Length && _sb.ToString(prevEnd - newline.Length, newline.Length) == newline;
    }

    public void Write(string text, bool spaceBefore)
    {
        if (PendingNewline)
            NewLine(Continuation);
        FlushIndent();
        if (spaceBefore && _hasContent && !_justPadded)
            _sb.Append(' ');
        _sb.Append(text);
        _justPadded = false;
        _hasContent = true;
        int lastBreak = text.LastIndexOf('\n');
        LineNumber += text.Count(c => c == '\n');
        if (lastBreak >= 0)
            _lineStart = _sb.Length - (text.Length - lastBreak - 1);
    }

    /// <summary>Pads with spaces to <paramref name="column"/>, or writes one space if already past it.</summary>
    public void PadTo(int column)
    {
        if (PendingNewline)
        {
            NewLine(column);
            return;
        }
        FlushIndent();
        int current = Column;
        _sb.Append(' ', current < column ? column - current : 1);
        _justPadded = true;
    }

    private void FlushIndent()
    {
        if (_pendingIndent is { } indent)
        {
            _sb.Append(' ', indent);
            _pendingIndent = null;
        }
    }

    private void TrimTrailingSpaces()
    {
        int end = _sb.Length;
        while (end > _lineStart && _sb[end - 1] == ' ')
            end--;
        _sb.Length = end;
    }

    public override string ToString()
    {
        var copy = new LineWriter(newline);
        copy._sb.Append(_sb);
        copy._lineStart = _lineStart;
        copy.TrimTrailingSpaces();
        return copy._sb.ToString();
    }
}
