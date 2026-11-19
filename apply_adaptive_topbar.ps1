# Requires Administrator privileges to modify files under C:\Program Files\HONOR\MagicAnimation\MagicTaskbarApp
param(
    [switch]$Elevated,
    [switch]$PauseOnExit
)

$ErrorActionPreference = "Stop"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    if ($Elevated) {
        Write-Host "需要管理员权限写入系统程序目录，请允许 UAC 提权后重试。" -ForegroundColor Yellow
        exit 1
    }

    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"", "-Elevated")
    if ($PauseOnExit) { $argList += "-PauseOnExit" }
    try {
        $proc = Start-Process -FilePath "powershell.exe" -ArgumentList $argList -Verb RunAs -Wait -PassThru
        exit $proc.ExitCode
    } catch {
        Write-Host "已取消管理员授权，安装中止。" -ForegroundColor Yellow
        if ($PauseOnExit) {
            Write-Host "按任意键关闭..." -ForegroundColor Gray
            $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
        }
        exit 1
    }
}

$exitCode = 0
try {

$appDir = "C:\Program Files\HONOR\MagicAnimation\MagicTaskbarApp"
$srcDll = Join-Path $PSScriptRoot "bin\Release\net6.0-windows\TopBarAdaptiveColor.dll"
$dstDll = Join-Path $appDir "TopBarAdaptiveColor.dll"
$srcRestore = Join-Path $PSScriptRoot "restore_topbar.ps1"
$dstRestore = Join-Path $appDir "restore_adaptive_topbar.ps1"
$rcPath = Join-Path $appDir "magictaskbar-ui.runtimeconfig.json"
$bakPath = Join-Path $appDir "magictaskbar-ui.runtimeconfig.json.bak"

$ccDir = "C:\Program Files\HONOR\NotifControlUI"
$ccExe = Join-Path $ccDir "ControlCenterTrayUI.exe"
$srcBoostDll = Join-Path $PSScriptRoot "bin\Release\net6.0-windows\ControlCenterSpeedBoost.dll"
$srcBoostCfg = Join-Path $PSScriptRoot "bin\Release\net6.0-windows\ControlCenterTrayUI.exe.config"
$dstBoostDll = Join-Path $ccDir "ControlCenterSpeedBoost.dll"
$dstBoostCfg = Join-Path $ccDir "ControlCenterTrayUI.exe.config"
$bakBoostCfg = Join-Path $ccDir "ControlCenterTrayUI.exe.config.bak"

$needsBuild = (-not (Test-Path $srcDll)) -or (-not (Test-Path $srcBoostDll)) -or (-not (Test-Path $srcBoostCfg))
if (-not $needsBuild -and (Test-Path (Join-Path $PSScriptRoot "TopBarAdaptiveColor.csproj"))) {
    $dllTime = (Get-Item $srcDll).LastWriteTimeUtc
    $newerCs = Get-ChildItem $PSScriptRoot -Recurse -Filter *.cs -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTimeUtc -gt $dllTime } |
        Select-Object -First 1
    if ($null -ne $newerCs -and (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        $needsBuild = $true
    }
}
if ($needsBuild) {
    Write-Host "正在编译最新补丁产物 (dotnet build -c Release)..." -ForegroundColor Cyan
    dotnet build (Join-Path $PSScriptRoot "TopBarAdaptiveColor.csproj") -c Release
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build 失败 (ExitCode=$LASTEXITCODE)。"
    }
}

if (-not (Test-Path $bakPath)) {
    Copy-Item $rcPath $bakPath -Force
    Write-Host "已备份原始配置到: $bakPath" -ForegroundColor Green
}

# Stop running magictaskbar-ui first so TopBarAdaptiveColor.dll is unlocked before copying.
$copied = $false
for ($attempt = 1; $attempt -le 3; $attempt++) {
    $procs = Get-Process -Name "magictaskbar-ui" -ErrorAction SilentlyContinue
    if ($procs) {
        Write-Host "正在停止 magictaskbar-ui.exe 以更新补丁..." -ForegroundColor Cyan
        foreach ($p in $procs) {
            try {
                Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
                $null = $p.WaitForExit(3000)
            } catch {}
        }
    }

    try {
        Copy-Item $srcDll $dstDll -Force
        $copied = $true
        break
    } catch {
        if ($attempt -eq 3) { throw }
        Start-Sleep -Milliseconds 300
    }
}

if ($copied) {
    Write-Host "已部署 DLL: $dstDll" -ForegroundColor Green
}

if (Test-Path $srcRestore) {
    Copy-Item $srcRestore $dstRestore -Force -ErrorAction SilentlyContinue
}

$json = Get-Content $rcPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($null -eq $json.runtimeOptions.configProperties) {
    $json.runtimeOptions | Add-Member -NotePropertyName configProperties -NotePropertyValue ([PSCustomObject]@{})
}
$json.runtimeOptions.configProperties | Add-Member -NotePropertyName "STARTUP_HOOKS" -NotePropertyValue $dstDll -Force
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($rcPath, ($json | ConvertTo-Json -Depth 10), $utf8NoBom)
Write-Host "已更新 runtimeconfig STARTUP_HOOKS。" -ForegroundColor Green

Start-Sleep -Seconds 2
$restarted = Get-Process -Name "magictaskbar-ui" -ErrorAction SilentlyContinue
if (-not $restarted) {
    $exePath = Join-Path $appDir "magictaskbar-ui.exe"
    Start-Process "explorer.exe" -ArgumentList ('"' + $exePath + '"')
}

if (Test-Path $ccExe) {
    if ((Test-Path $dstBoostCfg) -and -not (Test-Path $bakBoostCfg)) {
        $existingCfgText = Get-Content $dstBoostCfg -Raw -ErrorAction SilentlyContinue
        if ($existingCfgText -and ($existingCfgText -notmatch "ControlCenterSpeedBoost")) {
            Copy-Item $dstBoostCfg $bakBoostCfg -Force
            Write-Host "已备份控制中心原始配置到: $bakBoostCfg" -ForegroundColor Green
        }
    }

    $boostCopied = $false
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $ccProcs = Get-Process -Name "ControlCenterTrayUI" -ErrorAction SilentlyContinue
        if ($ccProcs) {
            Write-Host "正在停止 ControlCenterTrayUI.exe 以部署响应加速模块..." -ForegroundColor Cyan
            foreach ($p in $ccProcs) {
                try {
                    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
                    $null = $p.WaitForExit(3000)
                } catch {}
            }
        }

        try {
            Copy-Item $srcBoostDll $dstBoostDll -Force
            Copy-Item $srcBoostCfg $dstBoostCfg -Force
            $boostCopied = $true
            break
        } catch {
            if ($attempt -eq 3) { throw }
            Start-Sleep -Milliseconds 300
        }
    }

    if ($boostCopied) {
        Write-Host "已部署控制中心响应加速模块: $dstBoostDll" -ForegroundColor Green
    }

    Start-Sleep -Seconds 2
    $ccRestarted = Get-Process -Name "ControlCenterTrayUI" -ErrorAction SilentlyContinue
    if (-not $ccRestarted) {
        # Launch via explorer.exe so ControlCenterTrayUI stays at Medium integrity for non-elevated TopBar PostMessage.
        Start-Process "explorer.exe" -ArgumentList ('"' + $ccExe + '"')
    }
}

Write-Host "完成！可在设置「Magic 视界工具箱」中随时开关全部功能（支持页面内一键卸载）。日志: $env:TEMP\MagicTaskbar\adaptive-topbar.log" -ForegroundColor Green

} catch {
    Write-Host "部署失败: $($_.Exception.Message)" -ForegroundColor Red
    $exitCode = 1
} finally {
    if ($PauseOnExit) {
        Write-Host "`n按任意键关闭窗口..." -ForegroundColor Gray
        $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    }
}

exit $exitCode
