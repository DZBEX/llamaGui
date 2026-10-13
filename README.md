# LlamaLauncher

llama.cpp 的 Windows 桌面启动器（WPF / .NET 8）：管理本地 GGUF 模型、多实例 `llama-server.exe`、启动配置、内置流式聊天、硬件与能力探测、以及 llama.cpp 程序版本的检查、安装、切换与回滚。

> 本项目不下载 GGUF 模型；只管理用户本地的模型文件与 llama.cpp 运行程序。

## 功能

- **多实例**：并行运行多个 `llama-server.exe`，独立端口/日志/健康检查/启动历史；加载中、就绪、失败、异常退出状态分明。
- **能力探测**：对每个二进制执行 `--version` / `--help` / `--list-devices` 并缓存快照；参数面板只生成受支持的参数，
  不支持的关键参数会阻止启动而不是被静默忽略。
- **模型库**：登记/扫描本地 GGUF（只读文件头解析元数据），失效检测；移除登记不删除文件。
- **启动配置**：结构化参数、预设、能力校验、脱敏命令预览、高级额外参数与冲突检测、JSON 导入导出（默认不含秘密）。
- **内置聊天**：真实 SSE 流式输出、停止生成、系统提示词与采样参数、Markdown 安全渲染与代码块复制、历史持久化与中断恢复。
- **安全默认**：仅监听 127.0.0.1；局域网监听需要显式确认 + API Key；密钥使用 Windows DPAPI 存储，日志/历史/导出/诊断包全部脱敏。
- **版本管理**：官方 Releases 检查（稳定/预发布通道）、资产分类标注、流式下载与 SHA-256 校验、ZIP 安全解压、
  独立版本目录、切换默认与一键回滚、本地压缩包导入。
- **可观测**：滚动日志（每实例 stdout/stderr + 应用日志）、启动历史、更新审计、脱敏诊断包。

## 构建

```powershell
dotnet build LlamaLauncher.sln -c Release         # Release 构建（0 警告 0 错误）
pwsh -File scripts/build.ps1                      # 构建 + 全部测试（单元/集成/UI 共 306 项）
```

## 运行

```powershell
dotnet run --project src/LlamaLauncher.App -c Debug
```

## 发布

```powershell
pwsh -File scripts/publish-portable.ps1            # 自包含便携版 + zip + SHA-256（-FrameworkDependent 可改为框架依赖）
pwsh -File scripts/build-installer.ps1             # Windows 安装包（需要 Inno Setup 6；脚本会自动检测并给出指引）
```

## 文档

- [实施计划与进度](docs/IMPLEMENTATION_PLAN.md)
- [架构说明](docs/ARCHITECTURE.md)
- [用户手册](docs/USER_GUIDE.md)
- [故障排查](docs/TROUBLESHOOTING.md)
- [llama.cpp 兼容策略](docs/LLAMA_CPP_COMPATIBILITY.md)
- [更新系统设计](docs/UPDATE_DESIGN.md)
- [安全模型](docs/SECURITY.md)
- [测试计划与执行结果](docs/TEST_PLAN.md)

## 目录结构

见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。
