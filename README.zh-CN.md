# SQL Server MCP

一个面向 Windows 的只读 [Model Context Protocol](https://modelcontextprotocol.io/) 服务，可供 Codex 及其他支持 stdio 的 MCP 客户端探索和查询 Microsoft SQL Server。

[English](README.md)

## 特性

- 提供 35 个固定工具，覆盖连接检查、对象解析、结构查看、调用图、部署前校验、安全诊断和预估执行计划。
- 启动时只注册工具，不连接数据库，也不扫描全库；首次数据库调用时才建立连接。
- SQL 用户名和密码从 Windows Credential Manager 读取，不写入 JSON 配置。
- 基于 ScriptDom 的只读 Guard 接受单条 `SELECT` / `WITH`，或仅包含变量、本地 `#temp`、向临时表插入和最终 `SELECT` 的受控诊断批次。
- 所有工具都返回原生 MCP `structuredContent` 和精简兼容文本，并统一附带服务器、数据库、只读状态、登录、时间、耗时和隔离级别。
- 可配置返回行数、结果大小、文本长度、锁等待、命令和连接超时。
- 受限制或可能截断的工具会返回统一 `resultInfo`，说明已返回数量、限制、截断原因和缩小范围建议。
- stdout 只承载 MCP 协议，应用日志写入文件。

只读 Guard 是纵深防御，不能替代 SQL Server 权限控制。请始终使用独立的最小权限只读账号。

## 环境要求

- Windows 10/11 或 Windows Server
- [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- 可访问的 SQL Server
- 支持 stdio MCP 服务的客户端

## 快速开始

1. 从 [最新 Release](https://github.com/EdmondLu/sqlserver-mcp/releases/latest) 下载 `sqlserver-mcp-win-x64.zip`，解压到例如 `C:\Tools\sqlserver-mcp`。
2. 将 `sqlserver_mcp.example.json` 复制为 `sqlserver_mcp.json`，修改示例服务器、数据库和凭据 target。
3. 把 SQL 登录保存到 Windows Credential Manager：

```powershell
cmdkey /generic:sqlserver-mcp/SampleDb /user:readonly_user /pass
```

4. 在 Codex 中注册：

```toml
[mcp_servers.sqlserver_mcp]
type = "stdio"
command = 'C:\Tools\sqlserver-mcp\sqlserver_mcp.exe'
args = ["--config", 'C:\Tools\sqlserver-mcp\sqlserver_mcp.json']
startup_timeout_sec = 30
```

修改 MCP 客户端配置后需要重启客户端。

## SQL 权限

典型的最小权限数据库用户可授予：

```sql
ALTER ROLE db_datareader ADD MEMBER [readonly_user];
GRANT VIEW DEFINITION TO [readonly_user];
GRANT SHOWPLAN TO [readonly_user];
```

`VIEW DEFINITION` 用于读取结构和模块定义；只有 `explain_query_plan` 需要 `SHOWPLAN`。请在目标业务数据库内授权，不要在 `master` 中授权。

## 配置

完整示例见 [`docs/sqlserver_mcp.example.json`](docs/sqlserver_mcp.example.json)。主要默认值：返回 50 行、最大 500 行、结果上限 5 MB、单个文本值 1000 字符、锁等待 5 秒、命令超时 20 秒、连接超时 10 秒。

安全默认值为：禁止跨库和系统库，禁止服务器级 DMV，连接加密开启，不默认信任服务器证书，SQL 日志关闭。相对路径的 `logs`、`cache`、`tmp` 目录会创建在配置文件旁边。

`search_config_text` 只搜索 `textSearch.targets` 中显式配置的文本列和定位元数据列；默认不扫描全库文本列。目标配置可选 `keyColumn`、`nameColumn`、`labelColumns`、`createdAtColumn`、`updatedAtColumn`、`createdByColumn`、`updatedByColumn`、`contentKind` 等定位字段，用于返回页面、控件、菜单、维护人、时间和脚本类型。搜索结果会返回 `matchColumn`、`matchedTerm` 和 1-based `matchStart`；完整目标配置默认不返回，需要时传 `includeTargets=true`。如果目标表存在 `usable`、`enabled`、`active` 这类状态列，默认排序会让可用配置优先；传 `usableOnly=true` 时只返回可用配置，没有状态列的目标会自动忽略该过滤。

SQL 文本可能包含敏感数据，仅在确有需要时启用 `logging.logSql`。

## 工具

- `test_connection`、`health_check`
- `find_objects`、`resolve_object`、`find_column`、`profile_column`、`find_usage`
- `describe_table`、`get_object_overview`
- `get_indexes`、`get_constraints`、`get_foreign_keys`
- `search_sql_modules`、`get_module_definition`、`validate_tsql_script`、`validate_tsql_file`
- `compare_module_to_file`、`compare_module_to_repo`、`compare_modules_to_files`、`analyze_module_temp_tables`
- `get_dependencies`、`get_callers`、`get_callees`、`get_dependency_graph`
- `search_config_text`、`find_field_consumers`、`find_page_by_table`、`find_page_by_save_procedure`
- `run_readonly_query`、`run_readonly_batch`、`describe_query_result`
- `explain_query_plan`、`explain_query_plan_summary`、`batch_metadata`
- `reload_connection`

`health_check` 顶层返回 `serverVersion`，可直接确认当前发布到运行目录的服务端版本。

`explain_query_plan` 默认只返回语句、内存授予、warning、扫描、缺失索引、隐式转换、排序、hash、lookup、并行等摘要；仅在 `includeXml=true` 时返回原始 SHOWPLAN XML。

`analyze_module_temp_tables` 会分析模块内本地临时表的创建、读写、JOIN、跨行 INSERT/SELECT INTO/UPDATE 和字段流转摘要。

`compare_module_to_file` 适合已知道本地 SQL 文件路径时确认“仓库 SQL 是否已执行到数据库”；`compare_module_to_repo` 可按对象名在本地仓库/目录下自动发现 `.sql` 候选文件，唯一高分候选会直接比较，并列候选会返回列表让调用方收窄路径。

模块/文件对比明确返回 `exactMatch`、`bodyMatch`、`semanticMatch`；`differenceKind` 使用 `exact_match`、`wrapper_only`、`format_only`、`comment_only`、`body_changed`。`CREATE` / `CREATE OR ALTER`、BOM、首尾空行、会话 `SET` 和尾部 `GO` 不影响 `bodyMatch`。批量部署比较还返回 `target_missing`、`local_missing`，目标尚未部署时仍会校验本地语法、引用对象和可部署状态。

大结果搜索和只读查询会返回 `cursor`、`nextCursor`、`hasMore` 和可直接续查的 `nextRequest`。`find_usage` 使用字面量搜索，不再让过程名或字段名中的 `_` 进入 SQL `LIKE` 通配语义；每个命中都带 `matchKind`、`matchedText`、行列、上下文和置信度。

`describe_query_result` 可显式传入 `templateValues` 描述 UI SQL 模板，例如 `{ "0": "1=1" }` 会先把 `{0}` 替换为 `1=1`，再推断结果列。替换值按 SQL 片段处理，最终 SQL 仍会经过只读 Guard。

结构工具会识别 `vwp_`、`vwpr_`、`vwt_`、`vwtr_` 这四种历史视图前缀，并优先尝试对应的无前缀物理表。

字段长度明确区分 `maxLengthBytes` 和 `maxLengthCharacters`，避免把 `nvarchar` / `nchar` 的 SQL Server 字节长度误当成字符长度。

## 构建与测试

```powershell
dotnet restore SqlServerMcp.sln
dotnet test SqlServerMcp.sln --nologo
dotnet publish src\SqlServerMcp\SqlServerMcp.csproj -c Release -r win-x64 --self-contained false -o artifacts\publish
```

## 安全说明

- SQL 登录应禁止写入、服务器管理、跨库访问和链接服务器访问。
- 配置文件和日志目录只应对目标用户可读。
- 工具响应可能包含数据库结构、模块定义、查询计划和查询结果，请同时评估 MCP 客户端的数据处理策略。
- 安全问题请按 [SECURITY.md](SECURITY.md) 说明私下报告。

## 许可证

MIT
