namespace Quarry.Core.Results;

/// <summary>Describes one column of a result set.</summary>
/// <param name="SqlTypeName">SQL Server type name, e.g. "nvarchar" or "datetime2".</param>
/// <param name="Scale">Numeric scale or fractional-seconds precision, when applicable.</param>
public sealed record ColumnInfo(string Name, string SqlTypeName, Type ClrType, bool AllowsNull, int? Size = null, int? Precision = null, int? Scale = null)
{
    /// <summary>Column heading shown to users; unnamed columns (e.g. SELECT 1) get "(No column name)".</summary>
    public string DisplayName => string.IsNullOrEmpty(Name) ? "(No column name)" : Name;
}

/// <summary>A fully materialised result set. Values are CLR values; SQL NULL is <c>null</c>.</summary>
public sealed class ResultSet(IReadOnlyList<ColumnInfo> columns)
{
    public IReadOnlyList<ColumnInfo> Columns { get; } = columns;

    public List<object?[]> Rows { get; } = [];

    /// <summary>True when rows were dropped because of the configured row limit.</summary>
    public bool IsTruncated { get; set; }
}
