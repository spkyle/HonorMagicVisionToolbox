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
        $null = Start-Process -FilePath "powershell.exe" -ArgumentList $argList -Verb RunAs
        exit 0
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

# Strip Internet Mark-of-the-Web (Zone.Identifier) so .NET 4.8 / .NET 8 never blocks downloaded DLLs or scripts.
Get-ChildItem -Path $PSScriptRoot -Recurse -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue

$appDir = "C:\Program Files\HONOR\MagicAnimation\MagicTaskbarApp"
if (-not (Test-Path $appDir)) {
    throw "未找到荣耀 Magic 视界安装目录: $appDir"
}

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

$mcDir = "C:\Program Files\HONOR\PCManager"
$mcExe = Join-Path $mcDir "MessageCenterUI.exe"
$srcBridgeDll = Join-Path $PSScriptRoot "bin\Release\net6.0-windows\MessageCenterBridge.dll"
$dstBridgeDll = Join-Path $mcDir "MessageCenterBridge.dll"
$dstBridgeCfg = Join-Path $mcDir "MessageCenterUI.exe.config"
$bakBridgeCfg = Join-Path $mcDir "MessageCenterUI.exe.config.bak"

$needsBuild = (-not (Test-Path $srcDll)) -or (-not (Test-Path $srcBoostDll)) -or (-not (Test-Path $srcBoostCfg)) -or (-not (Test-Path $srcBridgeDll))
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
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw "未找到预编译的 bin\Release 产物且本机未安装 .NET SDK。请确认下载并完整解压 Release 页面中的 MagicVisionToolbox-v*.zip（不要只下载 Source code 源码包）。"
    }
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
    Unblock-File -Path $dstDll -ErrorAction SilentlyContinue
    Write-Host "已部署 DLL: $dstDll" -ForegroundColor Green
}

if (Test-Path $srcRestore) {
    Copy-Item $srcRestore $dstRestore -Force -ErrorAction SilentlyContinue
    Unblock-File -Path $dstRestore -ErrorAction SilentlyContinue
}

$json = Get-Content $rcPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($null -eq $json.runtimeOptions.configProperties) {
    $json.runtimeOptions | Add-Member -NotePropertyName configProperties -NotePropertyValue ([PSCustomObject]@{})
}
$json.runtimeOptions.configProperties | Add-Member -NotePropertyName "STARTUP_HOOKS" -NotePropertyValue $dstDll -Force
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($rcPath, ($json | ConvertTo-Json -Depth 10), $utf8NoBom)
$rcWriteUtc = (Get-Item $rcPath).LastWriteTimeUtc
Write-Host "已更新 runtimeconfig STARTUP_HOOKS。" -ForegroundColor Green

# Guard against Honor watchdog relaunching magictaskbar-ui before runtimeconfig was updated.
$staleProcs = @(Get-Process -Name "magictaskbar-ui" -ErrorAction SilentlyContinue | Where-Object {
    try { $_.StartTime.ToUniversalTime() -lt $rcWriteUtc } catch { $true }
})
if ($staleProcs.Count -gt 0) {
    foreach ($p in $staleProcs) {
        try {
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            $null = $p.WaitForExit(3000)
        } catch {}
    }
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
        Unblock-File -Path $dstBoostDll, $dstBoostCfg -ErrorAction SilentlyContinue
        # Refresh timestamp after Copy-Item (which preserves source LastWriteTime) before watchdog stale check.
        Start-Sleep -Milliseconds 150
        $cfgWriteUtc = [DateTime]::UtcNow
        (Get-Item $dstBoostCfg).LastWriteTimeUtc = $cfgWriteUtc
        Write-Host "已部署控制中心响应加速模块: $dstBoostDll" -ForegroundColor Green
    }

    if ((-not (Test-Path $dstBoostDll)) -or (-not (Test-Path $dstBoostCfg))) {
        throw "未检测到控制中心响应加速模块或配置文件: $dstBoostCfg"
    }

    # Guard against Honor MBAProcessWatcher relaunching ControlCenterTrayUI.exe before .exe.config landed.
    $cfgWriteUtc = (Get-Item $dstBoostCfg).LastWriteTimeUtc
    $staleCcProcs = @(Get-Process -Name "ControlCenterTrayUI" -ErrorAction SilentlyContinue | Where-Object {
        try { $_.StartTime.ToUniversalTime() -lt $cfgWriteUtc } catch { $true }
    })
    if ($staleCcProcs.Count -gt 0) {
        foreach ($p in $staleCcProcs) {
            try {
                Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
                $null = $p.WaitForExit(3000)
            } catch {}
        }
    }

    Start-Sleep -Seconds 2
    $ccRestarted = Get-Process -Name "ControlCenterTrayUI" -ErrorAction SilentlyContinue
    if (-not $ccRestarted) {
        # Launch via explorer.exe so ControlCenterTrayUI stays at Medium integrity for non-elevated TopBar PostMessage.
        Start-Process "explorer.exe" -ArgumentList ('"' + $ccExe + '"')
    }
}

if (Test-Path $mcExe) {
    if ((Test-Path $dstBridgeCfg) -and -not (Test-Path $bakBridgeCfg)) {
        $existingMcCfgText = Get-Content $dstBridgeCfg -Raw -ErrorAction SilentlyContinue
        if ($existingMcCfgText -and ($existingMcCfgText -notmatch "MessageCenterBridge")) {
            Copy-Item $dstBridgeCfg $bakBridgeCfg -Force
            Write-Host "已备份通知中心原始配置到: $bakBridgeCfg" -ForegroundColor Green
        }
    }

    $bridgeCopied = $false
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $mcProcs = Get-Process -Name "MessageCenterUI" -ErrorAction SilentlyContinue
        if ($mcProcs) {
            Write-Host "正在停止 MessageCenterUI.exe 以部署通知桥接模块..." -ForegroundColor Cyan
            foreach ($p in $mcProcs) {
                try {
                    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
                    $null = $p.WaitForExit(3000)
                } catch {}
            }
        }

        try {
            Copy-Item $srcBridgeDll $dstBridgeDll -Force

            $mcBaseXmlText = if (Test-Path $bakBridgeCfg) {
                Get-Content $bakBridgeCfg -Raw -Encoding UTF8
            } elseif (Test-Path $dstBridgeCfg) {
                Get-Content $dstBridgeCfg -Raw -Encoding UTF8
            } else {
                '<?xml version="1.0" encoding="utf-8"?><configuration><startup><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8" /></startup><runtime /></configuration>'
            }

            [xml]$mcXml = $mcBaseXmlText
            $confNode = $mcXml.SelectSingleNode("/configuration")
            if ($null -eq $confNode) {
                throw "无法解析 MessageCenterUI.exe.config XML 根节点"
            }

            $runtimeNode = $confNode.SelectSingleNode("runtime")
            if ($null -eq $runtimeNode) {
                $runtimeNode = $mcXml.CreateElement("runtime")
                [void]$confNode.AppendChild($runtimeNode)
            }

            foreach ($oldNode in @($runtimeNode.SelectNodes("appDomainManagerAssembly | appDomainManagerType"))) {
                [void]$runtimeNode.RemoveChild($oldNode)
            }

            $admAsm = $mcXml.CreateElement("appDomainManagerAssembly")
            $admAsm.SetAttribute("value", "MessageCenterBridge, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null")
            [void]$runtimeNode.PrependChild($admAsm)

            $admType = $mcXml.CreateElement("appDomainManagerType")
            $admType.SetAttribute("value", "MessageCenterBridge.MessageCenterDomainManager")
            [void]$runtimeNode.PrependChild($admType)

            $mcUtf8 = New-Object System.Text.UTF8Encoding($false)
            [System.IO.File]::WriteAllText($dstBridgeCfg, $mcXml.OuterXml, $mcUtf8)
            $bridgeCopied = $true
            break
        } catch {
            if ($attempt -eq 3) { throw }
            Start-Sleep -Milliseconds 300
        }
    }

    if ($bridgeCopied) {
        Unblock-File -Path $dstBridgeDll, $dstBridgeCfg -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 150
        $mcCfgWriteUtc = [DateTime]::UtcNow
        (Get-Item $dstBridgeCfg).LastWriteTimeUtc = $mcCfgWriteUtc
        Write-Host "已部署通知中心桥接模块: $dstBridgeDll" -ForegroundColor Green
    }

    $mcCfgWriteUtc = (Get-Item $dstBridgeCfg).LastWriteTimeUtc
    $staleMcProcs = @(Get-Process -Name "MessageCenterUI" -ErrorAction SilentlyContinue | Where-Object {
        try { $_.StartTime.ToUniversalTime() -lt $mcCfgWriteUtc } catch { $true }
    })
    if ($staleMcProcs.Count -gt 0) {
        foreach ($p in $staleMcProcs) {
            try {
                Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
                $null = $p.WaitForExit(3000)
            } catch {}
        }
    }
}

# Launch magictaskbar-ui after MessageCenterBridge is in place so its startup pre-warm loads the bridge cleanly.
Start-Sleep -Seconds 2
$restarted = Get-Process -Name "magictaskbar-ui" -ErrorAction SilentlyContinue
if (-not $restarted) {
    $exePath = Join-Path $appDir "magictaskbar-ui.exe"
    Start-Process "explorer.exe" -ArgumentList ('"' + $exePath + '"')
}

if (-not (Test-Path $dstDll)) {
    throw "未检测到已部署的 DLL: $dstDll"
}
$verifyJson = Get-Content $rcPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($verifyJson.runtimeOptions.configProperties.STARTUP_HOOKS -ne $dstDll) {
    throw "STARTUP_HOOKS 校验失败，请检查 $rcPath 写入权限。"
}

Write-Host "部署完成。可在 Magic 视界设置「Magic 视界工具箱」中调整各项开关或卸载。日志: $env:TEMP\MagicTaskbar\adaptive-topbar.log" -ForegroundColor Green

} catch {
    Write-Host "部署失败: $($_.Exception.Message)" -ForegroundColor Red
    $exitCode = 1
} finally {
    if ($PauseOnExit -or $exitCode -ne 0) {
        Write-Host "`n按任意键关闭窗口..." -ForegroundColor Gray
        try {
            $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
        } catch {
            Read-Host "按回车键退出"
        }
    }
}

exit $exitCode
