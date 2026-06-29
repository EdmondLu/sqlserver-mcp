# Changelog

All notable changes to this project are documented here.

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
