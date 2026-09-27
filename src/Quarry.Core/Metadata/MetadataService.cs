using System.Text;
using Microsoft.Data.SqlClient;

namespace Quarry.Core.Metadata;

/// <summary>
/// Reads object metadata from the sys catalog views. Each call opens a (pooled) connection to the
/// database it asks about, so queries can use plain <c>sys.*</c> names and OBJECT_DEFINITION.
/// </summary>
public sealed class MetadataService(Func<string?, SqlConnection> connectionFactory)
{
    public async Task<ServerInfo> GetServerInfoAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT CAST(@@SERVERNAME AS nvarchar(256)),
                   CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)),
                   CAST(SERVERPROPERTY('Edition') AS nvarchar(128)),
                   SUSER_SNAME()
            """;
        return (await QueryAsync(null, sql, r => new ServerInfo(
            r.IsDBNull(0) ? "" : r.GetString(0),
            r.IsDBNull(1) ? "" : r.GetString(1),
            r.IsDBNull(2) ? "" : r.GetString(2),
            r.IsDBNull(3) ? "" : r.GetString(3)), ct)).Single();
    }

    public Task<List<string>> GetDatabasesAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT name
            FROM sys.databases
            WHERE state_desc = 'ONLINE' AND HAS_DBACCESS(name) = 1
            ORDER BY CASE WHEN database_id <= 4 THEN 0 ELSE 1 END, name
            """;
        return QueryAsync(null, sql, r => r.GetString(0), ct);
    }

    public Task<List<DbObject>> GetObjectsAsync(string database, CancellationToken ct = default)
    {
        const string sql = """
            SELECT o.object_id, s.name, o.name, RTRIM(o.type)
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.type IN ('U','V','P','PC','FN','FS','IF','TF','FT','SN') AND o.is_ms_shipped = 0
            ORDER BY s.name, o.name
            """;
        return QueryAsync(database, sql, r => new DbObject(
            r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3) switch
            {
                "U" => DbObjectKind.Table,
                "V" => DbObjectKind.View,
                "P" or "PC" => DbObjectKind.Procedure,
                "FN" or "FS" => DbObjectKind.ScalarFunction,
                "SN" => DbObjectKind.Synonym,
                _ => DbObjectKind.TableFunction,
            }), ct);
    }

    public Task<List<ColumnMetadata>> GetColumnsAsync(string database, int objectId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT c.name, t.name, c.max_length, c.precision, c.scale, t.is_user_defined,
                   c.is_nullable, c.is_identity, c.is_computed,
                   CAST(CASE WHEN EXISTS (
                       SELECT 1 FROM sys.index_columns ic
                       JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                       WHERE i.is_primary_key = 1 AND ic.object_id = c.object_id AND ic.column_id = c.column_id)
                   THEN 1 ELSE 0 END AS bit)
            FROM sys.columns c
            JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = @id
            ORDER BY c.column_id
            """;
        return QueryAsync(database, sql, r => new ColumnMetadata(
            r.GetString(0),
            SqlNames.FormatType(r.GetString(1), r.GetInt16(2), r.GetByte(3), r.GetByte(4), r.GetBoolean(5)),
            r.GetBoolean(6), r.GetBoolean(9), r.GetBoolean(7), r.GetBoolean(8)), ct, ("@id", objectId));
    }

    public Task<List<ParameterMetadata>> GetParametersAsync(string database, int objectId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT p.name, t.name, p.max_length, p.precision, p.scale, t.is_user_defined, p.is_output, p.has_default_value
            FROM sys.parameters p
            JOIN sys.types t ON t.user_type_id = p.user_type_id
            WHERE p.object_id = @id AND p.parameter_id > 0
            ORDER BY p.parameter_id
            """;
        return QueryAsync(database, sql, r => new ParameterMetadata(
            r.GetString(0),
            SqlNames.FormatType(r.GetString(1), r.GetInt16(2), r.GetByte(3), r.GetByte(4), r.GetBoolean(5)),
            r.GetBoolean(6), r.GetBoolean(7)), ct, ("@id", objectId));
    }

    public Task<List<IndexMetadata>> GetIndexesAsync(string database, int objectId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT i.name, i.type_desc, i.is_unique, i.is_primary_key,
                   STUFF((SELECT ', ' + c.name + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END
                          FROM sys.index_columns ic
                          JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                          WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                          ORDER BY ic.key_ordinal
                          FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, '')
            FROM sys.indexes i
            WHERE i.object_id = @id AND i.type > 0 AND i.is_hypothetical = 0
            ORDER BY i.is_primary_key DESC, i.name
            """;
        return QueryAsync(database, sql, r => new IndexMetadata(
            r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetBoolean(3), r.IsDBNull(4) ? "" : r.GetString(4)), ct, ("@id", objectId));
    }

    /// <summary>The module's source (procedures, functions, views), or null for tables and encrypted modules.</summary>
    public async Task<string?> GetDefinitionAsync(string database, int objectId, CancellationToken ct = default)
    {
        var rows = await QueryAsync(database, "SELECT OBJECT_DEFINITION(@id)", r => r.IsDBNull(0) ? null : r.GetString(0), ct, ("@id", objectId));
        return rows.FirstOrDefault();
    }

    /// <summary>A CREATE script: the module definition, or a CREATE TABLE generated from metadata.</summary>
    public async Task<string> ScriptCreateAsync(string database, DbObject obj, CancellationToken ct = default)
    {
        if (obj.Kind == DbObjectKind.Table)
        {
            var columns = await GetColumnsAsync(database, obj.ObjectId, ct);
            return ScriptTable(obj, columns);
        }
        if (obj.Kind == DbObjectKind.Synonym)
        {
            var target = await QueryAsync(database, "SELECT base_object_name FROM sys.synonyms WHERE object_id = @id",
                r => r.GetString(0), ct, ("@id", obj.ObjectId));
            return $"CREATE SYNONYM {obj.QualifiedName} FOR {target.FirstOrDefault()};";
        }
        return await GetDefinitionAsync(database, obj.ObjectId, ct)
               ?? $"-- The definition of {obj.QualifiedName} is not available (it may be encrypted).";
    }

    public static string ScriptTable(DbObject table, IReadOnlyList<ColumnMetadata> columns)
    {
        var sb = new StringBuilder();
        sb.Append("CREATE TABLE ").Append(table.QualifiedName).AppendLine(" (");
        var lines = columns.Select(c =>
            $"    {SqlNames.Quote(c.Name)} {c.TypeName}{(c.IsIdentity ? " IDENTITY" : "")} {(c.IsNullable ? "NULL" : "NOT NULL")}").ToList();
        var keys = columns.Where(c => c.IsPrimaryKey).Select(c => SqlNames.Quote(c.Name)).ToList();
        if (keys.Count > 0)
            lines.Add($"    PRIMARY KEY ({string.Join(", ", keys)})");
        sb.AppendLine(string.Join("," + Environment.NewLine, lines));
        sb.Append(");");
        return sb.ToString();
    }

    public static string ScriptSelectTop(DbObject obj, IReadOnlyList<ColumnMetadata> columns, int top = 1000)
    {
        string list = columns.Count == 0
            ? "*"
            : string.Join("," + Environment.NewLine + "       ", columns.Select(c => SqlNames.Quote(c.Name)));
        return $"SELECT TOP ({top}) {list}{Environment.NewLine}FROM {obj.QualifiedName};";
    }

    public static string ScriptExecute(DbObject proc, IReadOnlyList<ParameterMetadata> parameters)
    {
        var sb = new StringBuilder();
        sb.Append("EXEC ").Append(proc.QualifiedName);
        for (int i = 0; i < parameters.Count; i++)
        {
            var p = parameters[i];
            sb.AppendLine(i == 0 ? "" : ",");
            sb.Append("    ").Append(p.Name).Append(" = NULL");
            if (p.IsOutput)
                sb.Append(" OUTPUT");
            sb.Append(" /* ").Append(p.TypeName).Append(" */");
        }
        sb.Append(';');
        return sb.ToString();
    }

    private async Task<List<T>> QueryAsync<T>(string? database, string sql, Func<SqlDataReader, T> map, CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = connectionFactory(database);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var list = new List<T>();
        while (await reader.ReadAsync(ct))
            list.Add(map(reader));
        return list;
    }
}
