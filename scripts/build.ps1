# LlamaLauncher 构建流水线：还原 -> Release 构建 -> 全部测试
# 用法：pwsh -File scripts/build.ps1

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    Write-Host "== dotnet restore ==" -ForegroundColor Cyan
    dotnet restore LlamaLauncher.sln
    if ($LASTEXITCODE -ne 0) { throw "restore 失败" }

    Write-Host "== dotnet build (Release) ==" -ForegroundColor Cyan
    dotnet build LlamaLauncher.sln -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "构建失败" }

    foreach ($project in @(
        "tests/LlamaLauncher.UnitTests",
        "tests/LlamaLauncher.IntegrationTests",
        "tests/LlamaLauncher.UiTests")) {
        Write-Host "== dotnet test $project ==" -ForegroundColor Cyan
        dotnet test $project -c Release --no-build --nologo
        if ($LASTEXITCODE -ne 0) { throw "测试失败：$project" }
    }

    Write-Host "全部构建与测试通过。" -ForegroundColor Green
}
finally {
    Pop-Location
}
