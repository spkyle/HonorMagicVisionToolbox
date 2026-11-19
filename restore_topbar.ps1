# Requires Administrator privileges
param(
    [switch]$Elevated,
    [switch]$PauseOnExit,
    [switch]$SelfDelete
)

$ErrorActionPreference = "Stop"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    if ($Elevated) {
        Write-Host "需要管理员权限执行卸载与还原。" -ForegroundColor Yellow
        exit 1
    }

    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"", "-Elevated")
    if ($PauseOnExit) { $argList += "-PauseOnExit" }
    if ($SelfDelete) { $argList += "-SelfDelete" }
    try {
        $proc = Start-Process -FilePath "powershell.exe" -ArgumentList $argList -Verb RunAs -Wait -PassThru
        exit $proc.ExitCode
    } catch {
        Write-Host "已取消管理员授权，卸载中止。" -ForegroundColor Yellow
        if ($PauseOnExit) {
            Write-Host "按任意键关闭..." -ForegroundColor Gray
            $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
        }
        exit 1
    }
}

$appDir = "C:\Program Files\HONOR\MagicAnimation\MagicTaskbarApp"
$dstDll = Join-Path $appDir "TopBarAdaptiveColor.dll"
$dstRestore = Join-Path $appDir "restore_adaptive_topbar.ps1"
$rcPath = Join-Path $appDir "magictaskbar-ui.runtimeconfig.json"
$bakPath = Join-Path $appDir "magictaskbar-ui.runtimeconfig.json.bak"

if (Test-Path $bakPath) {
    Copy-Item $bakPath $rcPath -Force
    Remove-Item $bakPath -Force
    Write-Host "已还原 magictaskbar-ui.runtimeconfig.json" -ForegroundColor Green
} else {
    $json = Get-Content $rcPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($null -ne $json.runtimeOptions.configProperties -and $json.runtimeOptions.configProperties.PSObject.Properties.Name -contains "STARTUP_HOOKS") {
        $json.runtimeOptions.configProperties.PSObject.Properties.Remove("STARTUP_HOOKS")
        $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText($rcPath, ($json | ConvertTo-Json -Depth 10), $utf8NoBom)
        Write-Host "已从 runtimeconfig 移除 STARTUP_HOOKS" -ForegroundColor Green
    }
}

$procs = Get-Process -Name "magictaskbar-ui" -ErrorAction SilentlyContinue
if ($procs) {
    $procs | Stop-Process -Force
    Start-Sleep -Seconds 3
}

if (Test-Path $dstDll) {
    Remove-Item $dstDll -Force -ErrorAction SilentlyContinue
}

if (Test-Path $dstRestore) {
    Remove-Item $dstRestore -Force -ErrorAction SilentlyContinue
}

$adaptiveCfg = Join-Path $env:APPDATA "taskbarAndAIbar\adaptive-topbar.json"
if (Test-Path $adaptiveCfg) {
    Remove-Item $adaptiveCfg -Force -ErrorAction SilentlyContinue
}

$pinnedTrayCfg = Join-Path $env:APPDATA "taskbarAndAIbar\pinned-tray-icons.json"
if (Test-Path $pinnedTrayCfg) {
    Remove-Item $pinnedTrayCfg -Force -ErrorAction SilentlyContinue
}

$adaptiveTempIcons = Join-Path $env:TEMP "MagicTaskbar\adaptive-icons"
if (Test-Path $adaptiveTempIcons) {
    Remove-Item $adaptiveTempIcons -Recurse -Force -ErrorAction SilentlyContinue
}

$adaptiveLog = Join-Path $env:TEMP "MagicTaskbar\adaptive-topbar.log"
if (Test-Path $adaptiveLog) {
    Remove-Item $adaptiveLog -Force -ErrorAction SilentlyContinue
}

$ccDir = "C:\Program Files\HONOR\NotifControlUI"
$ccExe = Join-Path $ccDir "ControlCenterTrayUI.exe"
$dstBoostDll = Join-Path $ccDir "ControlCenterSpeedBoost.dll"
$dstBoostCfg = Join-Path $ccDir "ControlCenterTrayUI.exe.config"
$bakBoostCfg = Join-Path $ccDir "ControlCenterTrayUI.exe.config.bak"

$ccProcs = Get-Process -Name "ControlCenterTrayUI" -ErrorAction SilentlyContinue
if ($ccProcs) {
    $ccProcs | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

if (Test-Path $bakBoostCfg) {
    Copy-Item $bakBoostCfg $dstBoostCfg -Force
    Remove-Item $bakBoostCfg -Force -ErrorAction SilentlyContinue
    Write-Host "已还原 ControlCenterTrayUI.exe.config" -ForegroundColor Green
} elseif (Test-Path $dstBoostCfg) {
    Remove-Item $dstBoostCfg -Force -ErrorAction SilentlyContinue
}

if (Test-Path $dstBoostDll) {
    Remove-Item $dstBoostDll -Force -ErrorAction SilentlyContinue
}

$boostLog = Join-Path $env:TEMP "MagicTaskbar\control-center-boost.log"
if (Test-Path $boostLog) {
    Remove-Item $boostLog -Force -ErrorAction SilentlyContinue
}

$tpReg = "HKCU:\SOFTWARE\PCManager\TouchPadSetting"
if (Test-Path $tpReg) {
    Set-ItemProperty -Path $tpReg -Name "TripleFingerLightTouch" -Value 1 -Type DWord -ErrorAction SilentlyContinue
    $g = (Get-ItemProperty -Path $tpReg -Name "TouchpadGestures" -ErrorAction SilentlyContinue).TouchpadGestures
    if ($g -is [string]) {
        $parts = @($g.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
        if ($parts -notcontains '27') {
            $parts += '27'
            Set-ItemProperty -Path $tpReg -Name "TouchpadGestures" -Value ($parts -join ',') -Type String -ErrorAction SilentlyContinue
        }
    }

    try {
        [ushort]$tpVid = 0x27C6
        [ushort]$tpPid = 0x0F9A
        $hwidPath = "HKLM:\SOFTWARE\HONOR\HWID"
        if (Test-Path $hwidPath) {
            $hwidProps = Get-ItemProperty -Path $hwidPath -ErrorAction SilentlyContinue
            if ($null -ne $hwidProps) {
                $matchedProp = $hwidProps.PSObject.Properties | Where-Object { $_.Name -like "*GoodixForcePad*" } | Select-Object -First 1
                if ($null -eq $matchedProp) {
                    $matchedProp = $hwidProps.PSObject.Properties | Where-Object { $_.Name -like "*ForcePad*" } | Select-Object -First 1
                }
                if ($null -ne $matchedProp -and ($matchedProp.Value -is [string])) {
                    if ($matchedProp.Value -match 'VID_([0-9A-Fa-f]{4}).*PID_([0-9A-Fa-f]{4})') {
                        $tpVid = [Convert]::ToUInt16($matches[1], 16)
                        $tpPid = [Convert]::ToUInt16($matches[2], 16)
                    }
                }
            }
        }

        if (-not ("TopBarRestoreTouchpadHid" -as [type])) {
            Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class TopBarRestoreTouchpadHid
{
    private const uint GenericRead = 0x80000000u;
    private const uint GenericWrite = 0x40000000u;
    private const uint FileShareRead = 0x00000001u;
    private const uint FileShareWrite = 0x00000002u;
    private const uint OpenExisting = 3u;
    private const uint DigcfPresent = 0x00000002u;
    private const uint DigcfDeviceInterface = 0x00000010u;
    private const ushort VendorUsagePage = 0xFF00;
    private const ushort VendorControlUsage = 0x0001;
    private const int HidpStatusSuccess = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HiddAttributes
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct HidpCaps
    {
        [FieldOffset(0)] public ushort Usage;
        [FieldOffset(2)] public ushort UsagePage;
        [FieldOffset(4)] public ushort InputReportByteLength;
        [FieldOffset(6)] public ushort OutputReportByteLength;
        [FieldOffset(8)] public ushort FeatureReportByteLength;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SendNotifyMessageW(UIntPtr hWnd, uint msg, UIntPtr wParam, string lParam);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetAttributes(IntPtr hidDeviceObject, ref HiddAttributes attributes);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetPreparsedData(IntPtr hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HidpCaps capabilities);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    public static bool TryEnableTripleFingerLightTouch(ushort vid, ushort pid, out string detail)
    {
        Guid hidGuid;
        HidD_GetHidGuid(out hidGuid);
        IntPtr invalid = new IntPtr(-1);
        IntPtr devInfo = SetupDiGetClassDevsW(ref hidGuid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (devInfo == IntPtr.Zero || devInfo == invalid)
        {
            detail = "SetupDiGetClassDevsW failed";
            return false;
        }

        try
        {
            SpDeviceInterfaceData ifData = new SpDeviceInterfaceData();
            ifData.cbSize = Marshal.SizeOf(typeof(SpDeviceInterfaceData));
            uint idx = 0;

            while (SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref hidGuid, idx, ref ifData))
            {
                idx++;
                uint reqSize;
                SetupDiGetDeviceInterfaceDetailW(devInfo, ref ifData, IntPtr.Zero, 0, out reqSize, IntPtr.Zero);
                if (reqSize <= 4)
                {
                    continue;
                }

                string path = null;
                IntPtr buf = Marshal.AllocHGlobal((int)reqSize);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    uint ignored;
                    if (SetupDiGetDeviceInterfaceDetailW(devInfo, ref ifData, buf, reqSize, out ignored, IntPtr.Zero))
                    {
                        path = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }

                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                ushort outLen = 0;
                IntPtr qh = CreateFileW(path, 0u, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0u, IntPtr.Zero);
                if (qh != invalid && qh != IntPtr.Zero)
                {
                    try
                    {
                        HiddAttributes attrs = new HiddAttributes();
                        attrs.Size = Marshal.SizeOf(typeof(HiddAttributes));
                        IntPtr pp;
                        if (HidD_GetAttributes(qh, ref attrs) && attrs.VendorID == vid && attrs.ProductID == pid && HidD_GetPreparsedData(qh, out pp) && pp != IntPtr.Zero)
                        {
                            try
                            {
                                HidpCaps caps;
                                if (HidP_GetCaps(pp, out caps) == HidpStatusSuccess &&
                                    caps.UsagePage == VendorUsagePage &&
                                    caps.Usage == VendorControlUsage &&
                                    caps.OutputReportByteLength >= 3)
                                {
                                    outLen = caps.OutputReportByteLength;
                                }
                            }
                            finally
                            {
                                HidD_FreePreparsedData(pp);
                            }
                        }
                    }
                    finally
                    {
                        CloseHandle(qh);
                    }
                }

                if (outLen < 3)
                {
                    continue;
                }

                IntPtr wh = CreateFileW(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0u, IntPtr.Zero);
                if (wh == invalid || wh == IntPtr.Zero)
                {
                    detail = "CreateFileW failed err=" + Marshal.GetLastWin32Error();
                    return false;
                }

                try
                {
                    byte[] report = new byte[Math.Max((int)outLen, 3)];
                    report[0] = 0x0E;
                    report[1] = 0x11;
                    report[2] = 0x01;
                    uint written;
                    if (WriteFile(wh, report, (uint)report.Length, out written, IntPtr.Zero) && written == report.Length)
                    {
                        SendNotifyMessageW(new UIntPtr(0xFFFFu), 0x001Au, UIntPtr.Zero, @"SOFTWARE\PCManager\TouchPadSetting");
                        detail = "HID [0E 11 01] (" + written + "B)";
                        return true;
                    }

                    detail = "WriteFile failed err=" + Marshal.GetLastWin32Error();
                    return false;
                }
                finally
                {
                    CloseHandle(wh);
                }
            }

            detail = "ForcePad vendor HID interface not found";
            return false;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfo);
        }
    }
}
'@
        }

        $hidDetail = ""
        if ([TopBarRestoreTouchpadHid]::TryEnableTripleFingerLightTouch($tpVid, $tpPid, [ref]$hidDetail)) {
            Write-Host "已还原压力触控板三指轻触固件状态 ($hidDetail)" -ForegroundColor Green
        } else {
            Write-Host "注意: 未写入触控板固件 ($hidDetail)" -ForegroundColor Yellow
        }
    } catch {
        Write-Host "注意: 还原触控板 HID 固件时跳过 ($($_.Exception.Message))" -ForegroundColor Yellow
    }

    Write-Host "已还原压力触控板三指轻触智慧搜索默认设置" -ForegroundColor Green
}

$restarted = Get-Process -Name "magictaskbar-ui" -ErrorAction SilentlyContinue
if (-not $restarted) {
    $exePath = Join-Path $appDir "magictaskbar-ui.exe"
    Start-Process "explorer.exe" -ArgumentList ('"' + $exePath + '"')
}

if (Test-Path $ccExe) {
    Start-Sleep -Seconds 1
    $ccRestarted = Get-Process -Name "ControlCenterTrayUI" -ErrorAction SilentlyContinue
    if (-not $ccRestarted) {
        Start-Process "explorer.exe" -ArgumentList ('"' + $ccExe + '"')
    }
}

Write-Host "已彻底还原为官方原始状态，并清理所有补丁残留文件。" -ForegroundColor Green

if ($SelfDelete -and (Test-Path $PSCommandPath)) {
    Remove-Item $PSCommandPath -Force -ErrorAction SilentlyContinue
}

if ($PauseOnExit) {
    Write-Host "`n按任意键关闭窗口..." -ForegroundColor Gray
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
}
