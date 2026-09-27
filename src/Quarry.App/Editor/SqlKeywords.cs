namespace Quarry.App.Editor;

/// <summary>Word lists shared by syntax highlighting and completion.</summary>
public static class SqlKeywords
{
    public static readonly string[] Keywords =
    [
        "ADD", "ALL", "ALTER", "AND", "ANY", "APPLY", "AS", "ASC", "AUTHORIZATION", "BACKUP", "BEGIN", "BETWEEN",
        "BREAK", "BROWSE", "BULK", "BY", "CASCADE", "CASE", "CATCH", "CHECK", "CHECKPOINT", "CLOSE", "CLUSTERED",
        "COLLATE", "COLUMN", "COMMIT", "COMMITTED", "COMPUTE", "CONSTRAINT", "CONTAINS", "CONTAINSTABLE", "CONTINUE",
        "CREATE", "CROSS", "CURRENT", "CURSOR", "DATABASE", "DBCC", "DEALLOCATE", "DECLARE", "DEFAULT", "DELETE",
        "DENY", "DESC", "DISABLE", "DISTINCT", "DISTRIBUTED", "DROP", "DUMP", "ELSE", "ENABLE", "END", "ERRLVL",
        "ESCAPE", "EXCEPT", "EXEC", "EXECUTE", "EXISTS", "EXIT", "EXTERNAL", "FETCH", "FILE", "FILLFACTOR", "FIRST",
        "FOR", "FOREIGN", "FREETEXT", "FREETEXTTABLE", "FROM", "FULL", "FUNCTION", "GO", "GOTO", "GRANT", "GROUP",
        "HAVING", "HOLDLOCK", "IDENTITY", "IDENTITY_INSERT", "IF", "IN", "INCLUDE", "INDEX", "INNER", "INSERT",
        "INTERSECT", "INTO", "IS", "ISOLATION", "JOIN", "KEY", "KILL", "LAST", "LEFT", "LEVEL", "LIKE", "LINENO",
        "LOAD", "MATCHED", "MERGE", "NATIONAL", "NEXT", "NOCHECK", "NOCOUNT", "NOLOCK", "NONCLUSTERED", "NOT", "NULL",
        "OF", "OFF", "OFFSET", "OFFSETS", "ON", "ONLY", "OPEN", "OPENDATASOURCE", "OPENJSON", "OPENQUERY",
        "OPENROWSET", "OPENXML", "OPTION", "OR", "ORDER", "OUTER", "OUTPUT", "OVER", "PARTITION", "PERCENT", "PIVOT",
        "PLAN", "PRECEDING", "PRIMARY", "PRINT", "PROC", "PROCEDURE", "PUBLIC", "RAISERROR", "RANGE", "READ",
        "READTEXT", "RECOMPILE", "RECONFIGURE", "REFERENCES", "REPEATABLE", "REPLICATION", "RESTORE", "RESTRICT",
        "RETURN", "RETURNS", "REVERT", "REVOKE", "RIGHT", "ROLLBACK", "ROWCOUNT", "ROWGUIDCOL", "ROWS", "RULE", "SAVE",
        "SCHEMA", "SECURITYAUDIT", "SELECT", "SERIALIZABLE", "SET", "SETUSER", "SHUTDOWN", "SNAPSHOT", "SOME",
        "STATISTICS", "SYNONYM", "TABLE", "TABLESAMPLE", "TEXTSIZE", "THEN", "THROW", "TIES", "TO", "TOP", "TRAN",
        "TRANSACTION", "TRIGGER", "TRUNCATE", "TRY", "TSEQUAL", "UNBOUNDED", "UNCOMMITTED", "UNION", "UNIQUE",
        "UNPIVOT", "UPDATE", "UPDATETEXT", "USE", "USING", "VALUES", "VARYING", "VIEW", "WAITFOR", "WHEN", "WHERE",
        "WHILE", "WITH", "WITHIN", "WRITETEXT", "XACT_ABORT", "ANSI_NULLS", "QUOTED_IDENTIFIER", "READONLY", "PERSISTED",
        "ENCRYPTION", "SCHEMABINDING", "OUT", "FOLLOWING", "ROW", "SOURCE", "TARGET", "JSON", "XML", "PATH", "AUTO", "RAW",
        "ELEMENTS", "ROOT", "TYPE", "LOGIN", "USER", "ROLE", "AFTER", "INSTEAD", "LOCAL", "GLOBAL", "STATIC", "FAST_FORWARD",
        "FORWARD_ONLY", "READ_ONLY", "SCROLL", "DYNAMIC", "KEYSET", "MAXRECURSION", "RECURSIVE",
    ];

    public static readonly string[] Functions =
        [.. Quarry.Parsing.Formatting.BuiltInFunctions.Names, "AT", "TIME", "ZONE"];

    public static readonly string[] DataTypes =
    [
        "BIGINT", "BINARY", "BIT", "CHAR", "CURSOR", "DATE", "DATETIME", "DATETIME2", "DATETIMEOFFSET", "DECIMAL",
        "FLOAT", "GEOGRAPHY", "GEOMETRY", "HIERARCHYID", "IMAGE", "INT", "MONEY", "NCHAR", "NTEXT", "NUMERIC",
        "NVARCHAR", "REAL", "ROWVERSION", "SMALLDATETIME", "SMALLINT", "SMALLMONEY", "SQL_VARIANT", "SYSNAME", "TEXT",
        "TIME", "TIMESTAMP", "TINYINT", "UNIQUEIDENTIFIER", "VARBINARY", "VARCHAR", "XML", "MAX", "VECTOR", "JSON",
    ];

    private static readonly HashSet<string> Reserved = new(Keywords.Concat(DataTypes), StringComparer.OrdinalIgnoreCase);

    public static bool IsKeyword(string word) => Reserved.Contains(word);
}
