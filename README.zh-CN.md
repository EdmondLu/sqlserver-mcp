# SQL Server MCP

一个面向 Windows 的只读 [Model Context Protocol](https://modelcontextprotocol.io/) 服务，可供 Codex 及其他支持 stdio 的 MCP 客户端探索和查询 Microsoft SQL Server。

[English](README.md)

## 特性

- 提供 44 个固定工具，覆盖连接检查、对象解析、结构查看、调用图、部署前校验、安全诊断、LOB/报表字节检查和预估执行计划。
- 启动时只注册工具，不连接数据库，也不扫描全库；首次数据库调用时才建立连接。
- SQL 用户名和密码从 Windows Credential Manager 读取，不写入 JSON 配置。
- 基于 ScriptDom 的只读 Guard 接受单条 `SELECT` / `WITH`，包括内层文本能独立通过同一 Guard 的一层 `OPENQUERY`；也接受仅包含局部变量、本地 `#temp` DML 和 SELECT 结果集的受控诊断批次。不确定语句和持久副作用仍一律拒绝。
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

服务器级 DMV 另有一层 SQL Server 权限：SQL Server 2022 及以上通常需要 `VIEW SERVER PERFORMANCE STATE`，旧版本使用 `VIEW SERVER STATE`；同时还分别受 `security.allowDmvQueries` 和 `security.allowServerLevelDmv` 控制。`health_check` 会把 SQL 有效权限、MCP 策略、最终有效访问和全部阻断原因分开返回。封装服务器 DMV 的业务视图可能通过 MCP 文本 Guard，但登录缺少服务器权限时仍会由 SQL Server 返回错误 300。权限指导会把人类可读的 `recommendations` 与 `permissionGuidance.adminSql` 分开；数据库级授权使用映射后的 database user，服务器级授权使用 login，数据库名和 principal 都进行安全的方括号转义。SQL 仅供有权限的管理员审核复制，MCP 不会自动执行。

## 配置

完整示例见 [`docs/sqlserver_mcp.example.json`](docs/sqlserver_mcp.example.json)。主要默认值：返回 50 行、最大 500 行、结果上限 5 MB、单个文本值 1000 字符、LOB 完整检查上限 50 MB、LOB 单块上限 262144 字符或字节、锁等待 5 秒、命令超时 20 秒、连接超时 10 秒。

安全默认值为：禁止跨库和系统库，禁止服务器级 DMV，连接加密开启，不默认信任服务器证书，SQL 日志关闭。相对路径的 `logs`、`cache`、`tmp` 目录会创建在配置文件旁边。

`search_config_text` 只搜索 `textSearch.targets` 中显式配置的文本列和定位元数据列；默认不扫描全库文本列。目标配置可选 `keyColumn`、`nameColumn`、`labelColumns`、`createdAtColumn`、`updatedAtColumn`、`createdByColumn`、`updatedByColumn`、`contentKind` 等定位字段，用于返回页面、控件、菜单、维护人、时间和脚本类型。搜索结果会返回 `matchColumn`、`matchedTerm` 和 1-based `matchStart`；完整目标配置默认不返回，需要时传 `includeTargets=true`。如果目标表存在 `usable`、`enabled`、`active` 这类状态列，默认排序会让可用配置优先；传 `usableOnly=true` 时只返回可用配置，没有状态列的目标会自动忽略该过滤。

SQL 文本可能包含敏感数据，仅在确有需要时启用 `logging.logSql`。

## 工具

- `test_connection`、`health_check`
- `find_objects`、`resolve_object`、`find_column`、`profile_column`、`find_usage`
- `describe_table`、`get_object_overview`
- `get_indexes`、`get_constraints`、`get_foreign_keys`
- `search_sql_modules`、`get_module_definition`、`validate_tsql_script`、`validate_tsql_file`、`validate_deployment`
- `compare_table_to_file`、`compare_module_to_file`、`compare_module_to_repo`、`compare_modules_to_files`、`verify_deployment_set`、`analyze_module_temp_tables`
- `get_dependencies`、`get_callers`、`get_callees`、`get_dependency_graph`
- `search_config_text`、`verify_config_patch_file`、`find_field_consumers`、`find_page_by_table`、`find_page_by_save_procedure`
- `run_readonly_query`、`run_readonly_batch`、`read_lob`、`describe_query_result`
- `inspect_report_payload`、`compare_report_payloads`、`replace_report_payload_fragment`、`generate_guarded_report_patch`
- `explain_query_plan`、`explain_query_plan_summary`、`batch_metadata`
- `reload_connection`

`health_check` 顶层用 `mcpServerVersion` 表示 MCP 服务版本，同时返回 `sqlServerProductVersion`、`sqlServerProductLevel` 和 `sqlServerEdition`；兼容字段 `serverVersion` 保留，但其精确语义只是 `mcpServerVersion` 的别名，不表示数据库版本。权限结果还会分开返回 SQL Server 权限、MCP 策略、`effectiveAccess` 和 `blockedBy`。

`explain_query_plan` 默认只返回语句、内存授予、warning、扫描、缺失索引、隐式转换、排序、hash、lookup、并行等摘要；仅在 `includeXml=true` 时返回原始 SHOWPLAN XML。只有列侧转换或 `PlanAffectingConvert` 才会把隐式转换列为高风险；常量侧转换且保留索引查找时仅返回提示。

`analyze_module_temp_tables` 会分析模块内本地临时表的创建、读写、JOIN、跨行 INSERT/SELECT INTO/UPDATE 和字段流转摘要。

`compare_module_to_file` 适合已知道本地 SQL 文件路径时确认“仓库 SQL 是否已执行到数据库”；`compare_module_to_repo` 可按对象名在本地仓库/目录下自动发现 `.sql` 候选文件，唯一高分候选会直接比较，并列候选会返回列表让调用方收窄路径。

模块/文件对比明确返回 `exactMatch`、`bodyMatch`、`semanticMatch`；`differenceKind` 使用 `exact_match`、`wrapper_only`、`format_only`、`comment_only`、`body_changed`。`CREATE` / `CREATE OR ALTER`、BOM、首尾空行、会话 `SET` 和尾部 `GO` 不影响 `bodyMatch`。批量部署比较还返回 `target_missing`、`local_missing`，目标尚未部署时仍会校验本地语法、引用对象和可部署状态。

`validate_tsql_script` 和 `validate_tsql_file` 默认使用 `detailLevel=summary`，只返回关键诊断和计数；显式传 `detailLevel=full` 可恢复已解析对象、临时表、表变量和目标参数等完整详情。`staticValidationPassed` 只表示语法、元数据和包装要求通过。为保持向后兼容，这两个工具及 `compare_modules_to_files` 原有的 `readyToDeploy` 继续作为 `staticValidationPassed` 的已弃用别名，并返回 `readyToDeploySemantics=legacy_alias_of_staticValidationPassed`、`readyToDeployDeprecated=true`。新字段 `deploymentReady` 才采用保守的目标感知语义：已有目标未比较或发现正文漂移时必定为 `false`。单文件场景应使用组合入口 `validate_deployment` 一次完成静态校验、目标比较和风险判断；该新工具顶层的 `readyToDeploy` 是严格 `deploymentReady` 的别名并返回 `readyToDeployDeprecated=false`，其嵌套 `validation` 对象仍遵守旧静态语义。目标比对因 `VIEW DEFINITION` 等可识别元数据权限失败时，工具会保留 `validation`，返回 `targetComparison.state=inconclusive`、`compared=false`、稳定的错误/提示/所需权限和 `TARGET_COMPARISON_INCONCLUSIVE`。`OBJECT_DEFINITION=NULL` 不再被直接当作权限不足证据：模块查询会同时检查对象级有效 `VIEW DEFINITION` 和可判断的 `IsEncrypted`；只有确认缺少权限时才返回 `VIEW_DEFINITION_PERMISSION_REQUIRED` 和 `requiredPermission=VIEW DEFINITION`，已加密或其它定义不可用情况返回 `MODULE_DEFINITION_NOT_AVAILABLE`、不附所需权限，并提示从受控源码或批准的发布制品核对。取消、连接中断和未知错误不会被吞成 inconclusive。

compact 模块差异即使截断正文，也会完整返回 `totalHunkCount`、`returnedHunkCount`、`omittedHunkCount`。`deploymentRisk` 在截断前基于完整差异计算，包含受影响、目标独有和本地独有的标识符摘要；任何 `body_changed` 都会显著标记潜在生产逻辑回退风险，因为部署本地文件可能覆盖目标库独有逻辑。

`compare_modules_to_files` 现在把 `deploymentState` 与 `staticValidationState` 分开；调用方提供的临时表会作为非阻断 `external_temp_table` 契约警告。`diffMode=summary` 默认不再嵌套 diff，可用 `onlyMismatches`、`includeDiff`、`maxTotalTokens` 和 `fields` 明确控制批量输出。`batch_metadata` 会把每个请求的完整受支持参数传给底层操作，包括 `describe_table(mode=full)` 和各项 include 覆盖。

`compare_table_to_file` 会把 `CREATE TABLE`、后续 `ALTER TABLE` 和索引语句解析为最终结构，再比较字段、索引、主键/唯一/默认/检查约束及出向外键。表达式通过 ScriptDom 做语义归一，可识别标识符方括号、冗余括号、数字格式、布尔条件顺序和 SQL Server 对 `IN` 的等价 `OR` 展开；默认只返回摘要，并可用 `includeDetails`、`fields`、`maxTotalTokens` 控制输出，`includeDescriptions=true` 时还会比较表和字段的 `MS_Description`。`verify_config_patch_file` 支持字面量赋值和 `REPLACE(column, old, new)` 补丁，但只会通过参数化只读查询核验 `textSearch.targets` 白名单。`verify_deployment_set` 可一次合并表、模块和动态配置结果，并默认只返回差异项；任一本地输入缺失都会使整套状态保持 `inconclusive`，单项明确标为 `local_missing`。

静态校验会绑定可完整推导的 CTE、派生表和 APPLY 投影；剩余派生源不确定性会合并为一条非阻断 `analysis_inconclusive`，并返回引用数、别名数、作用域数和首个位置。表比较详情超过 `maxTotalTokens` 时，会优先保留预算内的前几条差异，再省略完整本地/目标模型，并通过 `omittedDifferenceCount` 精确报告余量。

静态校验会报告 `doomed_transaction_write_before_guard`：如果 CATCH 在 `IF XACT_STATE() = -1 THROW;` 之前执行 DROP、DML、`SELECT INTO`、`CREATE TABLE` 或调用过程，这些操作在不可提交事务中可能触发 SQL 错误 3930，并覆盖原始异常。CATCH 完全没有该守卫但包含同类危险动作时，返回稳定类别 `doomed_transaction_write_without_guard`；守卫位于动作之前时不告警，嵌套 TRY/CATCH 分别判断以避免重复。

大结果搜索和只读查询会返回 `cursor`、`nextCursor`、`hasMore` 和可直接续查的 `nextRequest`。`find_usage` 使用字面量搜索，不再让过程名或字段名中的 `_` 进入 SQL `LIKE` 通配语义；每个命中都带 `matchKind`、`matchedText`、行列、上下文和置信度。

`find_usage` 和调用方分析会复用按 `object_id + modify_date` 失效的模块正文、行号索引和确认依赖边缓存，避免热态重复传输并扫描全部模块；`reload_connection` 会明确清除这些元数据缓存。

`get_module_definition` 的多关键字结果会在 `slices[]` 中分别返回每个代码窗口；兼容字段 `definition` 会在不连续窗口之间插入 `-- ... omitted lines X-Y ...`，不再直接拼接无关语句。

`describe_query_result` 可显式传入 `templateValues` 描述 UI SQL 模板，例如 `{ "0": "1=1" }` 会先把 `{0}` 替换为 `1=1`，再推断结果列。替换值按 SQL 片段处理，最终 SQL 仍会经过只读 Guard。

`run_readonly_batch` 允许 `DECLARE`、`SET @local`、创建本地 `#temp` / `SELECT INTO #temp`、直接或通过唯一可解析顶层别名针对本地 `#temp` 的 `INSERT` / `UPDATE` / `DELETE`，以及一个或多个 SELECT 结果集。响应通过 `resultSets[]` 保留全部结果集，同时用兼容字段 `columns` / `rows` 返回最后一个结果集。本地临时对象必须恰好只有一个前导 `#`；全局 `##temp` 无论读写都会被拒绝。永久对象写入、无法静态证明的写目标别名、`EXEC`、动态 SQL、显式事务、不支持的 DDL、`NEXT VALUE FOR` 和其它不确定语句会被结构化拒绝；MCP 自己建立的事务即使成功也会强制回滚。

`read_lob` 要求查询精确返回一行一列文本或二进制值。每次调用只做一次顺序扫描，增量计算完整值哈希和长度，只保留请求分块；`maxLobMb` 对文本按 UTF-8 字节、对二进制按原始字节计限。顶层 `sha256` 是供跨系统比对的内容哈希：文本按替换回退后的 UTF-8、二进制按原始字节计算。文本按 UTF-16 代码单元偏移续读且不拆分有效代理项对，二进制以 Base64 分块返回。`nextCursor` 使用独立的 `cursorIdentity`：文本绑定精确 UTF-16LE 代码单元哈希（`identityEncoding=utf-16le-code-units`），二进制绑定原始字节哈希（`identityEncoding=raw-bytes`），并同时绑定 LOB kind、总长度和 SQL/参数指纹；因此两个 UTF-8 替换回退哈希相同但代码单元不同的非法代理项序列也会返回 `LOB_CURSOR_EXPIRED`。调用方必须丢弃之前的所有分块并从头读取。只有显式 `inspectBase64GzipXml=true` 才在既有字节上限内保留完整文本用于容器检查；`nvarchar` 的 UTF-16LE 数据库值哈希也继续返回。

`inspect_report_payload` 和 `compare_report_payloads` 不把内容转成 SQL Server XML，也不重新序列化，而是直接验证 Base64、GZip、UTF-8 BOM、XML 声明、CRLF/LF/CR 精确计数、Base64 规范/空白属性、XML 可解析性、根节点及可选目标节点唯一性。普通 compare 仍可用于一般差异分析，但只有同时提供 `originalFragmentBase64` 与 `replacementFragmentBase64`，并证明候选解压字节严格等于一次原始字节替换的结果时，`safeForGuardedPatch` 才可能为 true。它们不会加载 FastReport 运行库、执行脚本或数据绑定，也不宣称完成渲染校验。

`replace_report_payload_fragment` 把最容易破坏字节格式的候选构建步骤闭合在纯离线工具中：旧字节片段必须在原始解压字节中恰好出现一次，替换直接作用于原始 `byte[]`，随后生成新的 GZip 与无空白规范 Base64，并返回各层哈希、inspection/comparison/exactReplacementProof 以及补丁载荷参数。可选提供完整 `patchTarget`；提供后 `nextRequest.arguments` 可原样调用 `generate_guarded_report_patch`，未提供时则返回 `readyToCall=false` 和仍需补齐的 `requiredTargetArguments`。该工具不建立数据库连接、不写库，也不把 XML 重新序列化。它只承诺替换范围外的解压字节保持不变，不承诺压缩流或 GZip header 元数据完全保真；原始 Base64 若非规范或含空白，会明确报告规范化策略，并因存储文本属性变化保持 `safeForGuardedPatch=false`。

`generate_guarded_report_patch` 是纯离线生成器：工具本身不建立数据库连接，项目也没有新增任何可写 MCP 工具。调用者必须以 Base64 提供原始与替换字节片段；工具会证明旧片段在原始解压字节中恰好出现一次，且候选严格等于这一次字节替换，并返回出现次数、替换次数和期望候选 SHA-256。它还会拒绝 BOM、XML 声明、换行精确计数、Base64 属性、根节点或目标节点不变量漂移，把精确旧 ReportString 嵌入为恢复前像并添加各层哈希守卫。生成 SQL 的 `@Apply=0` 是只读预检：输出前像与检查结果后，在事务、`UPDLOCK` / `HOLDLOCK` 和 `UPDATE` 之前直接 `RETURN`；只有 `@Apply=1` 才开启写事务、重新锁定并校验唯一行与旧值、更新、验证和提交。不得把“执行后回滚”称为 dry-run。人工执行前仍必须核对真实键值与字段类型、导出前像、审核 SQL，并在 MCP 之外的获批写会话中显式启用。

结构工具会识别 `vwp_`、`vwpr_`、`vwt_`、`vwtr_` 这四种历史视图前缀，并优先尝试对应的无前缀物理表。

字段长度明确区分 `maxLengthBytes` 和 `maxLengthCharacters`。只有 `char` / `varchar` / `nchar` / `nvarchar` / `sysname` 会返回字符容量；数值、二进制、日期时间、GUID 等非字符类型的 `maxLengthCharacters` 为 null。

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
