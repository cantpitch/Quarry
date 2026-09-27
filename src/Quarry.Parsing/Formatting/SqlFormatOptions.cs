namespace Quarry.Parsing.Formatting;

public enum KeywordCase
{
    Upper,
    Lower,
    Preserve,
}

public enum ClauseLayout
{
    /// <summary>Clause bodies line up one space after the longest clause keyword in the query block.</summary>
    Aligned,

    /// <summary>One space after each keyword; continuation lines are indented.</summary>
    Compact,

    /// <summary>Keyword on its own line, body indented on the lines below.</summary>
    Indented,
}

public enum ListLayout
{
    OnePerLine,
    SingleLine,
}

public enum CommaPlacement
{
    Trailing,
    Leading,
}

public enum ConditionLayout
{
    /// <summary>Extra conditions on new lines, with the operand lined up under the first condition.</summary>
    UnderFirstCondition,

    /// <summary>All conditions stay on one line.</summary>
    SameLine,

    /// <summary>Extra conditions on new lines at the clause body column.</summary>
    BodyColumn,
}

public sealed record SqlFormatOptions
{
    public static SqlFormatOptions Default { get; } = new();

    public KeywordCase KeywordCase { get; init; } = KeywordCase.Upper;

    public ClauseLayout ClauseLayout { get; init; } = ClauseLayout.Aligned;

    /// <summary>SELECT columns and UPDATE SET assignments.</summary>
    public ListLayout ListLayout { get; init; } = ListLayout.OnePerLine;

    public CommaPlacement CommaPlacement { get; init; } = CommaPlacement.Trailing;

    /// <summary>Start each AND/OR of a WHERE or HAVING clause on a new line.</summary>
    public bool WhereConditionsOnNewLines { get; init; } = true;

    /// <summary>Where the extra AND/OR conditions of a JOIN … ON go.</summary>
    public ConditionLayout JoinConditions { get; init; } = ConditionLayout.UnderFirstCondition;

    public int IndentSize { get; init; } = 4;

    public string BatchSeparator { get; init; } = BatchSplitter.DefaultSeparator;
}
