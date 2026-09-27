using System.Collections.Concurrent;

namespace Quarry.Core.Metadata;

/// <summary>
/// Lazily-filled metadata for one server, shared by the object browser and completion.
/// Failed lookups are not cached, so they are retried on the next request.
/// </summary>
public sealed class MetadataCache(MetadataService service)
{
    private readonly ConcurrentDictionary<string, Task<List<string>>> _databases = new();
    private readonly ConcurrentDictionary<string, Task<List<DbObject>>> _objects = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string, int), Task<List<ColumnMetadata>>> _columns = new();

    public MetadataService Service { get; } = service;

    public Task<List<string>> GetDatabasesAsync()
        => GetOrAdd(_databases, "", _ => Service.GetDatabasesAsync());

    public Task<List<DbObject>> GetObjectsAsync(string database)
        => GetOrAdd(_objects, database, db => Service.GetObjectsAsync(db));

    public Task<List<ColumnMetadata>> GetColumnsAsync(string database, int objectId)
        => GetOrAdd(_columns, (database.ToLowerInvariant(), objectId), key => Service.GetColumnsAsync(database, key.Item2));

    /// <summary>Objects already loaded for <paramref name="database"/>, without querying.</summary>
    public IReadOnlyList<DbObject>? TryGetLoadedObjects(string database)
        => _objects.TryGetValue(database, out var task) && task.IsCompletedSuccessfully ? task.Result : null;

    public void Invalidate(string? database = null)
    {
        if (database is null)
        {
            _databases.Clear();
            _objects.Clear();
            _columns.Clear();
            return;
        }
        _objects.TryRemove(database, out _);
        foreach (var key in _columns.Keys.Where(k => string.Equals(k.Item1, database, StringComparison.OrdinalIgnoreCase)))
            _columns.TryRemove(key, out _);
    }

    private static Task<T> GetOrAdd<TKey, T>(ConcurrentDictionary<TKey, Task<T>> cache, TKey key, Func<TKey, Task<T>> load)
        where TKey : notnull
    {
        var task = cache.GetOrAdd(key, load);
        if (task.IsFaulted || task.IsCanceled)
        {
            cache.TryRemove(new KeyValuePair<TKey, Task<T>>(key, task));
            task = cache.GetOrAdd(key, load);
        }
        return task;
    }
}
