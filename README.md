# SQL Server MCP

[![CI](https://github.com/EdmondLu/sqlserver-mcp/actions/workflows/ci.yml/badge.svg)](https://github.com/EdmondLu/sqlserver-mcp/actions/workflows/ci.yml)
[![GitHub release](https://img.shields.io/github/v/release/EdmondLu/sqlserver-mcp)](https://github.com/EdmondLu/sqlserver-mcp/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

A Windows-first, read-only [Model Context Protocol](https://modelcontextprotocol.io/) server for exploring and querying Microsoft SQL Server from Codex and other MCP clients.

[简体中文](README.zh-CN.md)

## Highlights

- 16 focused tools for connection checks, object discovery, schema inspection, dependency analysis, SQL module search, read-only queries, and estimated query plans.
- Lazy database connections: startup registers tools but does not connect to SQL Server or scan the database.
- Credentials are read from Windows Credential Manager and are never stored in the JSON config.
- A ScriptDom-based guard accepts one `SELECT` or `WITH` query and rejects writes, DDL, execution, cross-database references, server-level DMVs, linked-server access, and bulk/external rowsets.
- Result row, payload, text-length, lock-wait, command, and connection limits are configurable.
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
| `logging.logSql` | `false` | Include submitted SQL text in file logs |
| `connection.encrypt` | `true` | Encrypt SQL connections |
| `connection.trustServerCertificate` | `false` | Skip certificate-chain validation |
| `connection.applicationIntent` | `ReadOnly` | Set SQL client application intent |

Relative `logs`, `cache`, and `tmp` directories are created beside the config file. SQL text may contain sensitive data, so enable `logging.logSql` only when appropriate.

## Tools

| Tool | Purpose |
| --- | --- |
| `test_connection` | Validate the connection and current SQL identity |
| `health_check` | Check config, runtime paths, connection, and permissions |
| `find_objects` | Search tables, views, procedures, and functions |
| `describe_table` | Inspect columns, indexes, constraints, and foreign keys |
| `get_object_overview` | Return compact metadata and dependency context |
| `find_column` | Find tables and views containing a column |
| `get_indexes` | Inspect index metadata |
| `get_constraints` | Inspect key, unique, default, and check constraints |
| `get_foreign_keys` | Inspect incoming and outgoing foreign keys |
| `search_sql_modules` | Search SQL module definitions |
| `get_module_definition` | Read a view, procedure, function, or trigger definition |
| `get_dependencies` | Find incoming and outgoing dependencies |
| `find_usage` | Find object, column, or token usage |
| `run_readonly_query` | Run one guarded read-only query |
| `explain_query_plan` | Return estimated SHOWPLAN XML without executing the query |
| `reload_connection` | Clear cached credentials and SQL connection pools |

Structure tools recognize the legacy view prefixes `vwp_`, `vwpr_`, `vwt_`, and `vwtr_`, and try the corresponding unprefixed physical table first.

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
