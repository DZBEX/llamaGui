# 构建 Windows 安装包（需要 Inno Setup 6 的 ISCC.exe）。
# 用法：pwsh -File scripts/build-installer.ps1

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    $candidates = @()
    if (${env:ProgramFiles(x86)}) { $candidates += (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe") }
    if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe") }
    $iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) {
        $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($command) { $iscc = $command.Source }
    }

    if (-not $iscc) {
        Write-Warning "未找到 Inno Setup 6（ISCC.exe），安装器未构建。"
        Write-Host "安装方式（任选其一）：" -ForegroundColor Yellow
        Write-Host "  1. winget install JRSoftware.InnoSetup"
        Write-Host "  2. 从 https://jrsoftware.org/isdl.php 下载安装（勾选命令行编译器）"
        Write-Host "完成后重新运行本脚本。便携版发布不受影响（scripts/publish-portable.ps1）。"
        exit 2
    }

    $portableRoot = Join-Path $repoRoot "artifacts/portable"
    $portableDir = Get-ChildItem $portableRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "LlamaLauncher-*-win-x64*" } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $portableDir) {
        Write-Host "未发现便携版输出，先执行 publish-portable.ps1 ..." -ForegroundColor Cyan
        & (Join-Path $PSScriptRoot "publish-portable.ps1")
        $portableDir = Get-ChildItem $portableRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like "LlamaLauncher-*-win-x64*" } |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
    }

    Write-Host "便携版来源：$($portableDir.FullName)"
    Write-Host "== 构建安装包（$iscc）==" -ForegroundColor Cyan
    & $iscc "/DPortableSource=$($portableDir.FullName)" (Join-Path $repoRoot "installer/LlamaLauncher.iss")
    if ($LASTEXITCODE -ne 0) { throw "ISCC 构建失败" }

    $outputDir = Join-Path $repoRoot "artifacts/installer"
    $setup = Get-ChildItem $outputDir -Filter "*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($setup) {
        $hash = (Get-FileHash -LiteralPath $setup.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        Set-Content -LiteralPath "$($setup.FullName).sha256" -Value "$hash  $($setup.Name)" -Encoding ASCII
        Write-Host "安装包已生成：$($setup.FullName)" -ForegroundColor Green
        Write-Host "SHA-256：$hash"
    }
}
finally {
    Pop-Location
}
