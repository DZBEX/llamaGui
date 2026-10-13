# 生成便携版发布包：artifacts/portable/LlamaLauncher-<版本>-win-x64/（可直接运行）与 zip 压缩包 + SHA-256 清单。
# 用法：
#   pwsh -File scripts/publish-portable.ps1                    # 默认自包含（无需预装 .NET，体积较大）
#   pwsh -File scripts/publish-portable.ps1 -FrameworkDependent # 框架依赖（需要 .NET Desktop Runtime 8）

param(
    [switch]$FrameworkDependent,
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    $version = (& dotnet msbuild src/LlamaLauncher.App/LlamaLauncher.App.csproj -nologo -getProperty:Version).Trim()
    $outDir = Join-Path $repoRoot "artifacts/publish/$Runtime"
    $suffix = ""
    $portableDir = Join-Path $repoRoot "artifacts/portable/LlamaLauncher-$version-$Runtime"
    if (Test-Path $portableDir) {
        try {
            Remove-Item -LiteralPath $portableDir -Recurse -Force -ErrorAction Stop
        }
        catch {
            # 目标目录被正在运行的应用占用时，改用带递增后缀的新目录（不终止用户进程）。
            $index = 2
            do {
                $candidate = Join-Path $repoRoot "artifacts/portable/LlamaLauncher-$version-$Runtime-r$index"
                $suffix = "-r$index"
                $index++
            } while (Test-Path $candidate)
            Write-Warning "原目录被占用（可能应用正在运行），改用新目录：$candidate"
            $portableDir = $candidate
        }
    }
    New-Item -ItemType Directory -Path $portableDir -Force | Out-Null

    $selfContained = if ($FrameworkDependent) { "false" } else { "true" }
    Write-Host "== dotnet publish (selfContained=$selfContained) ==" -ForegroundColor Cyan
    dotnet publish src/LlamaLauncher.App/LlamaLauncher.App.csproj -c Release -r $Runtime --self-contained $selfContained -p:PublishSingleFile=false -p:DebugType=None -o $outDir
    if ($LASTEXITCODE -ne 0) { throw "publish 失败" }

    Copy-Item -Path (Join-Path $outDir "*") -Destination $portableDir -Recurse -Force

    $runtimeNote = if ($FrameworkDependent) {
        "- 需要安装 .NET Desktop Runtime 8.0（x64）。"
    } else {
        "- 已自包含 .NET 运行时，无需额外安装。"
    }
    $readme = @"
LlamaLauncher $version（$Runtime）

运行要求：
$runtimeNote
- Windows 10 1809+ / Windows 11（x64）。

首次使用：
1. 启动 LlamaLauncher.exe。
2. 在“版本管理”页检查更新或“导入本地压缩包”选择已下载的官方 llama.cpp Windows x64 包，完成安装与能力探测；
   或直接在“启动配置”中选择本机已有的 llama-server.exe（会执行同样的能力探测）。
3. 在“模型库”页添加本地 GGUF 模型（本应用不下载模型文件）。
4. 新建启动配置 -> 在“服务实例”页启动 -> 状态变为“运行中”后可在“内置聊天”页对话。

数据位置：默认 %AppData%\LlamaLauncher（含数据库、日志、已安装的 llama.cpp 版本）。
便携数据模式：在程序目录创建 portable.marker 文件（或在“设置”页开启），数据写入程序目录下的 data\。

注意：本发布包不包含任何 llama.cpp 二进制与 GGUF 模型。
"@
    Set-Content -LiteralPath (Join-Path $portableDir "README-便携版.txt") -Value $readme -Encoding UTF8

    $zipPath = Join-Path $repoRoot "artifacts/portable/LlamaLauncher-$version-$Runtime$suffix.zip"
    if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -Path (Join-Path $portableDir "*") -DestinationPath $zipPath
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$zipPath.sha256" -Value "$hash  $(Split-Path -Leaf $zipPath)" -Encoding ASCII

    Write-Host "便携版已生成：" -ForegroundColor Green
    Write-Host "  目录：$portableDir"
    Write-Host "  压缩包：$zipPath"
    Write-Host "  SHA-256：$hash"
}
finally {
    Pop-Location
}
