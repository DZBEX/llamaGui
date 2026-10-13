# 第三方依赖许可证

本项目直接依赖以下 NuGet 包（由 NuGet 还原，以下为截至 2026-10 的记录，最终发布前由脚本复核）：

| 包 | 用途 | 许可证 |
| --- | --- | --- |
| CommunityToolkit.Mvvm | MVVM Toolkit（ObservableProperty/RelayCommand） | MIT |
| Markdig | Markdown 解析 | BSD-2-Clause |
| Microsoft.Extensions.Hosting / DI / Logging / Configuration | 宿主、依赖注入、日志 | MIT |
| Microsoft.Data.Sqlite | SQLite 数据访问（捆绑 SQLitePCLRaw） | MIT（SQLite 本体为 Public Domain） |
| System.Management | WMI 硬件信息查询 | MIT |
| System.Security.Cryptography.ProtectedData | DPAPI 秘密保护 | MIT |
| xunit / xunit.runner.visualstudio / Microsoft.NET.Test.Sdk | 测试框架（仅开发） | Apache-2.0 / MS 许可 |

本项目自身不包含 llama.cpp 二进制；llama.cpp 由用户在运行时下载或导入，遵循其自身许可证（MIT）。
