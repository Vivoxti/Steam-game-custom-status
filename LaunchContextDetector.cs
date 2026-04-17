using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SteamGameCustomStatus;

internal static class LaunchContextDetector
{
    public static bool IsSteamLaunch()
    {
        try
        {
            var visitedProcessIds = new HashSet<int> { Environment.ProcessId };
            var currentProcessId = Environment.ProcessId;

            for (var depth = 0; depth < 16; depth++)
            {
                var parentProcessId = TryGetParentProcessId(currentProcessId);
                if (parentProcessId is null || parentProcessId <= 0 || !visitedProcessIds.Add(parentProcessId.Value))
                {
                    return false;
                }

                using var parentProcess = Process.GetProcessById(parentProcessId.Value);
                if (string.Equals(parentProcess.ProcessName, "steam", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                currentProcessId = parentProcess.Id;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static int? TryGetParentProcessId(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var processInformation = new ProcessBasicInformation();
            var status = NtQueryInformationProcess(
                process.Handle,
                0,
                ref processInformation,
                Marshal.SizeOf<ProcessBasicInformation>(),
                out _);

            if (status != 0)
            {
                return null;
            }

            var parentProcessId = processInformation.InheritedFromUniqueProcessId.ToInt64();
            if (parentProcessId <= 0 || parentProcessId > int.MaxValue)
            {
                return null;
            }

            return (int)parentProcessId;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }
}
