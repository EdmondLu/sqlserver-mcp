# SQL Server MCP

一个面向 Windows 的只读 [Model Context Protocol](https://modelcontextprotocol.io/) 服务，可供 Codex 及其他支持 stdio 的 MCP 客户端探索和查询 Microsoft SQL Server。

[English](README.md)

## 特性

- 提供 16 个固定工具，覆盖连接检查、对象搜索、结构查看、依赖分析、SQL 模块搜索、只读查询和预估执行计划。
- 启动时只注册工具，不连接数据库，也不扫描全库；首次数据库调用时才建立连接。
- SQL 用户名和密码从 Windows Credential Manager 读取，不写入 JSON 配置。
- 基于 ScriptDom 的只读 Guard 只接受单条 `SELECT` 或 `WITH` 查询，并拒绝写操作、DDL、执行语句、跨库引用、服务器级 DMV、链接服务器和外部数据源。
- 可配置返回行数、结果大小、文本长度、锁等待、命令和连接超时。
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

SQL 文本可能包含敏感数据，仅在确有需要时启用 `logging.logSql`。

## 工具

- `test_connection`、`health_check`
- `find_objects`、`find_column`、`find_usage`
- `describe_table`、`get_object_overview`
- `get_indexes`、`get_constraints`、`get_foreign_keys`
- `search_sql_modules`、`get_module_definition`、`get_dependencies`
- `run_readonly_query`、`explain_query_plan`
- `reload_connection`

结构工具会识别 `vwp_`、`vwpr_`、`vwt_`、`vwtr_` 这四种历史视图前缀，并优先尝试对应的无前缀物理表。

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
