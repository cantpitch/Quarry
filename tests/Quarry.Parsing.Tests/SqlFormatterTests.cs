using Quarry.Parsing.Formatting;

namespace Quarry.Parsing.Tests;

public class SqlFormatterTests
{
    private static string Format(string sql, SqlFormatOptions? options = null)
    {
        var result = SqlFormatter.Format(sql, options);
        Assert.Equal(0, result.SkippedBatches);
        // Formatting is idempotent.
        Assert.Equal(result.Text, SqlFormatter.Format(result.Text, options).Text);
        return result.Text;
    }

    private static string Lines(params string[] lines) => string.Join("\n", lines);

    [Fact]
    public void PreferredStyle_FromMessyInput()
    {
        const string input = "select a.* from Table1 a join Table2 b on a.Id = b.Id left join Table3 c on a.OtherId = c.OtherId " +
                             "where a.Something = 5 and not exists (select 1 from Table4 d where d.Table4Id = a.Table4Id)";
        Assert.Equal(Lines(
            "SELECT    a.*",
            "FROM      Table1 a",
            "JOIN      Table2 b ON a.Id = b.Id",
            "LEFT JOIN Table3 c ON a.OtherId = c.OtherId",
            "WHERE     a.Something = 5",
            "          AND NOT EXISTS (SELECT 1",
            "                          FROM   Table4 d",
            "                          WHERE  d.Table4Id = a.Table4Id)"),
            Format(input));
    }

    [Fact]
    public void SelectList_OnePerLine_TrailingCommas()
    {
        // GROUP BY is the longest clause keyword, so it sets the body column.
        Assert.Equal(Lines(
            "SELECT   a.Id,",
            "         a.Name,",
            "         COUNT(*) AS Total",
            "FROM     Orders a",
            "GROUP BY a.Id, a.Name"),
            Format("SELECT a.Id, a.Name, count(*) AS Total FROM Orders a GROUP BY a.Id, a.Name"));
    }

    [Fact]
    public void SelectList_LeadingCommas_AndSingleLine()
    {
        var leading = SqlFormatOptions.Default with { CommaPlacement = CommaPlacement.Leading };
        Assert.Equal(Lines(
            "SELECT a,",
            "       b",
            "FROM   t").Replace("a,\n       b", "a\n     , b"),
            Format("select a, b from t", leading));

        var single = SqlFormatOptions.Default with { ListLayout = ListLayout.SingleLine };
        Assert.Equal(Lines("SELECT a, b", "FROM   t"), Format("select a,b from t", single));
    }

    [Fact]
    public void JoinConditions_UnderFirstCondition()
    {
        Assert.Equal(Lines(
            "SELECT *",
            "FROM   Table1 a",
            "JOIN   Table2 b ON a.Id = b.Id",
            "               AND a.Type = b.Type",
            "                OR a.X = b.X"),
            Format("SELECT * FROM Table1 a JOIN Table2 b ON a.Id = b.Id AND a.Type = b.Type OR a.X = b.X"));
    }

    [Fact]
    public void JoinConditions_OtherLayouts()
    {
        const string sql = "SELECT * FROM a JOIN b ON a.Id = b.Id AND a.T = b.T";
        Assert.Equal(Lines("SELECT *", "FROM   a", "JOIN   b ON a.Id = b.Id AND a.T = b.T"),
            Format(sql, SqlFormatOptions.Default with { JoinConditions = ConditionLayout.SameLine }));
        Assert.Equal(Lines("SELECT *", "FROM   a", "JOIN   b ON a.Id = b.Id", "       AND a.T = b.T"),
            Format(sql, SqlFormatOptions.Default with { JoinConditions = ConditionLayout.BodyColumn }));
    }

    [Fact]
    public void BetweenAnd_AndCaseAnd_AreNotSplit()
    {
        Assert.Equal(Lines(
            "SELECT CASE WHEN x > 1 AND y < 2 THEN 1 ELSE 0 END AS f",
            "FROM   t",
            "WHERE  d BETWEEN 1 AND 5",
            "       AND (a = 1 OR b = 2)"),
            Format("select case when x>1 and y<2 then 1 else 0 end as f from t where d between 1 and 5 and (a=1 or b=2)"));
    }

    [Fact]
    public void SelectModifiers_StayOnSelectLine()
    {
        Assert.Equal(Lines(
            "SELECT   DISTINCT TOP (10) a,",
            "         b",
            "FROM     t",
            "ORDER BY a DESC"),
            Format("select distinct top (10) a, b from t order by a desc"));
    }

    [Fact]
    public void Cte_UnionAndDerivedTable()
    {
        Assert.Equal(Lines(
            "WITH x AS (SELECT 1 AS n",
            "           UNION ALL",
            "           SELECT n + 1",
            "           FROM   x",
            "           WHERE  n < 10),",
            "     y AS (SELECT n",
            "           FROM   x)",
            "SELECT d.n",
            "FROM   (SELECT n",
            "        FROM   y) d"),
            Format("with x as (select 1 as n union all select n+1 from x where n<10), y as (select n from x) select d.n from (select n from y) d"));
    }

    [Fact]
    public void InsertUpdateDelete()
    {
        Assert.Equal(Lines(
            "INSERT INTO dbo.T (a, b)",
            "VALUES      (1, 'x'),",
            "            (2, 'y')"),
            Format("insert into dbo.T (a, b) values (1,'x'),(2,'y')"));

        Assert.Equal(Lines(
            "UPDATE t",
            "SET    a = 1,",
            "       b = b + 1",
            "FROM   dbo.T t",
            "JOIN   dbo.U u ON u.Id = t.Id",
            "WHERE  u.Flag = 1"),
            Format("update t set a = 1, b = b+1 from dbo.T t join dbo.U u on u.Id = t.Id where u.Flag = 1"));

        Assert.Equal(Lines(
            "DELETE FROM dbo.T",
            "WHERE       Id = -1"),
            Format("delete from dbo.T where Id = - 1"));
    }

    [Fact]
    public void CompactAndIndentedLayouts()
    {
        const string sql = "select a, b from t join u on t.Id = u.Id where x = 1 and y = 2";
        Assert.Equal(Lines(
            "SELECT a,",
            "    b",
            "FROM t",
            "JOIN u ON t.Id = u.Id",
            "WHERE x = 1",
            "    AND y = 2"),
            Format(sql, SqlFormatOptions.Default with { ClauseLayout = ClauseLayout.Compact }));

        Assert.Equal(Lines(
            "SELECT",
            "    a,",
            "    b",
            "FROM",
            "    t",
            "    JOIN u ON t.Id = u.Id",
            "WHERE",
            "    x = 1",
            "    AND y = 2"),
            Format(sql, SqlFormatOptions.Default with { ClauseLayout = ClauseLayout.Indented }));
    }

    [Fact]
    public void KeywordCasing()
    {
        Assert.Equal(Lines("select count(*)", "from   t", "where  x is not null"),
            Format("SELECT COUNT(*) FROM t WHERE x IS NOT NULL", SqlFormatOptions.Default with { KeywordCase = KeywordCase.Lower }));
        Assert.Equal(Lines("Select Count(*)", "from   t"),
            Format("Select Count(*) from t", SqlFormatOptions.Default with { KeywordCase = KeywordCase.Preserve }));
    }

    [Fact]
    public void IdentifiersAndStringsAreNeverRecased()
    {
        // "count" is a column here, [Select] is bracketed, t.select is not allowed but t.[from] is.
        Assert.Equal(Lines("SELECT count,", "       [Select],", "       t.[from],", "       'select * from x'", "FROM   dbo.t"),
            Format("select count, [Select], t.[from], 'select * from x' from dbo.t"));
    }

    [Fact]
    public void Comments_ArePreserved()
    {
        Assert.Equal(Lines(
            "-- header",
            "SELECT a, -- first",
            "       /* second */ b",
            "FROM   t -- trailing"),
            Format("-- header\nselect a, -- first\n /* second */ b from t -- trailing"));
    }

    [Fact]
    public void LineCommentInsideClause_ForcesLineBreak()
    {
        string formatted = Format("select a from t where x = 1 -- why\n and y = 2");
        Assert.Contains("-- why\n", formatted);
        Assert.Contains("AND y = 2", formatted);
    }

    [Fact]
    public void Batches_Blocks_AndProcedures()
    {
        const string input = """
            create procedure dbo.p @id int
            as
            begin
            set nocount on
            if exists (select 1 from t where id = @id) begin select * from t where id = @id end
            else select 0
            end
            go
            exec dbo.p 1
            """;
        Assert.Equal(Lines(
            "CREATE PROCEDURE dbo.p @id int",
            "AS",
            "BEGIN",
            "    SET NOCOUNT ON",
            "    IF EXISTS (SELECT 1",
            "               FROM   t",
            "               WHERE  id = @id)",
            "    BEGIN",
            "        SELECT *",
            "        FROM   t",
            "        WHERE  id = @id",
            "    END",
            "    ELSE",
            "        SELECT 0",
            "END",
            "GO",
            "EXEC dbo.p 1"),
            Format(input));
    }

    [Fact]
    public void TryCatch_AndWhile()
    {
        Assert.Equal(Lines(
            "BEGIN TRY",
            "    WHILE @i < 10",
            "        SET @i += 1",
            "END TRY",
            "BEGIN CATCH",
            "    SELECT ERROR_MESSAGE()",
            "END CATCH"),
            Format("begin try while @i < 10 set @i += 1 end try begin catch select error_message() end catch"));
    }

    [Fact]
    public void BlankLinesBetweenStatements_CollapseToOne()
    {
        Assert.Equal(Lines("SELECT 1", "", "SELECT 2", "SELECT 3;"),
            Format("select 1\n\n\n\nselect 2\nselect 3;"));
    }

    [Fact]
    public void BatchWithSyntaxError_IsLeftUnchanged()
    {
        var result = SqlFormatter.Format("select 1 from t\nGO\nselect from where\nGO\nselect 2");
        Assert.Equal(1, result.SkippedBatches);
        Assert.Equal(Lines("SELECT 1", "FROM   t", "GO", "select from where", "GO", "SELECT 2"), result.Text);
        Assert.Contains("Batch 2", result.Problems.Single());
    }

    [Fact]
    public void CrLfIsKept()
    {
        Assert.Equal("SELECT a\r\nFROM   t\r\n", Format("select a\r\nfrom t\r\n"));
    }

    [Fact]
    public void OtherStatementsKeepTheirLayout()
    {
        Assert.Equal(Lines("DECLARE @x int = 5,", "        @y int = 6", "SET @x = @y"),
            Format("declare @x int = 5,\n        @y int = 6\nset @x = @y"));
    }

    [Fact]
    public void RealisticScript_FormatsEveryBatch()
    {
        const string script = """
            -- Monthly report
            set nocount on;
            declare @from date = '2024-01-01', @to date = dateadd(month, 1, '2024-01-01');

            /* totals per customer */
            with sales as (
                select o.CustomerId, sum(ol.Qty * ol.Price) as Total, count(distinct o.Id) as Orders -- revenue
                from dbo.Orders o
                inner join dbo.OrderLines ol on ol.OrderId = o.Id and ol.Deleted = 0
                where o.OrderDate >= @from and o.OrderDate < @to
                group by o.CustomerId having sum(ol.Qty) > 0
            )
            select c.Name, s.Total, s.Orders,
                   rank() over (order by s.Total desc) as Rnk,
                   (select max(o2.OrderDate) from dbo.Orders o2 where o2.CustomerId = c.Id) as LastOrder,
                   case when s.Total > 1000 then 'gold' when s.Total > 100 then 'silver' else 'bronze' end as Tier
            from sales s
            join dbo.Customers c on c.Id = s.CustomerId
            outer apply (select top 1 a.City from dbo.Addresses a where a.CustomerId = c.Id order by a.Id desc) addr
            where c.Id in (select CustomerId from dbo.Active) or c.Vip = 1
            order by s.Total desc;
            go
            select * from (select Region, Year, Amount from dbo.Sales) src
            pivot (sum(Amount) for Year in ([2023], [2024])) p;

            declare c cursor local fast_forward for select Id from dbo.T;
            open c; fetch next from c into @id;
            while @@fetch_status = 0
            begin
                exec dbo.Process @id = @id, @mode = N'full'; -- one at a time
                fetch next from c into @id;
            end
            close c; deallocate c;
            go
            create table #t (Id int not null primary key, Name nvarchar(50) null);
            insert #t (Id, Name) select Id, Name from dbo.Customers where Name like N'A%';
            update #t set Name = upper(Name) output inserted.Id, deleted.Name into @log where Id > 10;
            """;
        var result = SqlFormatter.Format(script);
        Assert.Empty(result.Problems);
        Assert.True(SqlFormatter.SameTokens(script.Replace("\ngo\n", "\n"), result.Text.Replace("\nGO\n", "\n").Replace("\ngo\n", "\n")));
        Assert.Equal(result.Text, SqlFormatter.Format(result.Text).Text);
    }

    [Theory]
    [InlineData("SELECT a FROM t WHERE a >= 1 AND b <> 2 AND c != 3 AND d !< 4")]
    [InlineData("SELECT -1, +2, ~3, a - -1, a * -b FROM t")]
    [InlineData("SELECT CAST(x AS varchar(10)), CONVERT(int, y), LEFT(z, 2), t.* FROM t")]
    [InlineData("SELECT * FROM t WITH (NOLOCK) CROSS APPLY OPENJSON(t.j) WITH (a int '$.a') j OPTION (RECOMPILE)")]
    [InlineData("SELECT a, ROW_NUMBER() OVER (PARTITION BY b ORDER BY c) rn FROM t ORDER BY a OFFSET 10 ROWS FETCH NEXT 5 ROWS ONLY")]
    [InlineData("SELECT name FROM sys.objects FOR XML PATH('')")]
    [InlineData("SELECT * INTO #t FROM t; INSERT #t (a) SELECT a FROM u; DELETE t FROM t JOIN u ON u.id = t.id")]
    [InlineData("MERGE t USING s ON t.id = s.id WHEN MATCHED THEN UPDATE SET v = s.v;")]
    [InlineData("SELECT 1 /* a\n   multi-line\n comment */ FROM t")]
    [InlineData("SELECT N'it''s', 0x1F, 1.5e3, $12.50, @@ROWCOUNT, geography::Point(1, 2, 4326)")]
    [InlineData("IF 1 = 1 SELECT 1 ELSE IF 2 = 2 SELECT 2 ELSE BEGIN SELECT 3 END")]
    [InlineData("CREATE VIEW v AS SELECT a FROM t WITH CHECK OPTION")]
    [InlineData("SELECT a FROM t EXCEPT SELECT a FROM u INTERSECT (SELECT a FROM w)")]
    public void Formats_WithoutChangingTokens(string sql)
    {
        var result = SqlFormatter.Format(sql);
        Assert.Equal(0, result.SkippedBatches);
        Assert.True(SqlFormatter.SameTokens(sql, result.Text));
        Assert.Equal(result.Text, SqlFormatter.Format(result.Text).Text);
    }
}
