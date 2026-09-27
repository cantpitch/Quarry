using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Quarry.Core.Connections;
using Quarry.Core.Metadata;

namespace Quarry.App.ViewModels;

public enum NodeIcon
{
    Server,
    Database,
    Folder,
    Table,
    View,
    Procedure,
    Function,
    Synonym,
    Column,
    Key,
    Index,
    Parameter,
    Error,
    Loading,
}

/// <summary>A context-menu action on an explorer node.</summary>
public sealed record NodeAction(string Header, Func<Task> Execute);

/// <summary>What explorer nodes can ask of the rest of the app.</summary>
public interface IExplorerHost
{
    /// <summary>Opens a new query tab on the server/database with <paramref name="text"/>, optionally running it.</summary>
    Task OpenQueryAsync(ServerConnection server, string? database, string text, bool execute);

    Task CopyTextAsync(string text);

    void InsertIntoEditor(string text);

    void Disconnect(ServerNode node);

    Task ShowErrorAsync(string title, string message);

    string Filter { get; }
}

public abstract partial class ExplorerNode : ObservableObject
{
    private bool _loaded;
    private Task? _loading;

    protected ExplorerNode(string text, NodeIcon icon, bool hasChildren)
    {
        _text = text;
        Icon = icon;
        HasLazyChildren = hasChildren;
        if (hasChildren)
            Children.Add(new MessageNode("Loading…", NodeIcon.Loading));
    }

    [ObservableProperty]
    private string _text;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private string? _toolTip;

    public NodeIcon Icon { get; }

    public ObservableCollection<ExplorerNode> Children { get; } = [];

    protected bool HasLazyChildren { get; }

    /// <summary>Text inserted into the editor or copied (e.g. a bracketed name).</summary>
    public virtual string? InsertText => null;

    public virtual IEnumerable<NodeAction> GetActions() => [];

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && HasLazyChildren && !_loaded)
            _ = EnsureLoadedAsync();
    }

    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    public async Task RefreshAsync()
    {
        await (_loading ?? Task.CompletedTask);
        _loaded = false;
        _loading = null;
        OnRefreshing();
        if (IsExpanded)
            await EnsureLoadedAsync();
        else if (HasLazyChildren)
        {
            Children.Clear();
            Children.Add(new MessageNode("Loading…", NodeIcon.Loading));
        }
    }

    protected virtual void OnRefreshing() { }

    private async Task LoadAsync()
    {
        try
        {
            var children = await LoadChildrenAsync();
            SetChildren(children);
            _loaded = true;
        }
        catch (Exception ex)
        {
            Children.Clear();
            Children.Add(new MessageNode($"Error: {ex.Message}", NodeIcon.Error) { ToolTip = ex.Message });
            _loading = null; // allow retry on next expand
        }
    }

    protected virtual void SetChildren(IReadOnlyList<ExplorerNode> children)
    {
        Children.Clear();
        foreach (var child in children)
            Children.Add(child);
        if (Children.Count == 0)
            Children.Add(new MessageNode("(empty)", NodeIcon.Loading));
    }

    protected virtual Task<IReadOnlyList<ExplorerNode>> LoadChildrenAsync()
        => Task.FromResult<IReadOnlyList<ExplorerNode>>([]);

    /// <summary>Applies the explorer's name filter to loaded object folders below this node.</summary>
    public virtual void ApplyFilter(string filter)
    {
        foreach (var child in Children)
            child.ApplyFilter(filter);
    }
}

public sealed class MessageNode(string text, NodeIcon icon) : ExplorerNode(text, icon, hasChildren: false);

public sealed class LeafNode(string text, NodeIcon icon, string? insertText = null) : ExplorerNode(text, icon, hasChildren: false)
{
    public override string? InsertText { get; } = insertText;
}

public sealed class ServerNode : ExplorerNode
{
    private readonly IExplorerHost _host;

    public ServerNode(ServerConnection server, IExplorerHost host)
        : base(server.DisplayName, NodeIcon.Server, hasChildren: true)
    {
        Server = server;
        _host = host;
        ToolTip = server.Info is { } info ? $"{info.ServerName}\nSQL Server {info.ProductVersion}\n{info.Edition}" : null;
    }

    public ServerConnection Server { get; }

    protected override async Task<IReadOnlyList<ExplorerNode>> LoadChildrenAsync()
    {
        var databases = await Server.Metadata.GetDatabasesAsync();
        string[] system = ["master", "model", "msdb", "tempdb"];
        var nodes = new List<ExplorerNode>();
        var systemDbs = databases.Where(d => system.Contains(d, StringComparer.OrdinalIgnoreCase)).ToList();
        if (systemDbs.Count > 0)
            nodes.Add(new StaticFolderNode("System Databases", systemDbs.Select(d => (ExplorerNode)new DatabaseNode(Server, d, _host)).ToList()));
        nodes.AddRange(databases.Except(systemDbs).Select(d => new DatabaseNode(Server, d, _host)));
        return nodes;
    }

    protected override void OnRefreshing() => Server.Metadata.Invalidate();

    public override IEnumerable<NodeAction> GetActions() =>
    [
        new("New Query", () => _host.OpenQueryAsync(Server, null, "", false)),
        new("Refresh", RefreshAsync),
        new("Disconnect", () =>
        {
            _host.Disconnect(this);
            return Task.CompletedTask;
        }),
    ];
}

/// <summary>A folder whose children are known up front.</summary>
public sealed class StaticFolderNode : ExplorerNode
{
    public StaticFolderNode(string text, IReadOnlyList<ExplorerNode> children) : base(text, NodeIcon.Folder, hasChildren: false)
    {
        foreach (var child in children)
            Children.Add(child);
    }
}

public sealed class DatabaseNode : ExplorerNode
{
    private readonly ServerConnection _server;
    private readonly IExplorerHost _host;

    public DatabaseNode(ServerConnection server, string database, IExplorerHost host)
        : base(database, NodeIcon.Database, hasChildren: true)
    {
        _server = server;
        _host = host;
        Database = database;
    }

    public string Database { get; }

    public override string InsertText => SqlNames.Quote(Database);

    protected override Task<IReadOnlyList<ExplorerNode>> LoadChildrenAsync()
    {
        ExplorerNode Folder(string title, params DbObjectKind[] kinds) => new ObjectFolderNode(_server, Database, title, kinds, _host);
        return Task.FromResult<IReadOnlyList<ExplorerNode>>(
        [
            Folder("Tables", DbObjectKind.Table),
            Folder("Views", DbObjectKind.View),
            Folder("Stored Procedures", DbObjectKind.Procedure),
            new StaticFolderNode("Functions",
            [
                Folder("Table-valued Functions", DbObjectKind.TableFunction),
                Folder("Scalar-valued Functions", DbObjectKind.ScalarFunction),
            ]),
            Folder("Synonyms", DbObjectKind.Synonym),
        ]);
    }

    protected override void OnRefreshing() => _server.Metadata.Invalidate(Database);

    public override IEnumerable<NodeAction> GetActions() =>
    [
        new("New Query", () => _host.OpenQueryAsync(_server, Database, "", false)),
        new("Refresh", RefreshAsync),
        new("Copy Name", () => _host.CopyTextAsync(InsertText)),
    ];
}

/// <summary>Tables, Views, … : filled from the database's cached object list and filterable by name.</summary>
public sealed class ObjectFolderNode : ExplorerNode
{
    private readonly ServerConnection _server;
    private readonly string _database;
    private readonly DbObjectKind[] _kinds;
    private readonly IExplorerHost _host;
    private readonly string _title;
    private List<ExplorerNode> _all = [];

    public ObjectFolderNode(ServerConnection server, string database, string title, DbObjectKind[] kinds, IExplorerHost host)
        : base(title, NodeIcon.Folder, hasChildren: true)
    {
        _server = server;
        _database = database;
        _title = title;
        _kinds = kinds;
        _host = host;
    }

    protected override async Task<IReadOnlyList<ExplorerNode>> LoadChildrenAsync()
    {
        var objects = await _server.Metadata.GetObjectsAsync(_database);
        return objects.Where(o => _kinds.Contains(o.Kind)).Select(o => (ExplorerNode)new ObjectNode(_server, _database, o, _host)).ToList();
    }

    protected override void SetChildren(IReadOnlyList<ExplorerNode> children)
    {
        _all = children.ToList();
        ApplyFilter(_host.Filter);
    }

    protected override void OnRefreshing()
    {
        _all = [];
        _server.Metadata.Invalidate(_database);
    }

    public override void ApplyFilter(string filter)
    {
        if (_all.Count == 0 && Children.FirstOrDefault() is MessageNode)
            return; // not loaded yet
        var visible = string.IsNullOrWhiteSpace(filter)
            ? _all
            : _all.Where(n => n.Text.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        Children.Clear();
        foreach (var node in visible)
            Children.Add(node);
        Text = string.IsNullOrWhiteSpace(filter) ? _title : $"{_title} ({visible.Count} of {_all.Count})";
        if (_all.Count == 0)
            Children.Add(new MessageNode("(empty)", NodeIcon.Loading));
    }

    public override IEnumerable<NodeAction> GetActions() => [new("Refresh", RefreshAsync)];
}

public sealed class ObjectNode : ExplorerNode
{
    private readonly ServerConnection _server;
    private readonly string _database;
    private readonly IExplorerHost _host;

    public ObjectNode(ServerConnection server, string database, DbObject obj, IExplorerHost host)
        : base($"{obj.Schema}.{obj.Name}", IconFor(obj.Kind), hasChildren: obj.Kind != DbObjectKind.Synonym)
    {
        _server = server;
        _database = database;
        _host = host;
        Object = obj;
    }

    public DbObject Object { get; }

    public override string InsertText => Object.QualifiedName;

    private static NodeIcon IconFor(DbObjectKind kind) => kind switch
    {
        DbObjectKind.Table => NodeIcon.Table,
        DbObjectKind.View => NodeIcon.View,
        DbObjectKind.Procedure => NodeIcon.Procedure,
        DbObjectKind.Synonym => NodeIcon.Synonym,
        _ => NodeIcon.Function,
    };

    protected override Task<IReadOnlyList<ExplorerNode>> LoadChildrenAsync()
    {
        var service = _server.Metadata.Service;
        var children = new List<ExplorerNode>();
        if (Object.Kind is DbObjectKind.Table or DbObjectKind.View or DbObjectKind.TableFunction)
        {
            children.Add(new DetailFolderNode("Columns", async () =>
                (await _server.Metadata.GetColumnsAsync(_database, Object.ObjectId))
                .Select(c => (ExplorerNode)new LeafNode(c.Description, c.IsPrimaryKey ? NodeIcon.Key : NodeIcon.Column, SqlNames.Quote(c.Name)))
                .ToList()));
        }
        if (Object.Kind is DbObjectKind.Table or DbObjectKind.View)
        {
            children.Add(new DetailFolderNode("Indexes", async () =>
                (await service.GetIndexesAsync(_database, Object.ObjectId))
                .Select(i => (ExplorerNode)new LeafNode(i.Description, i.IsPrimaryKey ? NodeIcon.Key : NodeIcon.Index, SqlNames.Quote(i.Name)))
                .ToList()));
        }
        if (Object.Kind is DbObjectKind.Procedure or DbObjectKind.ScalarFunction or DbObjectKind.TableFunction)
        {
            children.Add(new DetailFolderNode("Parameters", async () =>
                (await service.GetParametersAsync(_database, Object.ObjectId))
                .Select(p => (ExplorerNode)new LeafNode(p.Description, NodeIcon.Parameter, p.Name))
                .ToList()));
        }
        return Task.FromResult<IReadOnlyList<ExplorerNode>>(children);
    }

    public override IEnumerable<NodeAction> GetActions()
    {
        var service = _server.Metadata.Service;
        if (Object.IsRowSource && Object.Kind != DbObjectKind.TableFunction)
        {
            yield return new("Select Top 1000 Rows", async () =>
            {
                var columns = Object.Kind == DbObjectKind.Synonym ? [] : await _server.Metadata.GetColumnsAsync(_database, Object.ObjectId);
                await _host.OpenQueryAsync(_server, _database, MetadataService.ScriptSelectTop(Object, columns), execute: true);
            });
        }
        if (Object.Kind == DbObjectKind.Procedure)
        {
            yield return new("Script as EXECUTE", async () =>
            {
                var parameters = await service.GetParametersAsync(_database, Object.ObjectId);
                await _host.OpenQueryAsync(_server, _database, MetadataService.ScriptExecute(Object, parameters), execute: false);
            });
        }
        if (Object.Kind == DbObjectKind.TableFunction)
        {
            yield return new("Script as SELECT", async () =>
            {
                var parameters = await service.GetParametersAsync(_database, Object.ObjectId);
                string args = string.Join(", ", parameters.Select(p => $"NULL /* {p.Name} {p.TypeName} */"));
                await _host.OpenQueryAsync(_server, _database, $"SELECT TOP (1000) *\nFROM {Object.QualifiedName}({args});", execute: false);
            });
        }
        yield return new("Script as CREATE", async () =>
            await _host.OpenQueryAsync(_server, _database, await service.ScriptCreateAsync(_database, Object), execute: false));
        yield return new("Copy Name", () => _host.CopyTextAsync(InsertText));
        yield return new("Refresh", RefreshAsync);
    }
}

/// <summary>Columns / Indexes / Parameters under an object.</summary>
public sealed class DetailFolderNode(string text, Func<Task<List<ExplorerNode>>> load) : ExplorerNode(text, NodeIcon.Folder, hasChildren: true)
{
    protected override async Task<IReadOnlyList<ExplorerNode>> LoadChildrenAsync() => await load();
}
