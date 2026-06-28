# Changelog

All notable changes to this project are documented here.

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
