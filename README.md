# Quarry

[![CI](https://github.com/cantpitch/Quarry/actions/workflows/ci.yml/badge.svg)](https://github.com/cantpitch/Quarry/actions/workflows/ci.yml)

A cross-platform SQL Server query tool for developers and analysts, built with C# and Avalonia. It runs on Windows, macOS and Linux.

## Features
- **Object explorer:** connect to several servers at once. Each server has its own tree: databases, tables, views, stored procedures, functions and synonyms, down to columns, indexes and parameters. The tree has a filter box.
- **Context menus:** Select Top 1000, Script as CREATE/EXECUTE, Copy Name. Double-click a node to insert its name into the editor.
- **Query editor:** T-SQL syntax highlighting (light and dark), completion for keywords, objects and columns (after `alias.`), and underlines for syntax errors.
- **Run Script (F5 / Ctrl+E):** runs the whole file, split on `GO` lines. `GO n` repeats the batch n times, and a `GO` inside a string or comment is ignored.
- **Run Query (Ctrl+Enter):** runs only the statement under the cursor. Statements are found by the T-SQL parser, so they don't need semicolons or `GO`. The statement that will run is highlighted as you move the cursor.
- **Selections:** if text is selected, both commands run the selection instead.
- **Output modes:** Grid, Text (like SSMS "results to text"), CSV and TSV. Switching modes doesn't re-run the query.
- **Export Results (Ctrl+Shift+E):** saves the loaded results as CSV, Excel (`.xlsx`), TSV, JSON or fixed-width text. The file type you choose sets the format. Right-click a grid to export just that result set.
  - **CSV, TSV and JSON:** one file per result set (`name.csv`, `name_2.csv`, …).
  - **Excel:** one worksheet per result set, with typed numbers and dates and a frozen header row.
  - **Text:** all result sets in one file.
- **Run Script to File (Shift+F5) / Run Query to File:** streams results straight from the server into the file without loading the grid. The grid row cap doesn't apply and memory use stays flat, so this is the way to extract large results.
- **Messages pane:** shows PRINT output, errors and row counts. Double-click an error to jump to its line.
- **Connections:** SQL Server authentication, Windows authentication (Windows only), and Microsoft Entra ID (interactive/MFA, default credential, service principal, device code).
- **Saved passwords:** kept in the OS keychain (Windows Credential Manager, macOS Keychain, or libsecret on Linux).
- **Per-tab sessions:** each query tab keeps its own connection open, so temp tables, `SET` options and `USE` carry over between runs.

## Build and run
You need the .NET 10 SDK.

```bash
dotnet run --project src/Quarry.App
```

## Tests
```bash
dotnet test
```

Integration and UI tests need a SQL Server. Set `QUARRY_TEST_CONNECTION` to a connection string, or have Docker running so the tests can start a SQL Server 2022 container themselves. They only create temp tables and a throwaway table in tempdb. Without a server, those tests are skipped.

```bash
export QUARRY_TEST_CONNECTION="Server=localhost;Integrated Security=true;TrustServerCertificate=true"
```

The headless UI tests save rendered screenshots to `QUARRY_UI_SNAPSHOTS`, or to a temp folder if that isn't set.

## Publish
```bash
dotnet publish src/Quarry.App -c Release -r win-x64 --self-contained
```

Replace `win-x64` with `osx-arm64`, `osx-x64` or `linux-x64` for the other platforms. On Linux, saving passwords requires libsecret (`libsecret-1-0`) and a running Secret Service such as GNOME Keyring or KWallet.

## Layout
| Project | Contents |
|---|---|
| `src/Quarry.Parsing` | GO batch splitter, ScriptDom statement locator, and the logic that decides what to execute |
| `src/Quarry.Core` | Connections, credential stores, query executor, result formatters, metadata |
| `src/Quarry.App` | The Avalonia UI |
| `tests/*` | xUnit tests; `Quarry.App.Tests` renders the UI headlessly |
