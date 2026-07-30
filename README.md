# SQL Server MCP

[![CI](https://github.com/EdmondLu/sqlserver-mcp/actions/workflows/ci.yml/badge.svg)](https://github.com/EdmondLu/sqlserver-mcp/actions/workflows/ci.yml)
[![GitHub release](https://img.shields.io/github/v/release/EdmondLu/sqlserver-mcp)](https://github.com/EdmondLu/sqlserver-mcp/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

A Windows-first, read-only [Model Context Protocol](https://modelcontextprotocol.io/) server for exploring and querying Microsoft SQL Server from Codex and other MCP clients.

[简体中文](README.zh-CN.md)

## Highlights

- 35 focused tools for connection checks, object resolution, schema inspection, dependency/call-graph analysis, deployment validation, guarded diagnostics, and estimated query plans.
- Lazy database connections: startup registers tools but does not connect to SQL Server or scan the database.
- Credentials are read from Windows Credential Manager and are never stored in the JSON config.
- A ScriptDom-based guard accepts one `SELECT`/`WITH` query, or a tightly controlled diagnostic batch limited to variables, local `#temp` tables, temp-table inserts, and a final `SELECT`.
- Every tool returns native MCP `structuredContent` plus a short compatibility text summary and a connection context containing server, database, read-only state, login, timestamp, elapsed time, and isolation level.
- Result row, payload, text-length, lock-wait, command, and connection limits are configurable.
- Bounded tools include a `resultInfo` block that summarizes returned rows/items, limits, truncation reasons, and narrowing hints.
- MCP protocol output stays on stdout; application logs are written to files.

The SQL guard is defense in depth, not a replacement for SQL Server permissions. Always use a dedicated least-privilege login with read-only database access.

## Requirements

- Windows 10/11 or Windows Server
- [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- A reachable SQL Server instance
- An MCP client that supports stdio servers

## Quick Start

1. Download `sqlserver-mcp-win-x64.zip` from the [latest release](https://github.com/EdmondLu/sqlserver-mcp/releases/latest) and extract it, for example to `C:\Tools\sqlserver-mcp`.
2. Copy `sqlserver_mcp.example.json` to `sqlserver_mcp.json` and replace the sample server, database, and credential target.
3. Store the SQL login in Windows Credential Manager:

```powershell
cmdkey /generic:sqlserver-mcp/SampleDb /user:readonly_user /pass
```

4. Register the server in Codex:

```toml
[mcp_servers.sqlserver_mcp]
type = "stdio"
command = 'C:\Tools\sqlserver-mcp\sqlserver_mcp.exe'
args = ["--config", 'C:\Tools\sqlserver-mcp\sqlserver_mcp.json']
startup_timeout_sec = 30
```

Restart the MCP client after changing its configuration.

## SQL Permissions

A practical least-privilege database user normally needs:

```sql
ALTER ROLE db_datareader ADD MEMBER [readonly_user];
GRANT VIEW DEFINITION TO [readonly_user];
GRANT SHOWPLAN TO [readonly_user];
```

`VIEW DEFINITION` enables module and schema inspection. `SHOWPLAN` is required only for `explain_query_plan`. Grant these permissions in the intended user database, not in `master`.

## Configuration

See [`docs/sqlserver_mcp.example.json`](docs/sqlserver_mcp.example.json) for a complete example.

| Setting | Default | Purpose |
| --- | --- | --- |
| `server` | required | SQL Server host or `host,port` |
| `database` | required | Single allowed database |
| `credentialTarget` | required | Windows Credential Manager target |
| `limits.defaultLimit` | `50` | Default returned rows |
| `limits.maxRows` | `500` | Hard row cap |
| `limits.maxResultMb` | `5` | Approximate result-size cap |
| `limits.maxTextLength` | `1000` | Per-value text cap |
| `limits.lockTimeoutMs` | `5000` | SQL lock timeout |
| `limits.commandTimeoutSeconds` | `20` | SQL command timeout |
| `limits.connectTimeoutSeconds` | `10` | Connection timeout |
| `security.allowDmvQueries` | `true` | Allow supported database-scoped DMVs |
| `security.allowServerLevelDmv` | `false` | Allow server-level DMVs |
| `security.allowCrossDatabase` | `false` | Allow three/four-part object references |
| `security.allowSystemDatabases` | `false` | Allow system databases |
| `textSearch.targets` | `[]` | Allow-listed text columns for `search_config_text` |
| `textSearch.snippetLength` | `240` | Snippet length for configured text searches |
| `compare.repoExcludePatterns` | `[]` | Default repository glob patterns to exclude in `compare_module_to_repo` |
| `logging.logSql` | `false` | Include submitted SQL text in file logs |
| `connection.encrypt` | `true` | Encrypt SQL connections |
| `connection.trustServerCertificate` | `false` | Skip certificate-chain validation |
| `connection.applicationIntent` | `ReadOnly` | Set SQL client application intent |

`textSearch.targets` can include optional locator fields such as `keyColumn`, `nameColumn`, `labelColumns`, `createdAtColumn`, `updatedAtColumn`, `createdByColumn`, `updatedByColumn`, and `contentKind`. `search_config_text` searches the configured text column plus key/name/label metadata, returns `matchColumn`, `matchedTerm`, and 1-based `matchStart`, and omits full target metadata unless `includeTargets=true`. If a target has a `usable`, `enabled`, or `active` style column, usable rows are sorted first by default and `usableOnly=true` filters to usable rows.

Relative `logs`, `cache`, and `tmp` directories are created beside the config file. SQL text may contain sensitive data, so enable `logging.logSql` only when appropriate.

## Tools

| Tool | Purpose |
| --- | --- |
| `test_connection` | Validate the connection and current SQL identity |
| `health_check` | Check serverVersion, config, runtime paths, connection, and permissions |
| `find_objects` | Search tables, views, procedures, and functions |
| `resolve_object` | Resolve exact and similar object names, including legacy view-to-table mappings |
| `describe_table` | Inspect compact `shape`, `write_contract`, `keys`, `performance`, or `full` metadata |
| `get_object_overview` | Return compact metadata and dependency context |
| `find_column` | Find tables and views containing a column |
| `profile_column` | Profile NULL/empty values, lengths, format quality, limit saturation, and samples |
| `get_indexes` | Inspect index metadata |
| `get_constraints` | Inspect key, unique, default, and check constraints |
| `get_foreign_keys` | Inspect incoming and outgoing foreign keys |
| `search_sql_modules` | Search SQL module definitions with next-step compare hints |
| `get_module_definition` | Read a module definition, optionally by keyword or line range |
| `validate_tsql_script` | Parse and metadata-check T-SQL without execution or database writes |
| `validate_tsql_file` | Validate a local `.sql` file without executing it |
| `compare_module_to_file` | Compare a database module definition with a known local file |
| `compare_module_to_repo` | Auto-discover matching repository `.sql` files and compare the best unambiguous candidate with the database module |
| `compare_modules_to_files` | Validate and compare an ordered deployment set, including missing targets |
| `analyze_module_temp_tables` | Analyze local temp table creation, usage, multiline statements, and column flow inside a module |
| `get_dependencies` | Find incoming and outgoing dependencies |
| `get_callers` | Find confirmed/static/dynamic callers and caller transaction signals |
| `get_callees` | Find confirmed/static/dynamic callees |
| `get_dependency_graph` | Build a bounded confirmed dependency graph |
| `find_usage` | Rank literal identifier/text/regex usage with source location and confidence |
| `search_config_text` | Search configured application/configuration text and locator metadata with match-column and audit metadata |
| `find_field_consumers` | Combine column, module, and configured page/low-code consumers |
| `find_page_by_table` | Find configured pages that reference a table or view |
| `find_page_by_save_procedure` | Find configured pages that reference a save procedure |
| `run_readonly_query` | Run one guarded read-only query with optional named parameters |
| `run_readonly_batch` | Run a rollback-only diagnostic batch using local `#temp` state |
| `describe_query_result` | Describe guarded query result columns without executing the query, optionally applying explicit UI placeholder replacements |
| `explain_query_plan` | Return an estimated-plan summary, with raw XML only when requested |
| `explain_query_plan_summary` | Return only the compact estimated-plan summary |
| `batch_metadata` | Run independent metadata requests in parallel with per-item errors |
| `reload_connection` | Clear cached credentials and SQL connection pools |

Bounded search/query tools expose stable `cursor`, `nextCursor`, `hasMore`, and executable `nextRequest` metadata where paging is supported. `find_usage` performs literal matching rather than SQL `LIKE`, so `_` in procedure and column names is never treated as a wildcard.

Module/file comparison now reports `exactMatch`, `bodyMatch`, and `semanticMatch`, with `differenceKind` values `exact_match`, `wrapper_only`, `format_only`, `comment_only`, and `body_changed`. `CREATE`/`CREATE OR ALTER`, BOMs, leading/trailing blank lines, session `SET` wrappers, and trailing `GO` do not change `bodyMatch`. Ordered deployment-set comparison also reports `target_missing` and `local_missing`, and validates a missing target's local script before deployment.

`describe_query_result` accepts optional `templateValues` for UI SQL placeholders, for example `{ "0": "1=1" }` replaces `{0}` before describing columns. Replacements are raw SQL fragments, and the final SQL is still parsed by the read-only guard.

Structure tools recognize the legacy view prefixes `vwp_`, `vwpr_`, `vwt_`, and `vwtr_`, and try the corresponding unprefixed physical table first.

Column metadata uses `maxLengthBytes` and `maxLengthCharacters` explicitly. This avoids interpreting SQL Server's byte-based `sys.columns.max_length` as a character count for `nvarchar`/`nchar`.

## Build

```powershell
dotnet restore SqlServerMcp.sln
dotnet test SqlServerMcp.sln --nologo
dotnet publish src\SqlServerMcp\SqlServerMcp.csproj -c Release -r win-x64 --self-contained false -o artifacts\publish
```

## Security Notes

- Use a dedicated login that cannot write, administer the server, access other databases, or use linked servers.
- Keep the config file and log directory readable only by the intended user.
- Tool responses can contain schema, module definitions, query plans, and selected data; review the MCP client's data-handling policy.
- Report vulnerabilities as described in [SECURITY.md](SECURITY.md).

## License

MIT
