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
    Database,
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
/// Ctrl+Space, and schemas / schema members / columns after a dot, including
/// three-part names in other databases ("db.schema.table").
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

        if (await GetDatabasesAsync() is { } databases)
        {
            foreach (var database in databases)
                Add(QuoteIfNeeded(database), CompletionKind.Database, "Database", 1);
        }

        if (await GetObjectsAsync(_context.Database) is { } objects)
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

    /// <summary>
    /// Items after "qualifier.": schemas of a database, objects in a schema, or columns of a
    /// table, view or alias. The qualifier may name another database ("db." or "db.schema.").
    /// </summary>
    private async Task<List<ICompletionData>> GetMemberItemsAsync(TextDocument document, int dotOffset)
    {
        var parts = ReadQualifier(document, dotOffset);
        if (parts.Count == 0 || parts.Count > 3)
            return [];
        var items = new List<ICompletionData>();
        var currentObjects = await GetObjectsAsync(_context.Database);

        // "schema." in the current database.
        if (parts.Count == 1 && currentObjects is not null)
            items.AddRange(SchemaMembers(currentObjects, parts[0]));

        // "db." or "db.schema." in any database.
        if (parts.Count <= 2 && await FindDatabaseAsync(parts[0]) is { } database
            && await GetObjectsAsync(database) is { } dbObjects)
        {
            if (parts.Count == 1)
            {
                items.AddRange(dbObjects.Select(o => o.Schema).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(schema => (ICompletionData)new SqlCompletionData(QuoteIfNeeded(schema), CompletionKind.Schema, $"Schema in {database}")));
            }
            else
            {
                items.AddRange(SchemaMembers(dbObjects, parts[1]));
            }
        }
        if (items.Count > 0)
            return items;

        // Columns of "table.", "schema.table.", "db.schema.table." or "alias.".
        var target = await ResolveObjectAsync(parts, currentObjects)
                     ?? (parts.Count == 1 ? await ResolveAliasAsync(document.Text, parts[0], currentObjects) : null);
        if (target is not { } t || _context.Server is not { } server)
            return [];

        try
        {
            var columns = await server.Metadata.GetColumnsAsync(t.Database, t.Object.ObjectId);
            return columns.Select(c => (ICompletionData)new SqlCompletionData(QuoteIfNeeded(c.Name), CompletionKind.Column, c.Description)).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static IEnumerable<ICompletionData> SchemaMembers(IReadOnlyList<DbObject> objects, string schema)
        => objects.Where(o => o.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase))
            .Select(o => (ICompletionData)new SqlCompletionData(QuoteIfNeeded(o.Name), CompletionKind.Object, $"{o.Kind} {o.Schema}.{o.Name}"));

    /// <summary>Resolves "name", "schema.name" or "db.schema.name" to an object and its database.</summary>
    private async Task<(string Database, DbObject Object)?> ResolveObjectAsync(List<string> parts, IReadOnlyList<DbObject>? currentObjects)
    {
        if (parts.Count == 3)
        {
            if (await FindDatabaseAsync(parts[0]) is { } database && await GetObjectsAsync(database) is { } objects
                && FindObject(objects, parts[1], parts[2]) is { } obj)
                return (database, obj);
            return null;
        }
        if (currentObjects is null || _context.Database is not { } current)
            return null;
        var found = FindObject(currentObjects, parts.Count == 2 ? parts[0] : null, parts[^1]);
        return found is null ? null : (current, found);
    }

    /// <summary>The server's database named <paramref name="name"/>, with its actual casing.</summary>
    private async Task<string?> FindDatabaseAsync(string name)
        => (await GetDatabasesAsync())?.FirstOrDefault(d => d.Equals(name, StringComparison.OrdinalIgnoreCase));

    private async Task<IReadOnlyList<string>?> GetDatabasesAsync()
    {
        if (_context.Server is not { } server)
            return null;
        try
        {
            return await server.Metadata.GetDatabasesAsync();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<DbObject>?> GetObjectsAsync(string? database)
    {
        if (_context.Server is not { } server || string.IsNullOrEmpty(database))
            return null;
        try
        {
            return await server.Metadata.GetObjectsAsync(database);
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
    private async Task<(string Database, DbObject Object)?> ResolveAliasAsync(string text, string alias, IReadOnlyList<DbObject>? currentObjects)
    {
        foreach (Match m in AliasPattern().Matches(text))
        {
            if (!m.Groups["alias"].Value.Equals(alias, StringComparison.OrdinalIgnoreCase))
                continue;
            var nameParts = SplitName(m.Groups["name"].Value);
            if (nameParts.Count is 0 or > 3)
                continue;
            if (await ResolveObjectAsync(nameParts, currentObjects) is { } obj)
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
