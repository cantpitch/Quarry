using System.Text.RegularExpressions;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Quarry.Core.Connections;
using Quarry.Core.Metadata;

namespace Quarry.App.Editor;

public enum CompletionKind
{
    Keyword,
    Function,
    DataType,
    Variable,
    Schema,
    Object,
    Column,
}

public sealed class SqlCompletionData(string text, CompletionKind kind, string? description = null, double priority = 0) : ICompletionData
{
    public IImage? Image => null;

    public string Text { get; } = text;

    public CompletionKind Kind { get; } = kind;

    public object Content => Text;

    public object Description { get; } = description ?? kind.ToString();

    public double Priority { get; } = priority;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        => textArea.Document.Replace(completionSegment, Text);
}

/// <summary>Where completion gets metadata from: the document's current server and database.</summary>
public interface ICompletionContext
{
    ServerConnection? Server { get; }

    string? Database { get; }
}

/// <summary>
/// Basic completion: keywords, functions, types, variables and object names on typing or
/// Ctrl+Space, and schema members / columns after a dot.
/// </summary>
public sealed partial class SqlCompletionController
{
    private readonly TextEditor _editor;
    private readonly ICompletionContext _context;
    private CompletionWindow? _window;
    private int _requestId;

    public SqlCompletionController(TextEditor editor, ICompletionContext context)
    {
        _editor = editor;
        _context = context;
        editor.TextArea.TextEntered += OnTextEntered;
        editor.TextArea.KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            _ = ShowAsync(explicitRequest: true);
        }
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (_window is not null || string.IsNullOrEmpty(e.Text))
            return;
        char c = e.Text[^1];
        if (c == '.' || ((char.IsLetter(c) || c == '_' || c == '@' || c == '#') && IsWordStart()))
            _ = ShowAsync(explicitRequest: false);
    }

    private bool IsWordStart()
    {
        int caret = _editor.CaretOffset;
        if (caret < 2)
            return true;
        char before = _editor.Document.GetCharAt(caret - 2);
        return !IsIdentifierChar(before);
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$';

    private async Task ShowAsync(bool explicitRequest)
    {
        int requestId = ++_requestId;
        var document = _editor.Document;
        int caret = _editor.CaretOffset;
        if (IsInCommentOrString(document, caret))
            return;

        // The word being typed, which the completion window filters on.
        int wordStart = caret;
        while (wordStart > 0 && IsIdentifierChar(document.GetCharAt(wordStart - 1)))
            wordStart--;

        List<ICompletionData> items;
        if (wordStart > 0 && document.GetCharAt(wordStart - 1) == '.')
        {
            items = await GetMemberItemsAsync(document, wordStart - 1);
        }
        else
        {
            if (!explicitRequest && caret - wordStart > 1)
                return;
            items = await GetGeneralItemsAsync(document);
        }

        // Stale: the user kept typing past this request or moved elsewhere.
        if (requestId != _requestId || items.Count == 0 || _window is not null)
            return;
        if (_editor.CaretOffset < wordStart || _editor.CaretOffset > wordStart + 128)
            return;

        var window = new CompletionWindow(_editor.TextArea)
        {
            StartOffset = wordStart,
            EndOffset = _editor.CaretOffset,
            CloseWhenCaretAtBeginning = !explicitRequest,
            MinWidth = 280,
        };
        foreach (var item in items)
            window.CompletionList.CompletionData.Add(item);
        string typed = document.GetText(wordStart, _editor.CaretOffset - wordStart);
        if (typed.Length > 0)
            window.CompletionList.SelectItem(typed);
        window.Closed += (_, _) => _window = null;
        _window = window;
        window.Show();
    }

    private async Task<List<ICompletionData>> GetGeneralItemsAsync(TextDocument document)
    {
        var items = new List<ICompletionData>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string text, CompletionKind kind, string? description = null, double priority = 0)
        {
            if (seen.Add(text))
                items.Add(new SqlCompletionData(text, kind, description, priority));
        }

        foreach (Match m in VariablePattern().Matches(document.Text))
            Add(m.Value, CompletionKind.Variable, "Variable", 2);

        if (await GetObjectsAsync() is { } objects)
        {
            foreach (var schema in objects.Select(o => o.Schema).Distinct(StringComparer.OrdinalIgnoreCase))
                Add(QuoteIfNeeded(schema), CompletionKind.Schema, "Schema", 1);
            foreach (var obj in objects)
                Add(QuoteIfNeeded(obj.Name), CompletionKind.Object, $"{obj.Kind} {obj.Schema}.{obj.Name}", 1);
        }

        foreach (var k in SqlKeywords.Keywords)
            Add(k, CompletionKind.Keyword, "Keyword");
        foreach (var f in SqlKeywords.Functions)
            Add(f, CompletionKind.Function, "Function");
        foreach (var t in SqlKeywords.DataTypes)
            Add(t, CompletionKind.DataType, "Data type");

        return items.OrderBy(i => i.Text, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Items after "qualifier.": objects in a schema, or columns of a table, view or alias.</summary>
    private async Task<List<ICompletionData>> GetMemberItemsAsync(TextDocument document, int dotOffset)
    {
        var parts = ReadQualifier(document, dotOffset);
        if (parts.Count == 0 || await GetObjectsAsync() is not { } objects)
            return [];

        string last = parts[^1];
        if (parts.Count == 1 && objects.Any(o => o.Schema.Equals(last, StringComparison.OrdinalIgnoreCase)))
        {
            return objects.Where(o => o.Schema.Equals(last, StringComparison.OrdinalIgnoreCase))
                .Select(o => (ICompletionData)new SqlCompletionData(QuoteIfNeeded(o.Name), CompletionKind.Object, $"{o.Kind} {o.Schema}.{o.Name}"))
                .ToList();
        }

        DbObject? target = parts.Count >= 2
            ? FindObject(objects, parts[^2], last)
            : FindObject(objects, null, last) ?? ResolveAlias(document.Text, last, objects);
        if (target is null || _context.Server is not { } server || _context.Database is not { } db)
            return [];

        try
        {
            var columns = await server.Metadata.GetColumnsAsync(db, target.ObjectId);
            return columns.Select(c => (ICompletionData)new SqlCompletionData(QuoteIfNeeded(c.Name), CompletionKind.Column, c.Description)).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<DbObject>?> GetObjectsAsync()
    {
        if (_context.Server is not { } server || string.IsNullOrEmpty(_context.Database))
            return null;
        try
        {
            return await server.Metadata.GetObjectsAsync(_context.Database);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static DbObject? FindObject(IReadOnlyList<DbObject> objects, string? schema, string name)
        => objects.Where(o => o.IsRowSource && o.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                              && (schema is null || o.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(o => o.Schema.Equals("dbo", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();

    /// <summary>Finds "FROM|JOIN name [AS] alias" in the script and resolves the alias to an object.</summary>
    private static DbObject? ResolveAlias(string text, string alias, IReadOnlyList<DbObject> objects)
    {
        foreach (Match m in AliasPattern().Matches(text))
        {
            if (!m.Groups["alias"].Value.Equals(alias, StringComparison.OrdinalIgnoreCase))
                continue;
            var nameParts = SplitName(m.Groups["name"].Value);
            if (nameParts.Count == 0)
                continue;
            var obj = FindObject(objects, nameParts.Count >= 2 ? nameParts[^2] : null, nameParts[^1]);
            if (obj is not null)
                return obj;
        }
        return null;
    }

    /// <summary>Reads the dotted name that ends just before <paramref name="dotOffset"/>, e.g. "[dbo].Orders".</summary>
    private static List<string> ReadQualifier(TextDocument document, int dotOffset)
    {
        int start = dotOffset;
        while (start > 0)
        {
            char c = document.GetCharAt(start - 1);
            if (IsIdentifierChar(c) || c is '.' or '[' or ']' or '"')
                start--;
            else
                break;
        }
        return SplitName(document.GetText(start, dotOffset - start));
    }

    private static List<string> SplitName(string name)
        => name.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().Trim('[', ']', '"').Replace("]]", "]"))
            .Where(p => p.Length > 0)
            .ToList();

    private static string QuoteIfNeeded(string name)
        => RegularIdentifier().IsMatch(name) && !SqlKeywords.IsKeyword(name) ? name : SqlNames.Quote(name);

    /// <summary>Line-based check: after "--" or inside an unclosed '...' on the caret's line.</summary>
    private static bool IsInCommentOrString(TextDocument document, int caret)
    {
        var line = document.GetLineByOffset(caret);
        string before = document.GetText(line.Offset, caret - line.Offset);
        bool inString = false;
        for (int i = 0; i < before.Length; i++)
        {
            if (before[i] == '\'')
                inString = !inString;
            else if (!inString && before[i] == '-' && i + 1 < before.Length && before[i + 1] == '-')
                return true;
        }
        return inString;
    }

    [GeneratedRegex(@"@[A-Za-z_#$@][\w#$@]*")]
    private static partial Regex VariablePattern();

    [GeneratedRegex(@"(?:FROM|JOIN|UPDATE|INTO|APPLY)\s+(?<name>(?:\[[^\]]+\]|""[^""]+""|[\w#$]+)(?:\.(?:\[[^\]]+\]|""[^""]+""|[\w#$]+))*)(?:\s+(?:AS\s+)?(?<alias>(?!(?:WHERE|ON|JOIN|INNER|LEFT|RIGHT|FULL|CROSS|OUTER|GROUP|ORDER|SET|WITH|UNION|HAVING|OPTION|VALUES|SELECT)\b)[A-Za-z_][\w$]*))?",
        RegexOptions.IgnoreCase)]
    private static partial Regex AliasPattern();

    [GeneratedRegex(@"^[A-Za-z_][\w@#$]*$")]
    private static partial Regex RegularIdentifier();
}
