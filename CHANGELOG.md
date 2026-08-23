# Changelog

All notable changes to this project are documented here.

## 2.3.0 - 2026-08-23

- Split `health_check` server-level DMV diagnostics into SQL Server effective permissions, MCP policy, effective access, and explicit blocker codes; added `mcpServerVersion`, `sqlServerProductVersion`, `sqlServerProductLevel`, and `sqlServerEdition` while retaining `serverVersion` as a documented compatibility alias. Permission guidance now separates explanations from never-executed administrator SQL, safely escapes identifiers, and uses database users for database grants and logins for server grants.
- Classified SQL Server error 300 as `SQL_SERVER_PERMISSION_REQUIRED`, with guidance that this is the SQL Server permission layer's final blocker even when the MCP policy permits the query.
- Made `validate_tsql_script` and `validate_tsql_file` summary-first through `detailLevel=summary|full`; static validity is exposed as `staticValidationPassed`. Their existing `readyToDeploy` field, and the one on `compare_modules_to_files`, remains a deprecated legacy alias of that static result, while the new conservative `deploymentReady` field remains false for an existing un-compared or drifted target.
- Added read-only `validate_deployment` to combine local file validation, target module comparison, drift detection, and deployment risk without executing the script or writing to SQL Server. Recognized metadata-permission failures now preserve static validation and return a conservative structured `targetComparison=inconclusive`; cancellation, connectivity, and unknown errors still propagate normally.
- Disambiguated `OBJECT_DEFINITION=NULL` by also checking effective object-level `VIEW DEFINITION` and `IsEncrypted`: confirmed permission failures still receive database-user grant guidance, while encrypted or otherwise unavailable definitions return `MODULE_DEFINITION_NOT_AVAILABLE` without an incorrect `requiredPermission` and direct deployment validation to controlled source or approved artifacts.
- Made module comparison report total, returned, and omitted hunk counts plus target/local affected identifiers and a high-severity production-regression risk even when compact diff line text is truncated.
- Added ScriptDom transaction-safety diagnostics for DROP/DML/SELECT INTO/CREATE TABLE/EXECUTE operations in CATCH before `IF XACT_STATE() = -1 THROW;` or when that guard is missing entirely, where SQL error 3930 could hide the original exception; nested CATCH blocks are assessed independently.

## 2.2.2 - 2026-08-13

- Bound complete CTE, derived-table, and APPLY projections, including repeated CTE names scoped to their nearest definition; genuinely unresolved derived references are collapsed into one non-blocking `analysis_inconclusive` warning with counts and the first location.
- Changed table comparison token budgeting to retain as many leading difference entries as fit, prioritize differences over full local/target models, and report the remaining `omittedDifferenceCount` precisely.

## 2.2.1 - 2026-08-10

- Normalized table expressions through ScriptDom so comments, redundant parentheses, quoted identifiers, numeric formats, reordered Boolean terms, and SQL Server's `IN`-to-`OR` check-constraint expansion compare semantically.
- Fixed `DELETE alias FROM schema.table alias` validation so the modification target alias is not reported as a missing `schema.alias` object.
- Made any locally inconclusive deployment item keep the aggregate state `inconclusive`; missing local files now use `local_missing`/`LOCAL_MISSING` instead of `CONFIG_INVALID` or a false `not_deployed` conclusion.
- Made `compare_table_to_file` summary-first and bounded with `includeDetails`, `fields`, and `maxTotalTokens`; optional `includeDescriptions` compares table and column `MS_Description` values.

## 2.2.0 - 2026-08-10

- Added `verify_deployment_set` to verify table scripts, SQL module files, and allow-listed UI/configuration patch files in one call with `deployed`, `not_deployed`, `partially_deployed`, `definition_mismatch`, and `inconclusive` states; differences-only output is the default.
- Added `compare_table_to_file`, which parses effective `CREATE TABLE`/`ALTER TABLE`/index structure and compares columns, indexes, key/default/check constraints, and outgoing foreign keys without executing the file.
- Added `verify_config_patch_file` for parameterized read-only verification of allow-listed literal and `REPLACE(column, old, new)` configuration UPDATE patches.
- Made `compare_modules_to_files` summary-first and bounded through `onlyMismatches`, `includeDiff`, `maxTotalTokens`, and `fields`; deployment equivalence and static validation are now separate states.
- Classified caller-provided temporary tables as non-blocking `external_temp_table_contract` references with explicit warnings.
- Forwarded operation-specific parameters through `batch_metadata`, including full `describe_table` presets/include overrides and definition/compare controls.

## 2.1.0 - 2026-07-30

- Made `maxLengthCharacters` null for numeric, binary, date/time, GUID, and other non-character types while preserving `maxLengthBytes`.
- Added explicit `slices[]` to `get_module_definition`; discontinuous keyword windows now include omitted-line separators instead of silently concatenating unrelated code.
- Refined implicit-conversion plan risk: only column-side conversions or `PlanAffectingConvert` signals are high risk, while constant-side conversions that retain an index seek are informational.
- Added an in-memory SQL module catalog keyed by `object_id + modify_date`, cached line offsets, and cached confirmed dependency edges for hot `find_usage` and caller analysis.
- Changed `get_callers` and `get_callees` to use object-targeted dependency queries when no fresh dependency snapshot exists; `reload_connection` now also clears metadata caches.

## 2.0.1 - 2026-07-30

- Fixed static T-SQL validation alias binding by resolving table sources within nested query, subquery, APPLY, and update scopes instead of using one global alias dictionary.
- Added `DECLARE @table TABLE (...)` symbol and column support; named EXEC argument labels and update target aliases are no longer reported as undeclared variables or missing objects.
- Downgraded derived/CTE row sources that cannot be bound uniquely to non-blocking `analysis_inconclusive` warnings instead of false `unresolved_column` errors.
- Fixed module comparison normalization when deployment comments precede `CREATE OR ALTER`; preamble, `GO`, CREATE/ALTER form, and CREATE keyword whitespace are normalized before body and semantic hashes.

## 2.0.0 - 2026-07-30

- Replaced JSON-in-text tool payloads with native MCP `structuredContent`, short compatibility text, MCP error signaling, and a uniform connection context.
- Fixed `find_usage` underscore wildcard false positives by using literal search plus identifier-boundary analysis; added ranked match kinds, source locations, context, confidence, regex mode, and stable cursors.
- Added no-execute/no-write `validate_tsql_script` and `validate_tsql_file` checks for syntax, variables, INSERT shapes, temp tables, object/column/type resolution, wrapper status, and dynamic SQL.
- Added `get_callers`, `get_callees`, and `get_dependency_graph`, including confirmed dependency edges, static call locations, dynamic-SQL warnings, and caller transaction signals.
- Added actionable SQL error codes/numbers/lines/suggestions for invalid objects, columns, types, parameters, and syntax.
- Added three-level module comparison (`exactMatch`, `bodyMatch`, `semanticMatch`) and `exact_match` / `wrapper_only` / `format_only` / `comment_only` / `body_changed` classifications.
- Added stable cursor metadata and executable `nextRequest` payloads to bounded searches and read-only query paging.
- Added compact `describe_table` presets, multi-keyword definition slicing controls, summary-first SHOWPLAN output, and `resolve_object`.
- Added ordered `compare_modules_to_files` deployment checks, including `target_missing` local-script validation.
- Added `profile_column`, configured page/field consumer tools, rollback-only `run_readonly_batch`, parallel `batch_metadata`, and explicit byte/character length fields.

## 1.15.1 - 2026-06-30

- Added top-level `serverVersion` to `health_check` so operators can confirm the published service version without a separate `initialize` probe.

## 1.15.0 - 2026-06-29

- Added `changedLineSummary` to module/file comparison results so callers do not need to parse summary text for changed and returned line counts.
- Added first-body-difference next actions that point directly to the database and local file lines to inspect.
- Added `compare.repoExcludePatterns` config defaults, merged with per-call `excludePatterns`, for routinely ignoring historical or backup SQL folders during repository module discovery.

## 1.14.0 - 2026-06-29

- Added `firstBodyDifference` to module/file comparison results, mapping the first SQL-normalized body difference back to database and file line numbers.
- Included first body-difference location in compare summaries so callers can quickly jump past script wrapper noise to the real changed line.

## 1.13.0 - 2026-06-29

- Added readable comparison summaries, `differenceKind`, `ignoredWrapperDifferences`, and `diff.mode` to module/file comparison results.
- Added `diffMode`, `maxHunks`, and `maxDiffLinesPerSide` to `compare_module_to_file` and `compare_module_to_repo` so callers can choose summary, compact, or fuller diff output.
- Added `excludePatterns` and `suggestedPatterns` to repository module comparison discovery for easier narrowing when old or backup SQL files share the same object name.

## 1.12.0 - 2026-06-29

- Improved module/file diff output to resynchronize after matching lines, return multiple compact hunks, and cap hunk/line output with truncation metadata.
- Added SQL module script normalization hashes and `sqlNormalizedMatch` for common deployment-script noise such as `CREATE OR ALTER`, leading `SET ANSI_NULLS` / `SET QUOTED_IDENTIFIER`, and trailing `GO`.
- Added top-level `status` and `candidateCount` to `compare_module_to_repo` responses for faster reading.

## 1.11.0 - 2026-06-29

- Added `compare_module_to_repo` to auto-discover matching local repository `.sql` files and compare the best unambiguous candidate with a database module definition.
- Strengthened `compare_module_to_file`, `search_sql_modules`, and `get_module_definition` discoverability for local SQL file vs target database deployment checks.
- Added module compare next-action hints and candidate discovery metadata for ambiguous repository matches.

## 1.10.0 - 2026-06-29

- Added `search_config_text.usableOnly` for filtering to usable/enabled/active rows when a target table has a matching status column.
- Prioritized usable/enabled/active rows in `search_config_text` ordering while preserving disabled rows by default.
- Added per-result `usable` metadata and target `usableColumn` summaries when a status column is detected.

## 1.9.0 - 2026-06-29

- Enhanced `search_config_text` to search configured metadata columns such as key, name, and labels in addition to the text column, and to return match column, match kind, matched term, and 1-based match position.
- Made full `search_config_text` target metadata optional through `includeTargets=false` by default for shorter day-to-day responses.
- Added explicit `templateValues` support to `describe_query_result` for UI SQL placeholders such as `{0}`, with final SQL still validated by the read-only guard.

## 1.8.0 - 2026-06-29

- Added a unified `resultInfo` block to bounded or truncation-prone tool responses while preserving existing `count`, `limit`, `truncated`, and `hint` fields.
- Allowed the read-only metadata function `sys.dm_exec_describe_first_result_set` through the SQL guard without allowing broader server-level DMVs.

## 1.7.0 - 2026-06-29

- Enhanced `explain_query_plan` summaries with statement cards, memory grant aggregation, warning counters, spill/no-join/optimizer-early-abort risks, and large-memory-grant hints.

## 1.6.0 - 2026-06-29

- Enhanced `analyze_module_temp_tables` with statement-window analysis, multiline INSERT/SELECT INTO/UPDATE detection, extracted target/projection/update columns, and per-temp-table column flow summaries.

## 1.5.0 - 2026-06-29

- Enhanced `search_config_text` with locator labels, audit fields, configured/detected content kind, and richer target metadata.
- Added `health_check` validation for configured `textSearch.targets`, including table existence and missing configured columns.

## 1.4.0 - 2026-06-29

- Added structured `explain_query_plan` summaries for SHOWPLAN XML, including scan, lookup, sort, hash, parallelism, missing-index, implicit-conversion, warning, and expensive-operator signals.

## 1.3.0 - 2026-06-29

- Added `analyze_module_temp_tables` to statically inspect local temp table creation, writes, reads, joins, and CREATE TABLE column definitions inside SQL modules.

## 1.2.0 - 2026-06-28

- Added `compare_module_to_file` to compare a database module definition with a local file, returning hashes, modify times, match flags, and compact line diff context.

## 1.1.0 - 2026-06-28

- Added `describe_query_result` for guarded query result-shape inspection without executing the target query.
- Added configured `search_config_text` profiles for allow-listed application/configuration text columns.
- Added keyword and line-range slicing, line numbers, and definition hashes to `get_module_definition`.
- Added named parameters and richer truncation metadata to `run_readonly_query`.
- Added trigger metadata to `get_object_overview` and clearer routing hints in tool descriptions.

## 1.0.0 - 2026-06-13

- Initial public release.
- Added 16 fixed MCP tools for SQL Server discovery, metadata inspection, usage analysis, guarded read-only queries, and estimated plans.
- Added lazy Windows Credential Manager authentication and file-only application logging.
- Added ScriptDom validation for single-statement read-only SQL, including rejection of cross-database, server-level DMV, linked-server, and external rowset access.
