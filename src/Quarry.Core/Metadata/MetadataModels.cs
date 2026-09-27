namespace Quarry.Core.Metadata;

public enum DbObjectKind
{
    Table,
    View,
    Procedure,
    ScalarFunction,
    TableFunction,
    Synonym,
}

public sealed record DbObject(int ObjectId, string Schema, string Name, DbObjectKind Kind)
{
    public string QualifiedName => $"{SqlNames.Quote(Schema)}.{SqlNames.Quote(Name)}";

    public bool IsRowSource => Kind is DbObjectKind.Table or DbObjectKind.View or DbObjectKind.TableFunction or DbObjectKind.Synonym;
}

public sealed record ColumnMetadata(string Name, string TypeName, bool IsNullable, bool IsPrimaryKey, bool IsIdentity, bool IsComputed)
{
    public string Description
    {
        get
        {
            var parts = new List<string>();
            if (IsPrimaryKey)
                parts.Add("PK");
            parts.Add(TypeName);
            if (IsIdentity)
                parts.Add("identity");
            if (IsComputed)
                parts.Add("computed");
            parts.Add(IsNullable ? "null" : "not null");
            return $"{Name} ({string.Join(", ", parts)})";
        }
    }
}

public sealed record ParameterMetadata(string Name, string TypeName, bool IsOutput, bool HasDefault)
{
    public string Description => $"{Name} ({TypeName}{(IsOutput ? ", output" : ", input")}{(HasDefault ? ", default" : "")})";
}

public sealed record IndexMetadata(string Name, string TypeDescription, bool IsUnique, bool IsPrimaryKey, string Columns)
{
    public string Description
    {
        get
        {
            string kind = IsPrimaryKey ? "PK, " : IsUnique ? "unique, " : "";
            return $"{Name} ({kind}{TypeDescription.ToLowerInvariant()}: {Columns})";
        }
    }
}

public sealed record ServerInfo(string ServerName, string ProductVersion, string Edition, string Login);

public static class SqlNames
{
    /// <summary>Brackets an identifier, escaping ']' — equivalent to QUOTENAME.</summary>
    public static string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";

    /// <summary>Makes a string literal, escaping quotes.</summary>
    public static string Literal(string value) => "N'" + value.Replace("'", "''") + "'";

    /// <summary>
    /// Formats a column/parameter type from sys.types plus the column's max_length, precision and scale.
    /// </summary>
    public static string FormatType(string typeName, short maxLength, byte precision, byte scale, bool isUserDefined)
    {
        if (isUserDefined)
            return typeName;
        return typeName.ToLowerInvariant() switch
        {
            "varchar" or "char" or "varbinary" or "binary" => $"{typeName}({(maxLength == -1 ? "max" : maxLength.ToString())})",
            "nvarchar" or "nchar" => $"{typeName}({(maxLength == -1 ? "max" : (maxLength / 2).ToString())})",
            "decimal" or "numeric" => $"{typeName}({precision},{scale})",
            "datetime2" or "time" or "datetimeoffset" => $"{typeName}({scale})",
            "float" when precision != 53 => $"{typeName}({precision})",
            _ => typeName,
        };
    }
}
