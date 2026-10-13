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

表：`schema_migrations`、`Models`、`LaunchProfiles`、`LlamaCppInstallations`、`ServerInstances`、`StartupHistory`、`Conversations`、`ChatMessages`、`UpdateOperations`、`AppSettings`、`Secrets`（DPAPI 密文）。

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
