using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Data.SqlClient;
using Quarry.App.Services;
using Quarry.App.ViewModels;
using Quarry.App.Views;
using Quarry.Core.Connections;
using Quarry.Core.Results;

namespace Quarry.App.Tests;

/// <summary>
/// Renders the screenshots in the README against a demo database. Runs only when
/// QUARRY_README_SCREENSHOTS (the output folder) and QUARRY_TEST_CONNECTION are set, and creates
/// the QuarryDemo database on that server, so point it at a throwaway one (CI does).
/// </summary>
public partial class UiTests
{
    private const string DemoDatabase = "QuarryDemo";

    [AvaloniaFact]
    public async Task ReadmeScreenshots_Render()
    {
        if (Environment.GetEnvironmentVariable("QUARRY_README_SCREENSHOTS") is not { Length: > 0 } outDir || TestProfile() is not var (testProfile, secret))
            return;
        Directory.CreateDirectory(outDir);
        await CreateDemoDatabaseAsync(testProfile, secret);

        // Not connected up front: that reads the login, which would then show next to the server name.
        var profile = testProfile with { Name = "demo-sql" };
        var server = new ServerConnection(profile, secret);

        var window = new MainWindow { Width = 1400, Height = 860 };
        window.Show();
        var vm = window.ViewModel;
        var serverNode = new ServerNode(server, vm) { IsExpanded = true };
        vm.ExplorerRoots.Add(serverNode);
        vm.Servers.Add(server);

        // A second tab behind the main one.
        await vm.NewQueryAsync(server, DemoDatabase, "EXEC Sales.usp_CustomerOrders @CustomerId = 3;");

        // The main tab: a query typed on one line, formatted, saved and run.
        var doc = await vm.NewQueryAsync(server, DemoDatabase);
        var editor = EditorOf(window);
        editor.Document.Text =
            "-- Best customers by revenue\n" +
            "select top (15) c.CustomerName, c.City, count(distinct o.OrderId) as Orders, sum(l.Quantity * l.UnitPrice) as Revenue, " +
            "max(o.OrderDate) as LastOrder from Sales.Customers c join Sales.Orders o on o.CustomerId = c.CustomerId " +
            "join Sales.OrderLines l on l.OrderId = o.OrderId where o.Status <> 'Cancelled' group by c.CustomerName, c.City order by Revenue desc";
        await vm.FormatAsync();
        string scratch = Path.Combine(Path.GetTempPath(), $"quarry-readme-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        await doc.SaveFileAsync(Path.Combine(scratch, "top-customers.sql"));
        await vm.RunScriptAsync();
        await WaitUntilAsync(() => !doc.IsExecuting);
        Assert.Equal("Query completed successfully.", doc.StatusText);

        // Explorer: QuarryDemo > Tables > Sales.Customers > Columns. Other user databases on the
        // server are left out of the picture.
        serverNode.IsExpanded = true;
        await LoadedAsync(serverNode);
        foreach (var other in serverNode.Children.OfType<DatabaseNode>().Where(d => d.Database != DemoDatabase).ToList())
            serverNode.Children.Remove(other);
        var database = (DatabaseNode)await ExpandAsync(serverNode, n => n is DatabaseNode { Database: DemoDatabase });
        var tables = await ExpandAsync(database, n => n.Text == "Tables");
        var customers = await ExpandAsync(tables, n => n.Text == "Sales.Customers");
        await ExpandAsync(customers, n => n.Text == "Columns");
        await LoadedAsync(customers.Children.First(n => n.Text == "Columns"));
        Dispatcher.UIThread.RunJobs();
        editor.CaretOffset = 0;
        SaveScreenshot(window, outDir, "query-results");

        doc.OutputMode = OutputMode.Text;
        await WaitUntilAsync(() => doc.TextOutput.Length > 0);
        SaveScreenshot(window, outDir, "results-text");
        doc.OutputMode = OutputMode.Grid;

        // History: a few more runs in another tab, one of them failing.
        var scratchDoc = await vm.NewQueryAsync(server, DemoDatabase);
        editor = EditorOf(window);
        foreach (string sql in new[]
        {
            "SELECT Category, COUNT(*) AS Products, AVG(ListPrice) AS AvgPrice FROM Sales.Products GROUP BY Category;",
            "EXEC Sales.usp_CustomerOrders @CustomerId = 3;",
            "SELECT * FROM Sales.Invoices;",
            "SELECT TOP (100) * FROM Sales.vOrderTotals ORDER BY Total DESC;",
        })
        {
            editor.Document.Text = sql;
            await vm.RunScriptAsync();
            await WaitUntilAsync(() => !scratchDoc.IsExecuting);
        }
        await WaitUntilAsync(() => vm.History.Items.Count >= 5);
        vm.SelectedSidePanel = 1;
        SaveScreenshot(window, outDir, "history");
        window.Close();

        // Settings > Formatting, with its live preview.
        var owner = new Window { Width = 900, Height = 900 };
        owner.Show();
        var settings = new SettingsDialog { DataContext = new SettingsViewModel() };
        _ = settings.ShowDialog(owner);
        settings.FindDescendantOfType<TabControl>()!.SelectedIndex = 1;
        SaveScreenshot(settings, outDir, "formatting-settings");
        settings.Close();

        // Connect dialog with saved connections.
        AppServices.Profiles.Save(profile with { SaveSecret = false }, null);
        var azure = new ConnectionProfile
        {
            Name = "Reporting (Azure SQL)",
            Server = "contoso-reporting.database.windows.net",
            Authentication = AuthenticationKind.EntraInteractive,
            UserName = "analyst@contoso.com",
            Database = "Reporting",
            SaveSecret = false,
        };
        AppServices.Profiles.Save(azure, null);
        var connectVm = new ConnectDialogViewModel();
        connectVm.SelectedProfile = connectVm.Profiles.First(p => p.Id == azure.Id);
        var connect = new ConnectDialog { DataContext = connectVm };
        _ = connect.ShowDialog(owner);
        SaveScreenshot(connect, outDir, "connect");
        connect.Close();
        owner.Close();
        Directory.Delete(scratch, recursive: true);
    }

    private static void SaveScreenshot(Window window, string dir, string name)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
#pragma warning disable CS0618 // see Snapshot
        window.CaptureRenderedFrame()?.Save(Path.Combine(dir, name + ".png"));
#pragma warning restore CS0618
    }

    /// <summary>Waits for a node's children, then expands the matching child and returns it.</summary>
    private static async Task<ExplorerNode> ExpandAsync(ExplorerNode node, Func<ExplorerNode, bool> match)
    {
        node.IsExpanded = true;
        await LoadedAsync(node);
        var child = node.Children.First(match);
        child.IsExpanded = true;
        await LoadedAsync(child);
        return child;
    }

    private static Task LoadedAsync(ExplorerNode node)
        => WaitUntilAsync(() => node.Children.Count > 0 && !(node.Children.Count == 1 && node.Children[0] is MessageNode));

    private static async Task CreateDemoDatabaseAsync(ConnectionProfile profile, string? secret)
    {
        await using (var master = new ServerConnection(profile, secret).CreateConnection("master"))
        {
            await master.OpenAsync();
            await ExecAsync(master, $"IF DB_ID('{DemoDatabase}') IS NOT NULL BEGIN ALTER DATABASE {DemoDatabase} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {DemoDatabase}; END");
            await ExecAsync(master, $"CREATE DATABASE {DemoDatabase}");
        }

        await using var db = new ServerConnection(profile, secret).CreateConnection(DemoDatabase);
        await db.OpenAsync();
        string[] batches =
        [
            "CREATE SCHEMA Sales",
            """
            CREATE TABLE Sales.Customers
            (
                CustomerId   int IDENTITY PRIMARY KEY,
                CustomerName nvarchar(100) NOT NULL,
                City         nvarchar(60)  NOT NULL,
                Country      nvarchar(60)  NOT NULL,
                Email        varchar(200)  NULL,
                CreatedAt    datetime2(0)  NOT NULL DEFAULT '2024-01-15'
            );
            CREATE TABLE Sales.Products
            (
                ProductId   int IDENTITY PRIMARY KEY,
                ProductName nvarchar(100) NOT NULL,
                Category    nvarchar(50)  NOT NULL,
                ListPrice   decimal(10,2) NOT NULL
            );
            CREATE TABLE Sales.Orders
            (
                OrderId    int IDENTITY PRIMARY KEY,
                CustomerId int NOT NULL REFERENCES Sales.Customers,
                OrderDate  date NOT NULL,
                Status     varchar(20) NOT NULL
            );
            CREATE TABLE Sales.OrderLines
            (
                OrderLineId int IDENTITY PRIMARY KEY,
                OrderId     int NOT NULL REFERENCES Sales.Orders,
                ProductId   int NOT NULL REFERENCES Sales.Products,
                Quantity    int NOT NULL,
                UnitPrice   decimal(10,2) NOT NULL
            );
            CREATE INDEX IX_Orders_CustomerId ON Sales.Orders (CustomerId);
            CREATE INDEX IX_OrderLines_OrderId ON Sales.OrderLines (OrderId);
            """,
            """
            CREATE VIEW Sales.vOrderTotals AS
            SELECT o.OrderId, o.CustomerId, o.OrderDate, SUM(l.Quantity * l.UnitPrice) AS Total
            FROM Sales.Orders o JOIN Sales.OrderLines l ON l.OrderId = o.OrderId
            GROUP BY o.OrderId, o.CustomerId, o.OrderDate
            """,
            """
            CREATE PROCEDURE Sales.usp_CustomerOrders @CustomerId int AS
            SELECT OrderId, OrderDate, Total FROM Sales.vOrderTotals WHERE CustomerId = @CustomerId ORDER BY OrderDate DESC
            """,
            """
            CREATE FUNCTION Sales.fn_CustomerRevenue (@CustomerId int) RETURNS decimal(12,2) AS
            BEGIN
                RETURN (SELECT SUM(Total) FROM Sales.vOrderTotals WHERE CustomerId = @CustomerId);
            END
            """,
            """
            INSERT Sales.Customers (CustomerName, City, Country, Email) VALUES
            (N'Alpine Outfitters', N'Denver', N'USA', 'orders@alpine.example'),
            (N'Blue Harbor Books', N'Portland', N'USA', 'hello@blueharbor.example'),
            (N'Cedar & Pine Furniture', N'Vancouver', N'Canada', NULL),
            (N'Driftwood Coffee Roasters', N'Seattle', N'USA', 'beans@driftwood.example'),
            (N'Elm Street Bakery', N'Boston', N'USA', NULL),
            (N'Fjord Design Studio', N'Oslo', N'Norway', 'studio@fjord.example'),
            (N'Granite Peak Climbing', N'Salt Lake City', N'USA', NULL),
            (N'Harbor Light Marine', N'Halifax', N'Canada', 'sales@harborlight.example'),
            (N'Iris Garden Supply', N'Amsterdam', N'Netherlands', NULL),
            (N'Juniper Tea House', N'Kyoto', N'Japan', 'tea@juniper.example'),
            (N'Kestrel Cycles', N'Utrecht', N'Netherlands', NULL),
            (N'Lighthouse Toys', N'Dublin', N'Ireland', 'play@lighthouse.example'),
            (N'Maple Leaf Outdoor', N'Toronto', N'Canada', NULL),
            (N'Northwind Sailing', N'Auckland', N'New Zealand', 'crew@northwind.example'),
            (N'Oak & Iron Hardware', N'Chicago', N'USA', NULL),
            (N'Pebble Beach Surf Co.', N'San Diego', N'USA', 'surf@pebble.example'),
            (N'Quartz Lab Supplies', N'Zurich', N'Switzerland', NULL),
            (N'Redwood Camping', N'Sacramento', N'USA', 'camp@redwood.example'),
            (N'Silverline Audio', N'Berlin', N'Germany', NULL),
            (N'Tundra Gear', N'Reykjavik', N'Iceland', 'gear@tundra.example');

            INSERT Sales.Products (ProductName, Category, ListPrice) VALUES
            (N'Trail Backpack 40L', N'Bags', 129.00), (N'Day Pack 20L', N'Bags', 59.50),
            (N'Down Jacket', N'Clothing', 219.00), (N'Rain Shell', N'Clothing', 149.00),
            (N'Merino Base Layer', N'Clothing', 79.00), (N'Two-Person Tent', N'Camping', 349.00),
            (N'Sleeping Bag -5°C', N'Camping', 189.00), (N'Camp Stove', N'Camping', 64.90),
            (N'Headlamp', N'Accessories', 34.95), (N'Water Filter', N'Accessories', 44.00),
            (N'Trekking Poles', N'Accessories', 89.00), (N'Climbing Harness', N'Climbing', 74.00);

            INSERT Sales.Orders (CustomerId, OrderDate, Status)
            SELECT 1 + ABS(CAST(HASHBYTES('MD5', CONCAT('customer', n)) AS int) % 20),
                   DATEADD(day, -(ABS(CAST(HASHBYTES('MD5', CONCAT('date', n)) AS int) % 540)), '2026-09-15'),
                   CASE n % 12 WHEN 0 THEN 'Cancelled' WHEN 1 THEN 'Pending' ELSE 'Shipped' END
            FROM (SELECT TOP (600) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects) AS numbers;

            INSERT Sales.OrderLines (OrderId, ProductId, Quantity, UnitPrice)
            SELECT o.OrderId, p.ProductId, 1 + ABS(CAST(HASHBYTES('MD5', CONCAT('qty', o.OrderId, '-', p.ProductId)) AS int) % 4), p.ListPrice
            FROM Sales.Orders o JOIN Sales.Products p ON ABS(CAST(HASHBYTES('MD5', CONCAT('line', o.OrderId, '-', p.ProductId)) AS int) % 4) = 0;
            """,
        ];
        foreach (string batch in batches)
            await ExecAsync(db, batch);
    }

    private static async Task ExecAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
