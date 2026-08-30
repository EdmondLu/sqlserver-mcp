# SQL Server MCP

[![CI](https://github.com/EdmondLu/sqlserver-mcp/actions/workflows/ci.yml/badge.svg)](https://github.com/EdmondLu/sqlserver-mcp/actions/workflows/ci.yml)
[![GitHub release](https://img.shields.io/github/v/release/EdmondLu/sqlserver-mcp)](https://github.com/EdmondLu/sqlserver-mcp/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

A Windows-first, read-only [Model Context Protocol](https://modelcontextprotocol.io/) server for exploring and querying Microsoft SQL Server from Codex and other MCP clients.

[简体中文](README.zh-CN.md)

## Highlights

- 44 focused tools for connection checks, object resolution, schema inspection, dependency/call-graph analysis, deployment validation, guarded diagnostics, LOB/report-byte inspection, and estimated query plans.
- Lazy database connections: startup registers tools but does not connect to SQL Server or scan the database.
- Credentials are read from Windows Credential Manager and are never stored in the JSON config.
- A ScriptDom-based guard accepts one `SELECT`/`WITH` query, or a tightly controlled diagnostic batch limited to local variables, local `#temp` DML, and SELECT result sets; uncertain statements and persistent side effects remain rejected.
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

Server-level DMVs use a separate permission layer (`VIEW SERVER PERFORMANCE STATE` on SQL Server 2022+, otherwise `VIEW SERVER STATE`) and are also independently controlled by `security.allowDmvQueries` and `security.allowServerLevelDmv`. `health_check` reports SQL permission, MCP policy, effective access, and every blocker separately. A database view that wraps a server-level DMV can pass the MCP text guard but still fail in SQL Server with error 300 when the login lacks the required server permission. Permission guidance separates human-readable `recommendations` from `permissionGuidance.adminSql`; database grants target the mapped database user, server grants target the login, and every database/principal identifier is safely bracket-escaped. These statements are suggestions for an authorized administrator and are never executed by the MCP.

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
| `limits.maxLobMb` | `50` | Maximum full LOB bytes scanned (`UTF-8` bytes for text, raw bytes for binary) by `read_lob`/report tools |
| `limits.maxLobChunkSize` | `262144` | Maximum returned LOB chunk in characters or bytes |
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
| `health_check` | Distinguish MCP version, SQL Server product version/edition, SQL permissions, MCP policy, and effective blockers |
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
| `get_module_definition` | Read a module definition, optionally by keyword or line range, with explicit discontinuous `slices[]` |
| `validate_tsql_script` | Summary-first static parse/metadata validation without execution or database writes |
| `validate_tsql_file` | Summary-first static validation of a local `.sql` file without claiming unverified deployment safety |
| `validate_deployment` | Combine one module file's static validation, target comparison, drift, and deployment risk |
| `compare_table_to_file` | Compare a CREATE TABLE script's effective structure with the target table |
| `compare_module_to_file` | Compare a database module definition with a known local file |
| `compare_module_to_repo` | Auto-discover matching repository `.sql` files and compare the best unambiguous candidate with the database module |
| `compare_modules_to_files` | Validate and compare an ordered deployment set with differences-only, field selection, diff suppression, and token budgets |
| `verify_deployment_set` | Verify tables, modules, and configuration patches together with one deployment state |
| `analyze_module_temp_tables` | Analyze local temp table creation, usage, multiline statements, and column flow inside a module |
| `get_dependencies` | Find incoming and outgoing dependencies |
| `get_callers` | Find confirmed/static/dynamic callers and caller transaction signals |
| `get_callees` | Find confirmed/static/dynamic callees |
| `get_dependency_graph` | Build a bounded confirmed dependency graph |
| `find_usage` | Rank literal identifier/text/regex usage with source location and confidence |
| `search_config_text` | Search configured application/configuration text and locator metadata with match-column and audit metadata |
| `verify_config_patch_file` | Parse a configuration UPDATE patch and verify its allow-listed current value without writes |
| `find_field_consumers` | Combine column, module, and configured page/low-code consumers |
| `find_page_by_table` | Find configured pages that reference a table or view |
| `find_page_by_save_procedure` | Find configured pages that reference a save procedure |
| `run_readonly_query` | Run one guarded read-only query with optional named parameters |
| `run_readonly_batch` | Run a rollback-only diagnostic batch with local variables, local `#temp` DML, and multiple result sets |
| `read_lob` | Hash one complete text/binary LOB and return resumable untruncated text or Base64 chunks |
| `inspect_report_payload` | Validate Base64/GZip/UTF-8/XML report bytes, BOM/declaration/newlines, and optional node uniqueness offline |
| `compare_report_payloads` | Compare original/candidate report containers and decompressed bytes without XML reserialization |
| `replace_report_payload_fragment` | Build a candidate offline through one unique exact decompressed-byte replacement, without XML reserialization or a database connection |
| `generate_guarded_report_patch` | Generate but never execute an exact-byte-replacement patch whose default branch is read-only preflight |
| `describe_query_result` | Describe guarded query result columns without executing the query, optionally applying explicit UI placeholder replacements |
| `explain_query_plan` | Return an estimated-plan summary, with raw XML only when requested |
| `explain_query_plan_summary` | Return only the compact estimated-plan summary |
| `batch_metadata` | Run independent metadata requests in parallel with per-item errors |
| `reload_connection` | Clear cached credentials, SQL connection pools, and metadata snapshots |

`health_check.mcpServerVersion` is the MCP executable version. The retained `serverVersion` field is only a compatibility alias and is not the database version; SQL Server identity is reported separately as `sqlServerProductVersion`, `sqlServerProductLevel`, and `sqlServerEdition`.

Bounded search/query tools expose stable `cursor`, `nextCursor`, `hasMore`, and executable `nextRequest` metadata where paging is supported. `find_usage` performs literal matching rather than SQL `LIKE`, so `_` in procedure and column names is never treated as a wildcard.

`find_usage` and caller analysis reuse an in-memory module catalog keyed by `object_id + modify_date`, plus line indexes and confirmed dependency edges. Hot calls avoid repeatedly transferring and scanning all module definitions; `reload_connection` explicitly invalidates these snapshots.

Multi-keyword `get_module_definition` results expose each selected window in `slices[]`. The compatibility `definition` string inserts `-- ... omitted lines X-Y ...` between discontinuous windows instead of joining unrelated statements directly.

Module/file comparison now reports `exactMatch`, `bodyMatch`, and `semanticMatch`, with `differenceKind` values `exact_match`, `wrapper_only`, `format_only`, `comment_only`, and `body_changed`. `CREATE`/`CREATE OR ALTER`, BOMs, leading/trailing blank lines, session `SET` wrappers, and trailing `GO` do not change `bodyMatch`. Ordered deployment-set comparison also reports `target_missing` and `local_missing`, and validates a missing target's local script before deployment.

`validate_tsql_script` and `validate_tsql_file` default to `detailLevel=summary`; use `detailLevel=full` to preserve resolved references, temp tables, table variables, and target parameter details. `staticValidationPassed` means only that parsing, metadata checks, and wrapper requirements passed. For backward compatibility, the existing `readyToDeploy` field on these tools and on `compare_modules_to_files` remains a deprecated alias of `staticValidationPassed`; responses mark this with `readyToDeploySemantics=legacy_alias_of_staticValidationPassed` and `readyToDeployDeprecated=true`. The new `deploymentReady` field is conservative: an existing target that has not been compared, or any detected target body drift, keeps it false. `validate_deployment` is the single-file combined entry point, and its top-level `readyToDeploy` is an alias of its strict `deploymentReady` with `readyToDeployDeprecated=false`; its nested `validation` object retains the legacy static semantics. When target comparison fails for a recognized metadata permission such as `VIEW DEFINITION`, the tool preserves `validation`, returns `targetComparison.state=inconclusive`, `compared=false`, stable error/hint/required-permission metadata, and `TARGET_COMPARISON_INCONCLUSIVE` instead of losing the static result. `OBJECT_DEFINITION=NULL` is not treated as proof of a permission failure: the module lookup also checks effective object-level `VIEW DEFINITION` and `IsEncrypted`. Only a confirmed missing permission returns `VIEW_DEFINITION_PERMISSION_REQUIRED` and `requiredPermission=VIEW DEFINITION`; an encrypted or otherwise unavailable definition returns `MODULE_DEFINITION_NOT_AVAILABLE` with no required permission and directs the caller to controlled source or an approved deployment artifact. Cancellation, connection failures, and unknown errors are not converted to inconclusive results.

Compact module diffs always report `totalHunkCount`, `returnedHunkCount`, and `omittedHunkCount`. `deploymentRisk` is computed from the complete diff before line-text truncation and includes affected, target-only, and local-only identifier summaries. Any `body_changed` target is marked as a potential production regression because deploying the local file may overwrite target-only logic.

`compare_modules_to_files` separates `deploymentState` from `staticValidationState`; caller-provided temporary tables are reported as non-blocking `external_temp_table` contracts. `diffMode=summary` omits nested diffs by default, while `onlyMismatches`, `includeDiff`, `maxTotalTokens`, and `fields` bound batch output explicitly. `batch_metadata` forwards each request's supported operation parameters, including `describe_table(mode=full)` and include overrides.

`compare_table_to_file` parses `CREATE TABLE`, subsequent `ALTER TABLE`, and index statements into an effective schema model before comparing columns, indexes, key/default/check constraints, and outgoing foreign keys. Expressions are normalized through ScriptDom, including quoted identifiers, redundant parentheses, numeric formats, Boolean term order, and SQL Server's equivalent `IN` expansion. It is summary-first and bounded by `includeDetails`, `fields`, and `maxTotalTokens`; `includeDescriptions=true` also compares table and column `MS_Description` values. `verify_config_patch_file` supports literal assignments and `REPLACE(column, old, new)` patches, but queries only configured `textSearch.targets` through parameterized read-only locators. `verify_deployment_set` combines table, module, and configuration results and defaults to returning differences only; any missing local input keeps the aggregate state `inconclusive` and is identified as `local_missing`.

Static validation binds complete CTE, derived-table, and APPLY projections. Remaining derived-source uncertainty is collapsed into one non-blocking `analysis_inconclusive` warning with reference, alias, and scope counts. When table comparison details exceed `maxTotalTokens`, leading differences are retained before full local/target models and `omittedDifferenceCount` reports the remainder.

Static validation reports `doomed_transaction_write_before_guard` when a CATCH block performs DROP, DML, `SELECT INTO`, `CREATE TABLE`, or calls a procedure before `IF XACT_STATE() = -1 THROW;`. It reports `doomed_transaction_write_without_guard` when the CATCH performs the same conservative set of possible writes but has no such guard at all. A guard before the operation suppresses both warnings, and nested TRY/CATCH blocks are assessed independently. These operations can raise SQL error 3930 in an uncommittable transaction and hide the original exception.

`describe_query_result` accepts optional `templateValues` for UI SQL placeholders, for example `{ "0": "1=1" }` replaces `{0}` before describing columns. Replacements are raw SQL fragments, and the final SQL is still parsed by the read-only guard.

`run_readonly_batch` accepts `DECLARE`, `SET @local`, local `#temp` creation/`SELECT INTO`, local `#temp` `INSERT`/`UPDATE`/`DELETE` (directly or through one uniquely resolved top-level alias), and one or more SELECT result sets. It returns every result set in `resultSets[]` while retaining the last result in compatibility `columns`/`rows`. A local temporary identifier must have exactly one leading `#`; global `##temp` references are rejected for reads and writes. Permanent targets, write-target aliases that cannot be proven locally, `EXEC`, dynamic SQL, explicit transactions, unsupported DDL, `NEXT VALUE FOR`, and other uncertain statements are rejected with structured reasons. The MCP-owned transaction is rolled back even on success.

`read_lob` requires exactly one row and one text or binary column. Each call performs one sequential scan, incrementally hashing and measuring the complete bounded value while retaining only the requested chunk. The byte limit means UTF-8 bytes for text and raw bytes for binary. `sha256` remains the cross-system content hash over normalized UTF-8 bytes for text or raw bytes for binary. `nextCursor` resumes by UTF-16 code-unit offset for text (without splitting valid surrogate pairs) or byte offset for binary. Its separate `cursorIdentity` binds the exact UTF-16LE code units (`identityEncoding=utf-16le-code-units`) for text or raw bytes (`identityEncoding=raw-bytes`) for binary, plus kind and lengths, in addition to the SQL/parameter fingerprint. This distinction prevents different malformed UTF-16 sequences that share the same replacement-fallback UTF-8 hash from being mixed. If a later scan sees different source content it returns `LOB_CURSOR_EXPIRED`; clients must discard prior chunks and restart. Bounded `inspectBase64GzipXml=true` explicitly captures the full text for inspection. For `nvarchar`, the UTF-16LE database-value hash is also returned for exact SQL-value guards.

`inspect_report_payload` and `compare_report_payloads` validate Base64/GZip, UTF-8 BOM, the exact XML declaration, exact CRLF/LF/CR counts, Base64 canonical/whitespace properties, XML parsing, root identity, and optional target-node uniqueness without converting the stored value to SQL Server XML or serializing it back. `compare_report_payloads` remains useful as a general diff, but `safeForGuardedPatch` can be true only when `originalFragmentBase64` and `replacementFragmentBase64` prove that the candidate decompressed bytes are exactly one raw-byte substitution. They do not load the FastReport runtime, execute scripts/data bindings, or validate rendering.

`replace_report_payload_fragment` closes the byte-sensitive candidate-construction step offline. It requires the original raw fragment to occur exactly once, substitutes the replacement directly in the decompressed byte array, creates a new GZip stream and canonical no-whitespace Base64 text, then returns all layer hashes, inspection/comparison/proof metadata, and guarded-patch payload arguments. When the optional complete `patchTarget` is supplied, `nextRequest.arguments` is directly callable as `generate_guarded_report_patch`; otherwise `readyToCall=false` and `requiredTargetArguments` lists the fields that must still be added. It never writes or connects to SQL Server and never reserializes XML. Exact preservation applies to decompressed bytes outside the replacement only: compressed bytes and GZip header metadata are explicitly not claimed to be preserved. If the original Base64 text is non-canonical or contains whitespace, the output policy reports canonicalization and keeps `safeForGuardedPatch=false` because the stored text policy changed.

`generate_guarded_report_patch` is an offline generator. The tool itself never opens a database connection and no writable MCP tool exists. It requires Base64-encoded original/replacement raw fragments, proves the original fragment occurs exactly once, and proves the candidate is byte-for-byte the result of that single substitution; proof metadata includes occurrence/replacement counts and the expected candidate SHA-256. It also refuses BOM/declaration/newline-count/Base64-property/root/selector drift, embeds the exact old ReportString as a recovery preimage, and guards old/new text plus compressed and decompressed hashes. In the generated SQL, `@Apply=0` performs read-only checks and a preimage preview, then `RETURN`s before any transaction, write lock, or `UPDATE`. Only `@Apply=1` starts a transaction, reacquires `UPDLOCK`/`HOLDLOCK`, rechecks uniqueness and the old value, updates, verifies, and commits. A human must verify real key/column types, export the preimage, review the SQL, and enable execution outside this MCP in an approved write session.

Structure tools recognize the legacy view prefixes `vwp_`, `vwpr_`, `vwt_`, and `vwtr_`, and try the corresponding unprefixed physical table first.

Column metadata uses `maxLengthBytes` and `maxLengthCharacters` explicitly. `maxLengthCharacters` is populated only for `char`/`varchar`/`nchar`/`nvarchar`/`sysname`; numeric, binary, date/time, GUID, and other non-character types return null.

Implicit-conversion plan risks are high only when the plan contains a column-side conversion or `PlanAffectingConvert`. A constant-side conversion that retains an index seek is returned as informational.

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
