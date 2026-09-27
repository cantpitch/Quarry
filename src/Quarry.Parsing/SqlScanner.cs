namespace Quarry.Parsing;

public enum SqlRegionKind
{
    LineComment,
    BlockComment,
    String,
    QuotedIdentifier,
    BracketIdentifier,
}

/// <summary>A region of text where T-SQL delimiters (GO, semicolons, blank lines) have no meaning.</summary>
public readonly record struct SqlRegion(SqlRegionKind Kind, TextSpan Span);

/// <summary>
/// Minimal T-SQL lexer that finds comments, strings and delimited identifiers.
/// Everything outside the returned regions is ordinary code.
/// </summary>
public static class SqlScanner
{
    public static List<SqlRegion> Scan(string text)
    {
        var regions = new List<SqlRegion>();
        int i = 0;
        int n = text.Length;

        while (i < n)
        {
            char c = text[i];
            char next = i + 1 < n ? text[i + 1] : '\0';

            if (c == '-' && next == '-')
            {
                int start = i;
                i += 2;
                while (i < n && text[i] != '\n' && text[i] != '\r')
                    i++;
                regions.Add(new SqlRegion(SqlRegionKind.LineComment, TextSpan.FromBounds(start, i)));
            }
            else if (c == '/' && next == '*')
            {
                int start = i;
                int depth = 1;
                i += 2;
                while (i < n && depth > 0)
                {
                    if (text[i] == '/' && i + 1 < n && text[i + 1] == '*')
                    {
                        depth++;
                        i += 2;
                    }
                    else if (text[i] == '*' && i + 1 < n && text[i + 1] == '/')
                    {
                        depth--;
                        i += 2;
                    }
                    else
                    {
                        i++;
                    }
                }
                regions.Add(new SqlRegion(SqlRegionKind.BlockComment, TextSpan.FromBounds(start, i)));
            }
            else if (c == '\'')
            {
                i = ScanDelimited(text, i, '\'', SqlRegionKind.String, regions);
            }
            else if (c == '"')
            {
                i = ScanDelimited(text, i, '"', SqlRegionKind.QuotedIdentifier, regions);
            }
            else if (c == '[')
            {
                i = ScanDelimited(text, i, ']', SqlRegionKind.BracketIdentifier, regions);
            }
            else
            {
                i++;
            }
        }

        return regions;
    }

    /// <summary>Scans from an opening delimiter to its close; a doubled close delimiter is an escape.</summary>
    private static int ScanDelimited(string text, int start, char close, SqlRegionKind kind, List<SqlRegion> regions)
    {
        int i = start + 1;
        int n = text.Length;
        while (i < n)
        {
            if (text[i] == close)
            {
                if (i + 1 < n && text[i + 1] == close)
                {
                    i += 2;
                    continue;
                }
                i++;
                break;
            }
            i++;
        }
        i = Math.Min(i, n);
        regions.Add(new SqlRegion(kind, TextSpan.FromBounds(start, i)));
        return i;
    }

    /// <summary>
    /// True when <paramref name="offset"/> lies strictly inside a region: the region starts
    /// before it and ends after it. <paramref name="regions"/> must be sorted by start.
    /// </summary>
    public static bool IsInsideRegion(IReadOnlyList<SqlRegion> regions, int offset)
    {
        int lo = 0, hi = regions.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            var span = regions[mid].Span;
            if (span.End <= offset)
                lo = mid + 1;
            else if (span.Start >= offset)
                hi = mid - 1;
            else
                return true;
        }
        return false;
    }

    /// <summary>Returns the region containing <paramref name="offset"/> (start inclusive), if any.</summary>
    public static SqlRegion? RegionAt(IReadOnlyList<SqlRegion> regions, int offset)
    {
        int lo = 0, hi = regions.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            var span = regions[mid].Span;
            if (span.End <= offset)
                lo = mid + 1;
            else if (span.Start > offset)
                hi = mid - 1;
            else
                return regions[mid];
        }
        return null;
    }
}
