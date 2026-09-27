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
- **Format SQL (Ctrl+Shift+F):** formats the selection, or the whole script. It's one undo step, and the cursor stays on the same code.
  - **Default style:** in each query block, clause bodies line up one space after the longest clause keyword (so `LEFT JOIN` sets the column). Each column is on its own line, AND/OR start new lines, and subqueries are laid out in place.
  - **Control flow:** IF/ELSE, WHILE, BEGIN…END and TRY/CATCH bodies are indented, and IF/WHILE conditions split at AND/OR. CASE expressions put each WHEN and ELSE on its own line, with END lined up under CASE. A CASE with a single WHEN stays on one line.
  - **CREATE TABLE** (and `DECLARE @t TABLE`, `CREATE TYPE … AS TABLE`): one column, constraint or index per line, with column names, data types and the rest of each definition lined up. Built-in data types are lowercase by default, everywhere they appear.
  - **Settings > Formatting:** keyword case, aligned, compact or indented clauses, list layout, comma placement, JOIN … ON and IF/WHILE conditions, BEGIN…END placement (own line, same line or indented), CASE layout, CREATE TABLE layout, data type case and indent size, with a live preview.
  - **Safety:** only whitespace and keyword case ever change. Every batch is re-parsed and its tokens compared after formatting. A batch with a syntax error, or one that can't be formatted safely, is left exactly as it was.
- **Output modes:** Grid, Text (like SSMS "results to text"), CSV and TSV. Switching modes doesn't re-run the query.
- **Export Results (Ctrl+Shift+E):** saves the loaded results as CSV, Excel (`.xlsx`), TSV, JSON or fixed-width text. The file type you choose sets the format. Right-click a grid to export just that result set.
  - **CSV, TSV and JSON:** one file per result set (`name.csv`, `name_2.csv`, …).
  - **Excel:** one worksheet per result set, with typed numbers and dates and a frozen header row.
  - **Text:** all result sets in one file.
- **Run Script to File (Shift+F5) / Run Query to File:** streams results straight from the server into the file without loading the grid. The grid row cap doesn't apply and memory use stays flat, so this is the way to extract large results.
- **Query history:** the History tab next to the object explorer lists everything you've run, newest first. Each entry shows the SQL that was sent, server, database, duration, row count and whether it succeeded.
  - Search to filter entries, and double-click one to reopen it in a new tab on the same server and database.
  - Right-click to run it again, insert it at the cursor, copy it, or delete it.
  - History is saved in `history.jsonl` in Quarry's data folder and keeps the newest 5,000 entries. It can be turned off in Settings or cleared from the panel.
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
