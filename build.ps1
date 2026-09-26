# build.ps1 — acad-mcp-loop 全量构建（LoopHost + SamplePlugin + LoopEngine）
# · LoopHost / SamplePlugin：net472，需 VS Build Tools（MSBuild）+ .NET 4.7.2 targeting pack
#   AutoCAD 引用从已安装 acad / LOOP_ACAD_REFS / -p:AcadRefs 解析（见 AcadRefs.props）
# · LoopEngine：net8，需 .NET SDK 8+
# LoopHost.dll 被 acad 占用（已 NETLOAD）时跳过——宿主更新需重启 CAD 重载。
param([string]$Configuration = "Debug")

$ErrorActionPreference = "Stop"

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    $vsInstallPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
    if ($vsInstallPath) {
        $msbuildPath = Join-Path $vsInstallPath "MSBuild\Current\Bin\MSBuild.exe"
    }
}
if (-not $msbuildPath -or -not (Test-Path $msbuildPath)) {
    Write-Host "Error: MSBuild.exe not found. Install Visual Studio Build Tools or update vswhere path." -ForegroundColor Red
    exit 1
}

function Test-FileLocked([string]$path) {
    if (-not (Test-Path $path)) { return $false }
    try {
        $fs = [System.IO.File]::Open($path, 'Open', 'ReadWrite', 'None')
        $fs.Close()
        return $false
    } catch { return $true }
}

# ---------------------------------------------------------------------------
# LoopHost（net472 宿主，NETLOAD 进 acad，不参与热重载）
# ---------------------------------------------------------------------------
Write-Host "Building LoopHost..." -ForegroundColor Cyan
if (Test-FileLocked "LoopHost\bin\$Configuration\LoopHost.dll") {
    Write-Host "LoopHost.dll locked by running acad - SKIPPED (restart acad + NETLOAD to update host)" -ForegroundColor Yellow
} else {
    & $msbuildPath "LoopHost\LoopHost.csproj" "/restore" "/p:Configuration=$Configuration" "/p:Platform=x64" "/v:m" "/nologo"
    if ($LASTEXITCODE -ne 0) { Write-Host "LoopHost build failed!" -ForegroundColor Red; exit 1 }
    Write-Host "LoopHost OK: LoopHost\bin\$Configuration\LoopHost.dll" -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# SamplePlugin（示例业务插件；每次构建生成新 BuildGuid 并写边车文件）
# ---------------------------------------------------------------------------
Write-Host "Building SamplePlugin..." -ForegroundColor Cyan
if (Test-FileLocked "SamplePlugin\bin\$Configuration\SamplePlugin.dll") {
    Write-Host "SamplePlugin.dll locked by running acad - SKIPPED" -ForegroundColor Yellow
} else {
    & $msbuildPath "SamplePlugin\SamplePlugin.csproj" "/restore" "/p:Configuration=$Configuration" "/p:Platform=x64" "/v:m" "/nologo"
    if ($LASTEXITCODE -ne 0) { Write-Host "SamplePlugin build failed!" -ForegroundColor Red; exit 1 }
    Write-Host "SamplePlugin OK: SamplePlugin\bin\$Configuration\SamplePlugin.dll" -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# LoopEngine（net8 MCP 引擎，stdio 服务进程）
# ---------------------------------------------------------------------------
Write-Host "Building LoopEngine..." -ForegroundColor Cyan
if (Test-FileLocked "LoopEngine\bin\$Configuration\net8.0\LoopEngine.exe") {
    Write-Host "LoopEngine.exe locked by an MCP session - SKIPPED (close the session using it, then rerun)" -ForegroundColor Yellow
} else {
$dotnetExe = "$env:USERPROFILE\dotnet8\dotnet.exe"
if (-not (Test-Path $dotnetExe)) { $dotnetExe = "dotnet" }
& $dotnetExe build "LoopEngine\LoopEngine.csproj" -c $Configuration --nologo -v m
    if ($LASTEXITCODE -ne 0) { Write-Host "LoopEngine build failed!" -ForegroundColor Red; exit 1 }
    Write-Host "LoopEngine OK (net8.0)" -ForegroundColor Green
}

Write-Host ""
Write-Host "acad-mcp-loop build done (LoopHost + SamplePlugin + LoopEngine)." -ForegroundColor Green
