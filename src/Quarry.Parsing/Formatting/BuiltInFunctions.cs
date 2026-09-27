namespace Quarry.Parsing.Formatting;

/// <summary>
/// Built-in function names. Most are not reserved words (the lexer reports them as identifiers),
/// so the formatter only changes their case when they are called, i.e. followed by "(".
/// </summary>
public static class BuiltInFunctions
{
    public static readonly IReadOnlyList<string> Names =
    [
        "ABS", "ACOS", "APP_NAME", "APPROX_COUNT_DISTINCT", "ASCII", "ASIN", "ATAN", "ATN2", "AVG", "CAST", "CEILING",
        "CHAR", "CHARINDEX", "CHECKSUM", "CHOOSE", "COALESCE", "COL_NAME", "COLUMNPROPERTY", "COMPRESS", "CONCAT",
        "CONCAT_WS", "CONVERT", "COS", "COT", "COUNT", "COUNT_BIG", "CUME_DIST", "CURRENT_TIMESTAMP", "CURRENT_USER",
        "DATALENGTH", "DATEADD", "DATEDIFF", "DATEDIFF_BIG", "DATEFROMPARTS", "DATENAME", "DATEPART", "DATETRUNC",
        "DATETIME2FROMPARTS", "DATETIMEFROMPARTS", "DAY", "DB_ID", "DB_NAME", "DECOMPRESS", "DEGREES", "DENSE_RANK",
        "DIFFERENCE", "EOMONTH", "ERROR_LINE", "ERROR_MESSAGE", "ERROR_NUMBER", "ERROR_PROCEDURE", "ERROR_SEVERITY",
        "ERROR_STATE", "EXP", "FIRST_VALUE", "FLOOR", "FORMAT", "FORMATMESSAGE", "GETDATE", "GETUTCDATE", "GREATEST",
        "GROUPING", "GROUPING_ID", "HASHBYTES", "HOST_NAME", "IIF", "ISDATE", "ISJSON", "ISNULL", "ISNUMERIC",
        "JSON_ARRAY", "JSON_MODIFY", "JSON_OBJECT", "JSON_QUERY", "JSON_VALUE", "LAG", "LAST_VALUE", "LEAD", "LEAST",
        "LEN", "LOG", "LOG10", "LOWER", "LTRIM", "MAX", "MIN", "MONTH", "NCHAR", "NEWID", "NEWSEQUENTIALID", "NTILE",
        "NULLIF", "OBJECT_DEFINITION", "OBJECT_ID", "OBJECT_NAME", "OBJECT_SCHEMA_NAME", "OBJECTPROPERTY",
        "PARSE", "PATINDEX", "PERCENT_RANK", "PERCENTILE_CONT", "PERCENTILE_DISC", "PI", "POWER", "QUOTENAME",
        "RADIANS", "RAND", "RANK", "REPLACE", "REPLICATE", "REVERSE", "ROUND", "ROW_NUMBER", "RTRIM", "SCHEMA_ID",
        "SCHEMA_NAME", "SCOPE_IDENTITY", "SERVERPROPERTY", "SESSION_USER", "SIGN", "SIN", "SOUNDEX", "SPACE", "SQRT",
        "SQUARE", "STDEV", "STDEVP", "STR", "STRING_AGG", "STRING_ESCAPE", "STRING_SPLIT", "STUFF", "SUBSTRING", "SUM",
        "SUSER_NAME", "SUSER_SNAME", "SWITCHOFFSET", "SYSDATETIME", "SYSDATETIMEOFFSET", "SYSTEM_USER",
        "SYSUTCDATETIME", "TAN", "TIMEFROMPARTS", "TODATETIMEOFFSET", "TRANSLATE", "TRIM", "TRY_CAST", "TRY_CONVERT",
        "TRY_PARSE", "TYPE_ID", "TYPE_NAME", "UNICODE", "UPPER", "USER_ID", "USER_NAME", "VAR", "VARP", "XACT_STATE",
        "YEAR", "GENERATE_SERIES", "DATE_BUCKET",
    ];

    private static readonly HashSet<string> Set = new(Names, StringComparer.OrdinalIgnoreCase);

    public static bool Contains(string name) => Set.Contains(name);
}
