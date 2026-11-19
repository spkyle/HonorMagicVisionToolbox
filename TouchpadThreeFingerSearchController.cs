using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace TopBarAdaptiveColor
{
    /// <summary>
    /// Controls HONOR Pressure Touchpad's three-finger light-touch Smart Search trigger
    /// (GoodixForcePad vendor HID report [0x0E, 0x11, 0x00/0x01]) alongside HKCU\SOFTWARE\PCManager\TouchPadSetting.
    ///
    /// Why HID control + drift/resume guards are required:
    /// MagicTouchPadPlugin.dll (in MBAMessageCenter.exe) couples ThreeFingerDrag (0x0E 0x05) and
    /// TripleFingerLightTouch (0x0E 0x11) inside DLOpermpl, never watches HKCU\...\TouchPadSetting,
    /// ignores WM_SETTINGCHANGE, unconditionally launches AISearchUI.exe on Goodix input report buf[1]==0x10,
    /// and re-sends 0E 11 &lt;ThreeFingerDrag&gt; on startup and 3s after power resume.
    /// </summary>
    internal static class TouchpadThreeFingerSearchController
    {
        internal const string DefaultRegistrySubKeyPath = @"SOFTWARE\PCManager\TouchPadSetting";
        internal const string ValueTripleFingerLightTouch = "TripleFingerLightTouch";
        internal const string ValueTouchpadGestures = "TouchpadGestures";
        internal const string ThreeFingerLightTouchGestureId = "27";

        internal const string HonorHardwareIdSubKeyPath = @"SOFTWARE\HONOR\HWID";
        internal const string ForcePadHwidKeyword = "GoodixForcePad";
        internal const ushort DefaultGoodixVendorId = 0x27C6;
        internal const ushort DefaultGoodixProductId = 0x0F9A;
        internal const ushort VendorHidUsagePage = 0xFF00;
        internal const ushort VendorHidControlUsage = 0x0001;
        internal const byte HidReportIdTouchpadOption = 0x0E;
        internal const byte HidCmdTripleFingerLightTouch = 0x11;

        private static readonly UIntPtr HwndBroadcast = new(0xFFFF);
        private const uint WmSettingChange = 0x001A;
        private const int RegNotifyChangeLastSet = 0x00000004;
        private const int ErrorKeyDeleted = 1018;
        private const int ResumeReapplyDelayMs = 3800;
        private const int RegWatchSettleDelayMs = 250;

        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const uint DigcfPresent = 0x00000002;
        private const uint DigcfDeviceInterface = 0x00000010;
        private const int HidpStatusSuccess = 0x00110000;

        private static readonly object ApplySync = new();
        private static readonly object ResumeTimerSync = new();
        private static readonly ManualResetEvent StopWatchEvent = new(false);

        private static int _initialized;
        private static int _watchersStarted;
        private static int _processExiting;
        private static int _desiredAllowed = -1;
        private static Timer? _resumeReapplyTimer;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 1)
            {
                return;
            }

            AdaptiveSettingsStore.TouchpadThreeFingerSearchChanged += OnSettingChanged;

            // Only touch the user's touchpad settings/firmware at startup if disabled in Toolbox.
            bool enabled = AdaptiveSettingsStore.IsTouchpadThreeFingerSearchEnabled;
            Volatile.Write(ref _desiredAllowed, enabled ? 1 : 0);
            if (!enabled)
            {
                Apply(false, DefaultRegistrySubKeyPath, notifyShell: true);
            }

            DiagnosticLog.Info($"TouchpadThreeFingerSearchController initialized (enabled={enabled}).");
        }

        private static void OnSettingChanged(bool enabled)
        {
            Volatile.Write(ref _desiredAllowed, enabled ? 1 : 0);
            Apply(enabled, DefaultRegistrySubKeyPath, notifyShell: true);
        }

        internal static bool Apply(
            bool allowThreeFingerSearch,
            string subKeyPath = DefaultRegistrySubKeyPath,
            bool notifyShell = true)
        {
            try
            {
                bool isDefaultHive = string.Equals(subKeyPath, DefaultRegistrySubKeyPath, StringComparison.OrdinalIgnoreCase);

                lock (ApplySync)
                {
                    if (notifyShell && isDefaultHive)
                    {
                        Volatile.Write(ref _desiredAllowed, allowThreeFingerSearch ? 1 : 0);
                    }

                    using RegistryKey? key = Registry.CurrentUser.CreateSubKey(subKeyPath, writable: true);
                    if (key == null)
                    {
                        DiagnosticLog.Warn($"TouchPadSetting registry key unavailable: HKCU\\{subKeyPath}");
                        return false;
                    }

                    int targetDword = allowThreeFingerSearch ? 1 : 0;
                    object? currentTriple = key.GetValue(ValueTripleFingerLightTouch);
                    if (currentTriple is not int existingDword || existingDword != targetDword)
                    {
                        key.SetValue(ValueTripleFingerLightTouch, targetDword, RegistryValueKind.DWord);
                    }

                    string? rawGestures = key.GetValue(ValueTouchpadGestures) as string;
                    if (rawGestures != null || !allowThreeFingerSearch)
                    {
                        string updatedGestures = UpdateGesturesCsv(rawGestures, allowThreeFingerSearch);
                        if (!string.Equals(rawGestures, updatedGestures, StringComparison.Ordinal))
                        {
                            key.SetValue(ValueTouchpadGestures, updatedGestures, RegistryValueKind.String);
                        }
                    }

                    if (notifyShell && isDefaultHive)
                    {
                        bool hidOk = TrySendGoodixGestureReport(
                            HidCmdTripleFingerLightTouch,
                            allowThreeFingerSearch,
                            out string hidDetail);

                        BroadcastTouchpadSettingChange();

                        if (hidOk)
                        {
                            DiagnosticLog.Info(
                                $"Touchpad three-finger AI Search updated: allow={allowThreeFingerSearch}, " +
                                $"{ValueTripleFingerLightTouch}={targetDword}, hid={hidDetail}.");
                        }
                        else
                        {
                            DiagnosticLog.Warn(
                                $"Touchpad registry updated (allow={allowThreeFingerSearch}, {ValueTripleFingerLightTouch}={targetDword}) " +
                                $"but Goodix HID command failed: {hidDetail}");
                        }

                        if (!allowThreeFingerSearch)
                        {
                            EnsureRuntimeWatchersStarted();
                        }

                        return true;
                    }

                    DiagnosticLog.Info(
                        $"Touchpad three-finger AI Search registry updated: allow={allowThreeFingerSearch}, " +
                        $"{ValueTripleFingerLightTouch}={targetDword} (subKey={subKeyPath}).");
                    return true;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"TouchpadThreeFingerSearchController.Apply failed: {ex.Message}");
                return false;
            }
        }

        internal static string UpdateGesturesCsv(string? existingCsv, bool includeThreeFingerGesture)
        {
            var tokens = new List<string>();
            if (!string.IsNullOrWhiteSpace(existingCsv))
            {
                string[] parts = existingCsv.Split(',', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length; i++)
                {
                    string token = parts[i].Trim();
                    if (token.Length == 0)
                    {
                        continue;
                    }

                    if (!includeThreeFingerGesture &&
                        string.Equals(token, ThreeFingerLightTouchGestureId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!tokens.Contains(token))
                    {
                        tokens.Add(token);
                    }
                }
            }

            if (includeThreeFingerGesture && !tokens.Contains(ThreeFingerLightTouchGestureId))
            {
                tokens.Add(ThreeFingerLightTouchGestureId);
            }

            return string.Join(",", tokens);
        }

        internal static byte[] BuildGestureControlReport(int reportByteLength, byte commandId, bool enabled)
        {
            int length = Math.Max(reportByteLength, 3);
            var report = new byte[length];
            report[0] = HidReportIdTouchpadOption;
            report[1] = commandId;
            report[2] = enabled ? (byte)1 : (byte)0;
            return report;
        }

        internal static bool TryParseVidPidFromHwid(string? hwidValue, out ushort vendorId, out ushort productId)
        {
            vendorId = 0;
            productId = 0;
            if (string.IsNullOrWhiteSpace(hwidValue))
            {
                return false;
            }

            string[] entries = hwidValue.Split(';', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < entries.Length; i++)
            {
                string segment = entries[i].Trim();
                if (TryExtractHex4AfterToken(segment, "VID_", out ushort vid) &&
                    TryExtractHex4AfterToken(segment, "PID_", out ushort pid))
                {
                    vendorId = vid;
                    productId = pid;
                    return true;
                }
            }

            return false;
        }

        internal static bool IsTripleFingerSearchRegistryDrifted(object? rawTripleValue, string? rawGesturesCsv)
        {
            if (rawTripleValue is not int dwordValue || dwordValue != 0)
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(rawGesturesCsv))
            {
                string[] parts = rawGesturesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length; i++)
                {
                    if (string.Equals(parts[i].Trim(), ThreeFingerLightTouchGestureId, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryExtractHex4AfterToken(string text, string token, out ushort value)
        {
            value = 0;
            int idx = text.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return false;
            }

            int hexStart = idx + token.Length;
            if (hexStart + 4 > text.Length)
            {
                return false;
            }

            return ushort.TryParse(
                text.AsSpan(hexStart, 4),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out value);
        }

        private static void ResolveForcePadVidPid(out ushort vendorId, out ushort productId, out bool fromRegistry)
        {
            vendorId = DefaultGoodixVendorId;
            productId = DefaultGoodixProductId;
            fromRegistry = false;

            try
            {
                using RegistryKey? hwidKey = Registry.LocalMachine.OpenSubKey(HonorHardwareIdSubKeyPath, writable: false);
                if (hwidKey == null)
                {
                    return;
                }

                string[] names = hwidKey.GetValueNames();
                string? fallbackMatch = null;
                for (int i = 0; i < names.Length; i++)
                {
                    string name = names[i];
                    if (name.IndexOf(ForcePadHwidKeyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        string? raw = hwidKey.GetValue(name) as string;
                        if (TryParseVidPidFromHwid(raw, out vendorId, out productId))
                        {
                            fromRegistry = true;
                            return;
                        }
                    }
                    else if (fallbackMatch == null && name.IndexOf("ForcePad", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        fallbackMatch = hwidKey.GetValue(name) as string;
                    }
                }

                if (TryParseVidPidFromHwid(fallbackMatch, out vendorId, out productId))
                {
                    fromRegistry = true;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"Failed to read {HonorHardwareIdSubKeyPath}; using fallback VID/PID: {ex.Message}");
            }
        }

        private static bool TrySendGoodixGestureReport(byte commandId, bool enabled, out string detail)
        {
            ResolveForcePadVidPid(out ushort targetVid, out ushort targetPid, out bool fromHwid);

            HidD_GetHidGuid(out Guid hidGuid);
            IntPtr devInfoSet = SetupDiGetClassDevsW(
                ref hidGuid,
                null,
                IntPtr.Zero,
                DigcfPresent | DigcfDeviceInterface);

            if (devInfoSet == IntPtr.Zero || devInfoSet == new IntPtr(-1))
            {
                detail = $"SetupDiGetClassDevsW failed (err={Marshal.GetLastWin32Error()})";
                return false;
            }

            try
            {
                var interfaceData = new SpDeviceInterfaceData
                {
                    cbSize = Marshal.SizeOf<SpDeviceInterfaceData>()
                };

                int matchedVidPidCollections = 0;
                uint memberIndex = 0;

                while (SetupDiEnumDeviceInterfaces(devInfoSet, IntPtr.Zero, ref hidGuid, memberIndex, ref interfaceData))
                {
                    memberIndex++;

                    if (!TryGetDeviceInterfacePath(devInfoSet, ref interfaceData, out string? devicePath) ||
                        string.IsNullOrEmpty(devicePath))
                    {
                        continue;
                    }

                    if (!TryGetMatchingControlOutputLength(
                            devicePath,
                            targetVid,
                            targetPid,
                            out ushort outputReportLength,
                            out bool vidPidMatched))
                    {
                        if (vidPidMatched)
                        {
                            matchedVidPidCollections++;
                        }

                        continue;
                    }

                    using SafeFileHandle writeHandle = CreateFileW(
                        devicePath,
                        GenericRead | GenericWrite,
                        FileShareRead | FileShareWrite,
                        IntPtr.Zero,
                        OpenExisting,
                        0,
                        IntPtr.Zero);

                    if (writeHandle.IsInvalid)
                    {
                        detail = $"CreateFileW({devicePath}) failed (err={Marshal.GetLastWin32Error()})";
                        return false;
                    }

                    byte[] report = BuildGestureControlReport(outputReportLength, commandId, enabled);
                    if (!WriteFile(writeHandle, report, (uint)report.Length, out uint bytesWritten, IntPtr.Zero) ||
                        bytesWritten != report.Length)
                    {
                        detail = $"WriteFile({devicePath}) failed (written={bytesWritten}/{report.Length}, err={Marshal.GetLastWin32Error()})";
                        return false;
                    }

                    detail = $"sent [{report[0]:X2} {report[1]:X2} {report[2]:X2}] ({bytesWritten}B, VID={targetVid:X4}, PID={targetPid:X4}, hwid={fromHwid})";
                    return true;
                }

                detail = $"ForcePad vendor control HID interface not found (VID={targetVid:X4}, PID={targetPid:X4}, UsagePage={VendorHidUsagePage:X4}, Usage={VendorHidControlUsage:X4}, vidPidCollections={matchedVidPidCollections})";
                return false;
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfoSet);
            }
        }

        private static bool TryGetDeviceInterfacePath(
            IntPtr devInfoSet,
            ref SpDeviceInterfaceData interfaceData,
            out string? devicePath)
        {
            devicePath = null;
            SetupDiGetDeviceInterfaceDetailW(
                devInfoSet,
                ref interfaceData,
                IntPtr.Zero,
                0,
                out uint requiredSize,
                IntPtr.Zero);

            if (requiredSize <= 4)
            {
                return false;
            }

            IntPtr detailBuffer = Marshal.AllocHGlobal((int)requiredSize);
            try
            {
                // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize is 8 on 64-bit, 6 on 32-bit; DevicePath starts at offset 4.
                Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetailW(
                        devInfoSet,
                        ref interfaceData,
                        detailBuffer,
                        requiredSize,
                        out _,
                        IntPtr.Zero))
                {
                    return false;
                }

                devicePath = Marshal.PtrToStringUni(IntPtr.Add(detailBuffer, 4));
                return !string.IsNullOrEmpty(devicePath);
            }
            finally
            {
                Marshal.FreeHGlobal(detailBuffer);
            }
        }

        private static bool TryGetMatchingControlOutputLength(
            string devicePath,
            ushort targetVid,
            ushort targetPid,
            out ushort outputReportLength,
            out bool vidPidMatched)
        {
            outputReportLength = 0;
            vidPidMatched = false;

            using SafeFileHandle queryHandle = CreateFileW(
                devicePath,
                0,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);

            if (queryHandle.IsInvalid)
            {
                return false;
            }

            var attrs = new HiddAttributes
            {
                Size = Marshal.SizeOf<HiddAttributes>()
            };

            if (!HidD_GetAttributes(queryHandle, ref attrs) ||
                attrs.VendorID != targetVid ||
                attrs.ProductID != targetPid)
            {
                return false;
            }

            vidPidMatched = true;

            if (!HidD_GetPreparsedData(queryHandle, out IntPtr preparsedData) || preparsedData == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                if (HidP_GetCaps(preparsedData, out HidpCaps caps) != HidpStatusSuccess)
                {
                    return false;
                }

                if (caps.UsagePage == VendorHidUsagePage &&
                    caps.Usage == VendorHidControlUsage &&
                    caps.OutputReportByteLength >= 3)
                {
                    outputReportLength = caps.OutputReportByteLength;
                    return true;
                }

                return false;
            }
            finally
            {
                HidD_FreePreparsedData(preparsedData);
            }
        }

        private static void EnsureRuntimeWatchersStarted()
        {
            if (Interlocked.Exchange(ref _watchersStarted, 1) == 1)
            {
                return;
            }

            try
            {
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"Touchpad PowerModeChanged hook failed: {ex.Message}");
            }

            var watchThread = new Thread(RegWatchLoop)
            {
                IsBackground = true,
                Name = "TopBarAdaptiveColor.TouchpadRegWatch"
            };
            watchThread.Start();
        }

        private static bool IsEffectivelyDisabled()
        {
            int desired = Volatile.Read(ref _desiredAllowed);
            return desired == 0 || (desired < 0 && !AdaptiveSettingsStore.IsTouchpadThreeFingerSearchEnabled);
        }

        private static void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume ||
                Volatile.Read(ref _processExiting) != 0 ||
                !IsEffectivelyDisabled())
            {
                return;
            }

            // MagicTouchPadPlugin sleeps 3000ms after PBT_APMRESUMEAUTOMATIC before calling 0x8CB0 -> 0x4F50(ThreeFingerDrag).
            lock (ResumeTimerSync)
            {
                if (Volatile.Read(ref _processExiting) != 0)
                {
                    return;
                }

                if (_resumeReapplyTimer == null)
                {
                    _resumeReapplyTimer = new Timer(OnResumeReapplyTimer, null, ResumeReapplyDelayMs, Timeout.Infinite);
                }
                else
                {
                    _resumeReapplyTimer.Change(ResumeReapplyDelayMs, Timeout.Infinite);
                }
            }
        }

        private static void OnResumeReapplyTimer(object? state)
        {
            lock (ApplySync)
            {
                if (Volatile.Read(ref _processExiting) != 0 || !IsEffectivelyDisabled())
                {
                    return;
                }

                DiagnosticLog.Info("Power resume settled; enforcing disabled three-finger AI Search state on GoodixForcePad.");
                Apply(false, DefaultRegistrySubKeyPath, notifyShell: true);
            }
        }

        private static void OnProcessExit(object? sender, EventArgs e)
        {
            Interlocked.Exchange(ref _processExiting, 1);
            try { StopWatchEvent.Set(); } catch { }
            try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }

            lock (ResumeTimerSync)
            {
                try { _resumeReapplyTimer?.Dispose(); } catch { }
                _resumeReapplyTimer = null;
            }
        }

        private static void RegWatchLoop()
        {
            try
            {
                while (Volatile.Read(ref _processExiting) == 0)
                {
                    using RegistryKey? key = Registry.CurrentUser.CreateSubKey(DefaultRegistrySubKeyPath, writable: false);
                    if (key == null)
                    {
                        return;
                    }

                    using var changedEvent = new AutoResetEvent(false);
                    WaitHandle[] waitHandles = { changedEvent, StopWatchEvent };

                    while (Volatile.Read(ref _processExiting) == 0)
                    {
                        // Dedicated persistent thread owns RegNotifyChangeKeyValue so Windows will not prematurely signal on thread exit.
                        int status = RegNotifyChangeKeyValue(
                            key.Handle,
                            watchSubtree: false,
                            RegNotifyChangeLastSet,
                            changedEvent.SafeWaitHandle,
                            asynchronous: true);

                        if (status != 0)
                        {
                            if (status == ErrorKeyDeleted)
                            {
                                break;
                            }

                            DiagnosticLog.Warn($"Touchpad RegNotifyChangeKeyValue failed (err={status}); watcher stopping.");
                            return;
                        }

                        WaitHandle.WaitAny(waitHandles);
                        if (Volatile.Read(ref _processExiting) != 0)
                        {
                            return;
                        }

                        if (StopWatchEvent.WaitOne(RegWatchSettleDelayMs))
                        {
                            return;
                        }

                        ReapplyDisabledIfNeeded();
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"Touchpad registry watcher exited: {ex.Message}");
            }
        }

        private static void ReapplyDisabledIfNeeded()
        {
            lock (ApplySync)
            {
                if (Volatile.Read(ref _processExiting) != 0 || !IsEffectivelyDisabled())
                {
                    return;
                }

                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(DefaultRegistrySubKeyPath, writable: false);
                if (key == null)
                {
                    return;
                }

                object? rawTriple = key.GetValue(ValueTripleFingerLightTouch);
                string? rawGestures = key.GetValue(ValueTouchpadGestures) as string;
                if (!IsTripleFingerSearchRegistryDrifted(rawTriple, rawGestures))
                {
                    return;
                }

                DiagnosticLog.Info("Detected external TouchPadSetting drift while three-finger AI Search disabled; re-applying HID/registry guard.");
                Apply(false, DefaultRegistrySubKeyPath, notifyShell: true);
            }
        }

        private static void BroadcastTouchpadSettingChange()
        {
            try
            {
                SendNotifyMessage(HwndBroadcast, WmSettingChange, UIntPtr.Zero, DefaultRegistrySubKeyPath);
            }
            catch
            {
                // Non-fatal fallback notification.
            }
        }

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
            [FieldOffset(0)]
            public ushort Usage;

            [FieldOffset(2)]
            public ushort UsagePage;

            [FieldOffset(4)]
            public ushort InputReportByteLength;

            [FieldOffset(6)]
            public ushort OutputReportByteLength;

            [FieldOffset(8)]
            public ushort FeatureReportByteLength;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SendNotifyMessage(UIntPtr hWnd, uint msg, UIntPtr wParam, string lParam);

        [DllImport("advapi32.dll", SetLastError = false)]
        private static extern int RegNotifyChangeKeyValue(
            SafeRegistryHandle hKey,
            [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
            int dwNotifyFilter,
            SafeWaitHandle hEvent,
            [MarshalAs(UnmanagedType.Bool)] bool asynchronous);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HiddAttributes attributes);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern int HidP_GetCaps(IntPtr preparsedData, out HidpCaps capabilities);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(
            ref Guid classGuid,
            string? enumerator,
            IntPtr hwndParent,
            uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr deviceInfoSet,
            IntPtr deviceInfoData,
            ref Guid interfaceClassGuid,
            uint memberIndex,
            ref SpDeviceInterfaceData deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiGetDeviceInterfaceDetailW(
            IntPtr deviceInfoSet,
            ref SpDeviceInterfaceData deviceInterfaceData,
            IntPtr deviceInterfaceDetailData,
            uint deviceInterfaceDetailDataSize,
            out uint requiredSize,
            IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WriteFile(
            SafeFileHandle hFile,
            byte[] lpBuffer,
            uint nNumberOfBytesToWrite,
            out uint lpNumberOfBytesWritten,
            IntPtr lpOverlapped);
    }
}
