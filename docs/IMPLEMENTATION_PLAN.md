# LlamaLauncher 实施计划与进度

> 状态：全部阶段已完成（✅）。配套证据见 `docs/TEST_PLAN.md` 与本文“验证记录”。
> 所有命令均在 Windows + PowerShell、工作区根目录执行；Release 构建与测试是每个阶段的验收闸门。

## 环境基线

- .NET SDK：8.0.425（global.json 固定，rollForward: latestPatch）。
- 目标框架：net8.0（Domain/Contracts/Application/UnitTests/TestChildProcess）；net8.0-windows（Infrastructure/App/IntegrationTests/UiTests）。
- NuGet 源：nuget.org（本机缓存优先）。

## 阶段状态总览

| 阶段 | 内容 | 状态 |
| --- | --- | --- |
| 0 | 仓库检查、方案与骨架 | ✅ |
| 1 | SQLite、模型库、JSON 配置档案 | ✅ |
| 2 | llama.cpp 安装、能力探测、参数与命令行构建 | ✅ |
| 3 | 进程管理与多实例 | ✅ |
| 4 | 硬件检测与资源监控 | ✅ |
| 5 | HTTP 服务与内置聊天 | ✅ |
| 6 | 安全、托盘与生命周期 | ✅ |
| 7 | llama.cpp 更新与回滚 | ✅ |
| 8 | 发布、文档与最终验收 | ✅（安装器构建依赖 Inno Setup，脚本已提供并给出指引） |

## 阶段 0：仓库检查、方案与骨架（✅）

- LlamaLauncher.sln + 分层项目（App / Application / Domain / Infrastructure / Contracts + 4 个测试项目）。
- Directory.Build.props（nullable、LangVersion 12）、.editorconfig、.gitignore、global.json。
- 宿主与 DI 骨架；首次 Release 构建即 0 警告 0 错误。

## 阶段 1：SQLite 与模型库（✅）

- Domain：实体/枚举/状态机/值域校验（启动参数、聊天参数、监听地址分类）。
- SQLite：手写迁移框架（schema_migrations，v1 建 11 张表与索引）、9 个仓储、事务与级联、在线备份。
- 模型库：登记 / 目录扫描（可取消 + 进度）/ 失效检测 / 别名标签备注；GGUF 元数据读取只访问文件头与元数据区。
- JSON 配置档案：版本化 schema、导入计划（新增/覆盖/跳过/无效 + 问题预览）、事务批量写入、默认剥离秘密引用。
- 测试：SQLite 迁移/仓储/会话排序/中断恢复、GGUF v1/v3 解析与容错、模型扫描、导入导出往返。

## 阶段 2：llama.cpp 安装与能力探测（✅）

- ExternalProcessRunner：argv 传参、双流异步读取、独立超时与取消、进程树终止；友好错误分类。
- 能力探测：--version / --help / --list-devices；原始输出保留；路径 + 文件指纹缓存；版本标签提取；后端标签汇总。
- 帮助解析：兼容两种排版与可选取值形式；未知行容错；参数目录归一化别名；设备解析。
- 命令行构建：能力驱动（不支持即阻止）；auto/all 依据帮助文本判定；flash-attn 两形态；API Key 掩码。
- 高级参数：Windows 规则解析、冲突检测（阻止或显式覆盖）、环境变量校验、脱敏预览。
- 安装管理：外部程序登记（强制探测）、重新探测、默认版本、被配置引用时阻止移除。

## 阶段 3：进程管理与多实例（✅）

- 状态机落地 + 运行代次防乱序；启动前完整校验（模型/程序/端口/安全/密钥）；/health 轮询区分加载中/就绪/错误/不可达。
- 停止（宽限期→终止进程树）、强制结束、重启、移除；PID + 启动时间 + 路径三重校验，绝不按名称误杀。
- 残留恢复：上次运行活跃状态经三重校验后安全接管，否则标记异常退出并说明。
- 每实例日志：内存环形缓冲 + 滚动文件 + 脱敏；落盘失败降级不中断推理；启动历史全量记录。
- 测试：双流压力（2 万行无死锁）、超时/取消/强杀、双实例独立、意外退出、脱敏回显、恢复与接管。

## 阶段 4：硬件检测与资源监控（✅）

- 系统信息（注册表 OS/CPU、GlobalMemoryStatusEx 内存）、WMI 显示适配器（失败降级）、nvidia-smi 可选采样。
- 三层设备判定：系统适配器 / 构建标签 / 该二进制实际设备，界面分开呈现并标注来源。
- 资源监控：后台单循环防重叠、周期可配置、进程 CPU/工作集/私有内存；GPU 不可得时显示 “—” 且注明来源。

## 阶段 5：HTTP 服务与内置聊天（✅）

- 健康检查与轻量 API 测试（/health、/v1/models、/props、一次小请求）。
- SSE 解析器（跨块/CRLF/多行 data/[DONE]/错误帧/逐字节容错）+ 流式聊天客户端（首 token 超时、总时限可选、取消传播、错误分类）。
- 会话服务：用户消息即时落库；助手占位流式增量保存（节流）；完成/中断/失败状态与真实指标；自动标题；清空/删除区分；启动恢复。
- 聊天 UI：会话列表、流式消息原位更新、停止生成、参数面板与校验、Ctrl+Enter、Markdown 安全渲染、代码块复制。

## 阶段 6：安全、托盘与生命周期（✅）

- DPAPI ISecretStore（密文入库）、默认 API Key 管理界面、局域网启动确认流程与三重校验（确认 + 密钥 + 能力）。
- 统一脱敏：日志写入前；诊断包导出二次脱敏；路径可按用户选择替换。
- 托盘（运行时绘制图标、快捷启停/聊天/退出）、关闭到托盘策略、退出流程（确认→停止全部服务→刷新日志→关闭）。

## 阶段 7：llama.cpp 更新与回滚（✅）

- GitHub Releases 客户端（稳定/预发布、速率限制与离线错误分类）、资产分类（后端/CUDA 版本/cudart/未识别）、官方域白名单。
- 下载（流式 + 进度 + 取消 + 清理、HTML 错误页识别、大小上限）、SHA-256（官方 digest 优先，否则本地指纹并标注）。
- ZIP 安全解压（穿越/绝对路径/符号链接/压缩炸弹）、staging 探测、独立目录注册、清单原子写 + 备份。
- 默认切换记录旧指针、一键回滚、删除保护（默认/配置引用/运行实例/目录范围）、更新审计历史。
- 测试：全流程安装、digest 不匹配、探测失败、非官方主机、切换 + 回滚、删除保护、导入、本地模拟下载/解压边界。

## 阶段 8：发布、文档与最终验收（✅）

- scripts/build.ps1（还原→构建→全部测试）；scripts/publish-portable.ps1（自包含/框架依赖 + zip + SHA-256）。
- scripts/build-installer.ps1 + installer/LlamaLauncher.iss（Inno Setup 6；本机未安装时输出指引并以代码 2 退出）。
- 发布产物冒烟：LlamaLauncher.exe --smoke-test（真实宿主启动、数据库迁移、恢复流程、设置/主题加载）→ 退出码 0。
- 文档：README、本文件、ARCHITECTURE、USER_GUIDE、TROUBLESHOOTING、LLAMA_CPP_COMPATIBILITY、UPDATE_DESIGN、SECURITY、TEST_PLAN、CHANGELOG、LICENSES。

## 验证记录（最终快照 2026-10-13）

- dotnet build LlamaLauncher.sln -c Release → 0 警告 0 错误。
- 测试：UnitTests 165 ✅ / IntegrationTests 116 ✅ / UiTests 25 ✅（共 306；含 ComboBox 主题渲染像素级回归）。
- 便携版发布：artifacts/portable/LlamaLauncher-0.9.0-win-x64.zip（含 SHA-256 清单）；发布产物冒烟退出码 0。
- 安装器：脚本与配置已提交；本机因缺少 Inno Setup 6 未实际生成 Setup.exe（脚本给出明确安装指引）。

## 已知限制（如实列出）

1. 真实 llama-server.exe 与真实 GGUF/GPU 的端到端行为需在用户环境验证（本机以受控子进程与服务模拟完成自动化验证）。
2. 安装包 Setup.exe 的生成与干净环境安装/卸载测试依赖 Inno Setup 6。
3. 局域网真实跨设备访问、GitHub 真实速率限制行为建议实机复核。
4. API Key 通过命令行传递受 Windows 进程可见性限制（已在 SECURITY.md 披露；密钥文件方式列为后续增强）。
