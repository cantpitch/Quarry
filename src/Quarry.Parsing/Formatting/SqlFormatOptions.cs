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

public enum BlockLayout
{
    /// <summary>BEGIN on its own line, lined up with IF / ELSE / WHILE.</summary>
    Aligned,

    /// <summary>BEGIN at the end of the IF / ELSE / WHILE line, and "END ELSE BEGIN".</summary>
    SameLine,

    /// <summary>BEGIN and END indented one level under IF / ELSE / WHILE, the body one more.</summary>
    Indented,
}

public enum CaseLayout
{
    /// <summary>WHEN / ELSE on their own lines, indented one level; END lined up with CASE.</summary>
    Indented,

    /// <summary>The first WHEN stays on the CASE line and the others line up under it; END lined up with CASE.</summary>
    Aligned,

    /// <summary>The whole expression on one line.</summary>
    SingleLine,
}

public sealed record SqlFormatOptions
{
    public static SqlFormatOptions Default { get; } = new();

    public KeywordCase KeywordCase { get; init; } = KeywordCase.Upper;

    /// <summary>Case of built-in data type names (int, varchar(max), …).</summary>
    public KeywordCase DataTypeCase { get; init; } = KeywordCase.Lower;

    public ClauseLayout ClauseLayout { get; init; } = ClauseLayout.Aligned;

    /// <summary>SELECT columns and UPDATE SET assignments.</summary>
    public ListLayout ListLayout { get; init; } = ListLayout.OnePerLine;

    public CommaPlacement CommaPlacement { get; init; } = CommaPlacement.Trailing;

    /// <summary>Start each AND/OR of a WHERE or HAVING clause on a new line.</summary>
    public bool WhereConditionsOnNewLines { get; init; } = true;

    /// <summary>Where the extra AND/OR conditions of a JOIN … ON go.</summary>
    public ConditionLayout JoinConditions { get; init; } = ConditionLayout.UnderFirstCondition;

    /// <summary>Where the extra AND/OR conditions of IF and WHILE go.</summary>
    public ConditionLayout ControlFlowConditions { get; init; } = ConditionLayout.BodyColumn;

    /// <summary>Where BEGIN / END go after IF, ELSE and WHILE.</summary>
    public BlockLayout BlockLayout { get; init; } = BlockLayout.Aligned;

    public CaseLayout CaseLayout { get; init; } = CaseLayout.Indented;

    /// <summary>Keep a CASE with a single WHEN (and no nested CASE or subquery) on one line.</summary>
    public bool SingleWhenCaseOnOneLine { get; init; } = true;

    /// <summary>In CREATE TABLE (and table variables / table types), line up column names, data types and the rest.</summary>
    public bool AlignColumnDefinitions { get; init; } = true;

    /// <summary>Put the "(" of a table definition on its own line instead of after the table name.</summary>
    public bool TableParenthesisOnOwnLine { get; init; } = true;

    public int IndentSize { get; init; } = 4;

    public string BatchSeparator { get; init; } = BatchSplitter.DefaultSeparator;
}
