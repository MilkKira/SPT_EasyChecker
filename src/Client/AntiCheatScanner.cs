using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SPT_EasyChecker_Client
{
    internal sealed class AntiCheatScanner
    {
        private static readonly string[] SuspiciousProcessNames =
        {
            "cheatengine",
            "cheat engine",
            "x64dbg",
            "x32dbg",
            "ollydbg",
            "ida",
            "processhacker",
            "scylla",
            "reclass",
            "dnspy"
        };

        private static readonly string[] SuspiciousDriverNames =
        {
            "dbk64.sys",
            "dbk32.sys",
            "cedriver.sys",
            "cheatengine.sys",
            "kprocesshacker.sys",
            "ksdumperdriver.sys",
            "dbutil_2_3.sys"
        };

        private static readonly string[] SuspiciousDeviceNames =
        {
            @"\\.\DBKProcHacker",
            @"\\.\DBKKernel",
            @"\\.\CEDRIVER",
            @"\\.\CEDRIVER60",
            @"\\.\CEKernel",
            @"\\.\KProcessHacker"
        };

        private static readonly string[] TrustedExternalHandleOwners =
        {
            "SPT.Launcher",
            "Aki.Launcher"
        };

        public ClientAntiCheatReport Scan()
        {
            var findings = new List<string>();
            ScanProcesses(findings);
            ScanDebuggerState(findings);
            ScanKernelModules(findings);
            ScanKernelDevices(findings);
            ScanExternalProcessHandles(findings);

            return new ClientAntiCheatReport
            {
                HasCheatEngineProcess = findings.Any(f => f.StartsWith("process:", StringComparison.OrdinalIgnoreCase)),
                HasSuspiciousKernelModule = findings.Any(f => f.StartsWith("kernel:", StringComparison.OrdinalIgnoreCase)
                                                              || f.StartsWith("kernel-device:", StringComparison.OrdinalIgnoreCase)),
                HasMemoryPatchTool = findings.Any(f => f.StartsWith("memory-tool:", StringComparison.OrdinalIgnoreCase)),
                Findings = findings
            };
        }

        private static void ScanProcesses(ICollection<string> findings)
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var name = process.ProcessName ?? string.Empty;
                    var title = process.MainWindowTitle ?? string.Empty;
                    var combined = (name + " " + title).ToLowerInvariant();

                    if (SuspiciousProcessNames.Any(combined.Contains))
                    {
                        findings.Add("process:" + name);
                        findings.Add("memory-tool:" + name);
                        continue;
                    }

                    foreach (ProcessModule module in process.Modules)
                    {
                        var moduleName = module.ModuleName ?? string.Empty;
                        if (moduleName.IndexOf("cheatengine", StringComparison.OrdinalIgnoreCase) >= 0
                            || moduleName.IndexOf("vehdebug", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            findings.Add("memory-tool:" + name + "/" + moduleName);
                            break;
                        }
                    }
                }
                catch
                {
                    // Protected processes are expected to reject module enumeration.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        private static void ScanKernelModules(ICollection<string> findings)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

            foreach (var driverPath in EnumerateLoadedDrivers())
            {
                var driverName = Path.GetFileName(driverPath);
                if (SuspiciousDriverNames.Any(name => string.Equals(name, driverName, StringComparison.OrdinalIgnoreCase)))
                    findings.Add("kernel:" + driverName);
            }
        }

        private static void ScanDebuggerState(ICollection<string> findings)
        {
            if (Debugger.IsAttached)
                findings.Add("memory-tool:managed-debugger-attached");

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

            if (IsDebuggerPresent())
                findings.Add("memory-tool:native-debugger-attached");

            if (CheckRemoteDebuggerPresent(GetCurrentProcess(), out var present) && present)
                findings.Add("memory-tool:remote-debugger-present");
        }

        private static void ScanKernelDevices(ICollection<string> findings)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

            foreach (var deviceName in SuspiciousDeviceNames)
            {
                using (var handle = CreateFile(
                           deviceName,
                           0,
                           FileShare.ReadWrite,
                           IntPtr.Zero,
                           FileMode.Open,
                           0,
                           IntPtr.Zero))
                {
                    if (!handle.IsInvalid)
                        findings.Add("kernel-device:" + deviceName);
                }
            }
        }

        private static void ScanExternalProcessHandles(ICollection<string> findings)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

            var currentProcessId = Process.GetCurrentProcess().Id;
            IntPtr buffer = IntPtr.Zero;

            try
            {
                var bufferLength = 0x10000;
                var status = 0;

                do
                {
                    if (buffer != IntPtr.Zero)
                        Marshal.FreeHGlobal(buffer);

                    buffer = Marshal.AllocHGlobal(bufferLength);
                    status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, bufferLength, out var returnLength);
                    if (status == StatusInfoLengthMismatch)
                        bufferLength = Math.Max(bufferLength * 2, returnLength);
                }
                while (status == StatusInfoLengthMismatch && bufferLength < 64 * 1024 * 1024);

                if (status != 0) return;

                var handleCount = Marshal.ReadIntPtr(buffer).ToInt64();
                var entrySize = Marshal.SizeOf(typeof(SystemHandleTableEntryInfoEx));
                var entryPointer = IntPtr.Add(buffer, IntPtr.Size * 2);

                for (long index = 0; index < handleCount; index++)
                {
                    var entry = (SystemHandleTableEntryInfoEx)Marshal.PtrToStructure(
                        IntPtr.Add(entryPointer, (int)(index * entrySize)),
                        typeof(SystemHandleTableEntryInfoEx));

                    var ownerProcessId = entry.UniqueProcessId.ToInt64();
                    if (ownerProcessId == currentProcessId || ownerProcessId <= 4)
                        continue;

                    if (!HasDangerousProcessAccess(entry.GrantedAccess))
                        continue;

                    var sourceProcess = OpenProcess(ProcessDuplicateHandle | ProcessQueryLimitedInformation, false, (int)ownerProcessId);
                    if (sourceProcess == IntPtr.Zero)
                        continue;

                    try
                    {
                        if (!DuplicateHandle(
                                sourceProcess,
                                entry.HandleValue,
                                GetCurrentProcess(),
                                out var duplicatedHandle,
                                0,
                                false,
                                DuplicateSameAccess))
                            continue;

                        try
                        {
                            var targetProcessId = GetProcessId(duplicatedHandle);
                            if (targetProcessId != currentProcessId)
                                continue;

                            var processName = ResolveProcessName((int)ownerProcessId);
                            if (TrustedExternalHandleOwners.Any(trusted => string.Equals(trusted, processName, StringComparison.OrdinalIgnoreCase)))
                                continue;

                            findings.Add(
                                "external-handle:" + processName + "/" + ownerProcessId + "/access=0x" +
                                entry.GrantedAccess.ToString("x"));
                            findings.Add("memory-tool:" + processName);
                        }
                        finally
                        {
                            CloseHandle(duplicatedHandle);
                        }
                    }
                    finally
                    {
                        CloseHandle(sourceProcess);
                    }
                }
            }
            catch
            {
                // Handle table layouts can vary by Windows build; failing closed here would be too noisy.
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(buffer);
            }
        }

        private static bool HasDangerousProcessAccess(uint grantedAccess)
        {
            const uint dangerous =
                ProcessCreateThread |
                ProcessVmOperation |
                ProcessVmWrite |
                ProcessSetInformation |
                ProcessSuspendResume;

            return (grantedAccess & dangerous) != 0;
        }

        private static string ResolveProcessName(int processId)
        {
            try
            {
                using (var process = Process.GetProcessById(processId))
                    return process.ProcessName ?? "pid";
            }
            catch
            {
                return "pid";
            }
        }

        private static IEnumerable<string> EnumerateLoadedDrivers()
        {
            var drivers = new IntPtr[1024];
            var size = drivers.Length * IntPtr.Size;

            if (!EnumDeviceDrivers(drivers, size, out var bytesNeeded))
                yield break;

            var count = Math.Min(drivers.Length, bytesNeeded / IntPtr.Size);
            var buffer = new StringBuilder(1024);

            for (var index = 0; index < count; index++)
            {
                buffer.Clear();
                if (GetDeviceDriverBaseName(drivers[index], buffer, buffer.Capacity) > 0)
                    yield return buffer.ToString();
            }
        }

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EnumDeviceDrivers([Out] IntPtr[] lpImageBase, int cb, out int lpcbNeeded);

        [DllImport("psapi.dll", CharSet = CharSet.Auto)]
        private static extern int GetDeviceDriverBaseName(IntPtr imageBase, StringBuilder lpBaseName, int nSize);

        [DllImport("kernel32.dll")]
        private static extern bool IsDebuggerPresent();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CheckRemoteDebuggerPresent(IntPtr hProcess, out bool isDebuggerPresent);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DuplicateHandle(
            IntPtr sourceProcessHandle,
            IntPtr sourceHandle,
            IntPtr targetProcessHandle,
            out IntPtr targetHandle,
            uint desiredAccess,
            bool inheritHandle,
            uint options);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int GetProcessId(IntPtr process);

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(
            int systemInformationClass,
            IntPtr systemInformation,
            int systemInformationLength,
            out int returnLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            FileShare shareMode,
            IntPtr securityAttributes,
            FileMode creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemHandleTableEntryInfoEx
        {
            public IntPtr Object;
            public IntPtr UniqueProcessId;
            public IntPtr HandleValue;
            public uint GrantedAccess;
            public ushort CreatorBackTraceIndex;
            public ushort ObjectTypeIndex;
            public uint HandleAttributes;
            public uint Reserved;
        }

        private const int SystemExtendedHandleInformation = 64;
        private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

        private const uint ProcessCreateThread = 0x0002;
        private const uint ProcessVmOperation = 0x0008;
        private const uint ProcessVmWrite = 0x0020;
        private const uint ProcessDuplicateHandle = 0x0040;
        private const uint ProcessSetInformation = 0x0200;
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint ProcessSuspendResume = 0x0800;
        private const uint DuplicateSameAccess = 0x00000002;
    }
}
