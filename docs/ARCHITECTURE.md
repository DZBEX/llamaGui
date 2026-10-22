# LlamaLauncher 架构说明

## 1. 总体分层

```text
LlamaLauncher.sln
├─ src/
│  ├─ LlamaLauncher.Domain          # 实体、值对象、枚举、状态机、校验规则（不依赖 WPF/SQLite/HttpClient/Process）
│  ├─ LlamaLauncher.Contracts       # llama-server HTTP API 的稳定 DTO（OpenAI 兼容聊天、健康、props）
│  ├─ LlamaLauncher.Application     # 用例编排、服务接口、纯逻辑（命令行构建、能力解析、SSE 解析、导入计划、资产筛选）
│  ├─ LlamaLauncher.Infrastructure  # SQLite、进程、HTTP、DPAPI、WMI、文件系统、GitHub、更新安装
│  └─ LlamaLauncher.App             # WPF Shell、Views、ViewModels、UI 服务（对话框/托盘/剪贴板/主题）
├─ tests/
│  ├─ LlamaLauncher.UnitTests        # 纯逻辑 + SQLite 仓储（临时库）
│  ├─ LlamaLauncher.IntegrationTests # 真实子进程、模拟 HTTP 服务、临时目录端到端流程
│  ├─ LlamaLauncher.UiTests          # ViewModel 级流程测试（无真实窗口）
│  └─ LlamaLauncher.TestChildProcess # 受控子进程程序（模拟 llama-server 输出/退出行为）
├─ installer/  scripts/  docs/
```

依赖方向（单向，禁止反向引用）：

```text
App ───────────────► Application ──────► Domain
 │                        │                ▲
 │                        ▼                │
 └──► Infrastructure ─────┴──────► Contracts┘（Contracts 仅被 Application/Infrastructure 引用）
```

- Domain 不依赖任何外部包。
- Application 定义服务接口（`I*Service`），只依赖 Domain/Contracts 与 `Microsoft.Extensions.Logging.Abstractions`。
- Infrastructure 实现接口，依赖 `Microsoft.Data.Sqlite`、`System.Management`、`ProtectedData`。
- App 通过 `Microsoft.Extensions.Hosting` 组装 DI；ViewModel 构造函数注入服务，命令使用 `RelayCommand`/`AsyncRelayCommand`；不直接 `new HttpClient()` / `new SqliteConnection()` / 启动进程。

## 2. 关键设计决策

| 决策 | 选择 | 理由 |
| --- | --- | --- |
| 数据访问 | `Microsoft.Data.Sqlite` + 手写 SQL 与版本化迁移 | 完全可控的事务/迁移/测试；避免 EF Core 迁移与 WPF 启动复杂度；包体积小 |
| Markdown 渲染 | Markdig 解析 + WPF `FlowDocument` 渲染 | 不引入 WebView2 运行时依赖，天然杜绝脚本注入；代码块提供复制按钮；无运行时缺失降级问题 |
| 托盘 | WinForms `NotifyIcon`（`UseWindowsForms`） | 稳定、无额外依赖；与 WPF 共用同一 UI 线程消息泵 |
| 秘密存储 | Windows DPAPI（CurrentUser）经 `ISecretStore` 抽象 | 仅本机可解密；测试用内存替身 |
| 时间 | BCL `TimeProvider` | 可测试的时间抽象 |
| 日志 | `Microsoft.Extensions.Logging` + 自定义滚动文件 Provider | 按实例分流、上限控制、脱敏 |
| 数据目录 | 默认程序根目录（exe 旁 `data/ logs/ downloads/ runtimes/ manifests/ backups/ exports/`）；可用引导文件改为其他目录；程序目录不可写时回退 `%AppData%\LlamaLauncher` | 所有应用文件集中随程序目录；旧版 `%AppData%` / `exe\data` 数据在首次启动时自动迁移并改写绝对路径 |
| UI 架构 | 单主窗口 + 左侧导航 + 页面 ViewModel 缓存 | 切换页面不中断服务实例；保持状态 |

## 3. 运行时核心流程

### 3.1 实例启动

```text
Profile ──► 校验(模型/可执行文件/参数/能力快照/端口/安全) ──► 命令行(argv)构建 ──► 预览(脱敏)
   └─► ProcessStart(ArgumentList, 无 shell) ──► stdout/stderr 异步采集 ──► 健康轮询 /health
        └─► LoadingModel ──► Ready ──►(异常/退出事件)──► Failed / ExitedUnexpectedly ──► 启动历史
```

- 进程生命周期由 `IServerProcessManager` 统一管理；状态机 `InstanceStateMachine` 集中控制合法迁移。
- 每个实例拥有独立运行代次（generation），旧进程的退出事件不会覆盖新一轮状态。
- 只终止“由本应用启动且 PID + 启动时间 + 可执行文件路径 均可验证”的进程（防误杀）。

### 3.2 聊天流式

```text
ChatView ──► ChatViewModel ──► IChatCompletionClient(SSE) ──► llama-server /v1/chat/completions
    │              │                     △
    │              ▼                     └─ SseParser（跨 buffer 分帧、[DONE]、错误帧）
    │         ConversationService（增量持久化、取消/中断状态）
    └──► MarkdownRenderer(FlowDocument) 渲染，代码块复制
```

### 3.3 更新

```text
ReleaseFeed(GitHub) ─► AssetSelector(纯逻辑分类) ─► 用户确认 ─► 流式下载(进度/取消)
   ─► SHA-256(官方 digest 优先) ─► ZIP 安全解压(拒绝穿越) ─► staging 探测(--version/--help/--list-devices)
   ─► 注册安装记录 ─► 切换默认指针(原子写) ─► 失败回滚
```

## 4. 数据存储

### 4.1 SQLite（`data/llamalauncher.db`）

表：`schema_migrations`、`Models`、`LaunchProfiles`、`LlamaCppInstallations`、`ServerInstances`、`StartupHistory`、`Conversations`、`ChatMessages`、`UpdateOperations`、`AppSettings`、`Secrets`（DPAPI 密文）、`WorkflowRuns`、`BenchmarkRuns`。

- 时间为 UTC ISO-8601 文本；UI 按本地时间显示。
- 关键写操作使用事务；消息按 `ConversationId + Sequence` 索引。
- 高频资源采样不写入数据库，仅保留滚动日志文件与内存快照。

### 4.2 版本清单与文件布局

```text
{DataRoot}/
├─ data/llamalauncher.db
├─ logs/                      # 应用日志与每实例日志（滚动、上限、脱敏）
├─ downloads/                 # 更新临时下载（可清理）
├─ runtimes/llama.cpp/<版本目录>/  # 每个 llama.cpp 版本独立目录
├─ manifests/installations.json    # 安装清单与默认版本指针（原子写 + 备份）
├─ backups/                        # 数据库备份
└─ exports/                        # JSON 配置导出（默认不含密钥）
```

## 5. 安全模型

- 默认监听 `127.0.0.1`；LAN 监听需显式开启、确认风险、且当前二进制支持 API Key 参数并已配置密钥。
- 密钥通过 DPAPI 保护；日志、命令预览、启动历史、诊断包、默认导出均掩码。
- 渲染层（FlowDocument）不执行脚本；外链在系统浏览器打开。
- 更新只信任官方 `ggml-org/llama.cpp` 源；手动导入需显式声明来源并做同等安全检查。

## 6. 可测试性

- 纯逻辑（参数校验、命令行构建、能力解析、SSE 解析、导入计划、资产筛选、状态机、脱敏）全部在 Application/Domain，无 I/O，直接单元测试。
- I/O 通过接口替换：HTTP 用本地 TcpListener 测试服务器；进程用 `LlamaLauncher.TestChildProcess`；文件用临时目录；数据库用临时 SQLite 文件；时间用自定义 `FakeTimeProvider`。

## 7. 决策模型实验室

新增模块沿用同一分层与依赖方向（引擎不引用 WPF，可脱离 UI 单元测试）：

```text
Domain/Workflows             # 定义/节点/连线/变量/表达式/模板/节点目录/运行记录/测试用例（可序列化，版本化）
Application/Workflows        # 引擎（调度/分支/并行/断点/单步/取消/重试/超时）、校验器、节点执行器、
                             # 断言评估、JSON 差异、模型解析与算法注册抽象、运行协调（WorkflowRunnerService）
Infrastructure               # /v1/systemone HTTP 客户端、实例→模型目标解析、JSON 文件存储（原子写）、
                             # WorkflowRuns SQLite 仓储（迁移 v2）
App/ViewModels/Lab + Views   # 实验室容器与六个子页、节点属性编辑器、自绘棋盘与工作流画布（无第三方图形库）
```

- 引擎通过 `IWorkflowRunObserver` 单向通知 UI（UI 侧切回 Dispatcher），不引用窗口/控件；
  `IWorkflowInteractionBroker` 提供人工确认通道（未提供时明确失败）。
- 数据流以 JSON（`JsonNode`）为统一载体：`input` / `vars` / `up` / `meta` / `error` / `doc`；
  表达式的语法、函数、长度、步数均有白名单与上限（禁止任意代码执行）。
- 存储：工作流与用例为版本化 JSON 文件（`workflows/`、`workflows/testcases/`、`workflows/snapshots/`），
  运行历史为 `WorkflowRuns` 表（内嵌定义快照与节点/日志 JSON，`FinalVariablesJson` 记录最终变量）。

## 8. 性能基准

```text
Domain/Benchmarks          # 运行记录/样本/配置、限量校验、聚合统计（纯逻辑，无 I/O）
Application/Benchmarks     # 运行编排（预热/重复/冷却/取消/提前中止）、提示构造、/completion 流帧解析、存储抽象
Infrastructure             # llama-server /completion 流式客户端（SSE + timings + 客户端实测）、
                           # BenchmarkRuns SQLite 仓储（迁移 v3）
App/ViewModels/Views       # 性能基准页（“本次测试 / 历史记录”两个页签）
```

- 测量保真：`cache_prompt=false` + 每个样本唯一提示前缀（防止服务端 KV 缓存复用导致偏快）；
  贪心采样（temperature=0 / top_k=1）；速度优先取服务端 `timings`，无 timings（旧版本）时以内容帧数
  近似生成 token 并回退客户端计时（首 token 与总耗时始终为客户端实测）。
- 运行记录按样本增量写入（中途取消/异常退出仍保留已完成样本）；完成后按档位聚合（均值 ± 样本标准差）。
- 沿用既有分层：编排不引用 WPF，UI 通过回调切回 Dispatcher；HTTP 客户端可在本地模拟 SSE 服务上端到端测试。
