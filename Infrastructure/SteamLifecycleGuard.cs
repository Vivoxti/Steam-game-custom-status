using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SteamGameCustomStatus.Infrastructure;

internal static class SteamLifecycleGuard
{
    private static readonly TimeSpan DefaultProcessExitWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultSteamShutdownWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultSteamStartupWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PostRestartRelaunchDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ExitCleanupDelay = TimeSpan.FromMilliseconds(750);

    public static bool TryStartSteamRestartHelper(string? steamExePath, ulong? relaunchRunGameId, int? waitForExitProcessId)
    {
        var script = BuildSteamRestartScript(steamExePath, relaunchRunGameId, waitForExitProcessId);
        return TryStartBackgroundPowerShell(script);
    }

    public static bool TryScheduleRunningAppIdCleanup(int waitForExitProcessId, uint expectedAppId)
    {
        if (waitForExitProcessId <= 0 || expectedAppId == 0)
        {
            return false;
        }

        var script = BuildRunningAppIdCleanupScript(waitForExitProcessId, expectedAppId);
        return TryStartBackgroundPowerShell(script);
    }

    private static string BuildSteamRestartScript(string? steamExePath, ulong? relaunchRunGameId, int? waitForExitProcessId)
    {
        var relaunchTarget = relaunchRunGameId.HasValue
            ? $"steam://rungameid/{relaunchRunGameId.Value}"
            : string.Empty;

        return string.Join(Environment.NewLine, new[]
        {
            "$ErrorActionPreference = 'SilentlyContinue'",
            $"$steamExePath = {ToPowerShellSingleQuotedLiteral(steamExePath ?? string.Empty)}",
            $"$relaunchTarget = {ToPowerShellSingleQuotedLiteral(relaunchTarget)}",
            $"$waitPid = {waitForExitProcessId ?? 0}",
            $"$waitForExitTimeoutMs = {(int)DefaultProcessExitWait.TotalMilliseconds}",
            $"$steamShutdownTimeoutMs = {(int)DefaultSteamShutdownWait.TotalMilliseconds}",
            $"$steamStartupTimeoutMs = {(int)DefaultSteamStartupWait.TotalMilliseconds}",
            $"$postRestartDelayMs = {(int)PostRestartRelaunchDelay.TotalMilliseconds}",
            "function Wait-ForProcessExit([int]$targetPid, [int]$timeoutMs) {",
            "    if ($targetPid -le 0) { return }",
            "    try {",
            "        $process = Get-Process -Id $targetPid -ErrorAction Stop",
            "        $null = $process.WaitForExit($timeoutMs)",
            "    } catch { }",
            "}",
            "function Get-SteamProcesses {",
            "    return @(Get-Process -Name 'steam' -ErrorAction SilentlyContinue)",
            "}",
            "function Wait-ForSteamState([bool]$shouldBeRunning, [int]$timeoutMs) {",
            "    $deadline = (Get-Date).AddMilliseconds($timeoutMs)",
            "    while ((Get-Date) -lt $deadline) {",
            "        $isRunning = (Get-SteamProcesses).Count -gt 0",
            "        if ($isRunning -eq $shouldBeRunning) { return $true }",
            "        Start-Sleep -Milliseconds 250",
            "    }",
            "    return $false",
            "}",
            "function Stop-SteamGracefully {",
            "    foreach ($process in Get-SteamProcesses) {",
            "        try {",
            "            if ($process.MainWindowHandle -ne 0) {",
            "                $null = $process.CloseMainWindow()",
            "            }",
            "        } catch { }",
            "    }",
            "    $null = Wait-ForSteamState -shouldBeRunning:$false -timeoutMs $steamShutdownTimeoutMs",
            "    if ((Get-SteamProcesses).Count -eq 0) { return }",
            "    foreach ($process in Get-SteamProcesses) {",
            "        try {",
            "            if (-not $process.HasExited) {",
            "                $process.Kill()",
            "            }",
            "        } catch { }",
            "    }",
            "    $null = Wait-ForSteamState -shouldBeRunning:$false -timeoutMs $steamShutdownTimeoutMs",
            "}",
            "function Start-Steam {",
            "    if (-not [string]::IsNullOrWhiteSpace($steamExePath)) {",
            "        Start-Process -FilePath $steamExePath | Out-Null",
            "        return",
            "    }",
            "    Start-Process 'steam://open/main' | Out-Null",
            "}",
            "if ($waitPid -gt 0) {",
            "    Wait-ForProcessExit -targetPid $waitPid -timeoutMs $waitForExitTimeoutMs",
            "    Start-Sleep -Milliseconds 500",
            "}",
            "Stop-SteamGracefully",
            "Start-Steam",
            "if (Wait-ForSteamState -shouldBeRunning:$true -timeoutMs $steamStartupTimeoutMs) {",
            "    Start-Sleep -Milliseconds $postRestartDelayMs",
            "}",
            "if (-not [string]::IsNullOrWhiteSpace($relaunchTarget)) {",
            "    Start-Process $relaunchTarget | Out-Null",
            "}"
        });
    }

    private static string BuildRunningAppIdCleanupScript(int waitForExitProcessId, uint expectedAppId)
    {
        return string.Join(Environment.NewLine, new[]
        {
            "$ErrorActionPreference = 'SilentlyContinue'",
            "$steamRegistryPath = 'HKCU:\\Software\\Valve\\Steam'",
            $"$waitPid = {waitForExitProcessId}",
            $"$expectedAppId = [uint32]{expectedAppId}",
            $"$waitForExitTimeoutMs = {(int)DefaultProcessExitWait.TotalMilliseconds}",
            $"$cleanupDelayMs = {(int)ExitCleanupDelay.TotalMilliseconds}",
            "function Wait-ForProcessExit([int]$targetPid, [int]$timeoutMs) {",
            "    if ($targetPid -le 0) { return }",
            "    try {",
            "        $process = Get-Process -Id $targetPid -ErrorAction Stop",
            "        $null = $process.WaitForExit($timeoutMs)",
            "    } catch { }",
            "}",
            "function Get-RunningAppId {",
            "    try {",
            "        $value = (Get-ItemProperty -Path $steamRegistryPath -Name 'RunningAppID' -ErrorAction SilentlyContinue).RunningAppID",
            "        if ($null -eq $value) { return [uint32]0 }",
            "        return [uint32]$value",
            "    } catch {",
            "        return [uint32]0",
            "    }",
            "}",
            "function Clear-RunningAppId {",
            "    try {",
            "        Set-ItemProperty -Path $steamRegistryPath -Name 'RunningAppID' -Value 0 -Type DWord",
            "        return",
            "    } catch { }",
            "    try {",
            "        New-ItemProperty -Path $steamRegistryPath -Name 'RunningAppID' -Value 0 -PropertyType DWord -Force | Out-Null",
            "    } catch { }",
            "}",
            "Wait-ForProcessExit -targetPid $waitPid -timeoutMs $waitForExitTimeoutMs",
            "Start-Sleep -Milliseconds $cleanupDelayMs",
            "if ((Get-RunningAppId) -eq $expectedAppId) {",
            "    Clear-RunningAppId",
            "    Start-Sleep -Milliseconds 400",
            "    if ((Get-RunningAppId) -eq $expectedAppId) {",
            "        Clear-RunningAppId",
            "    }",
            "}"
        });
    }

    private static bool TryStartBackgroundPowerShell(string script)
    {
        try
        {
            var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var powerShellPath = GetPowerShellExecutablePath();

            if (TryStartWithBreakawayFromJob(powerShellPath, encodedCommand))
            {
                return true;
            }

            return TryStartWithProcessStart(powerShellPath, encodedCommand);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryStartWithBreakawayFromJob(string powerShellPath, string encodedCommand)
    {
        try
        {
            var commandLine = BuildPowerShellCommandLine(powerShellPath, encodedCommand);

            var startupInfo = new STARTUPINFOW
            {
                cb = Marshal.SizeOf<STARTUPINFOW>()
            };

            const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
            const uint CREATE_NO_WINDOW = 0x08000000;

            var success = CreateProcessW(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CREATE_BREAKAWAY_FROM_JOB | CREATE_NO_WINDOW,
                IntPtr.Zero,
                null,
                ref startupInfo,
                out var processInfo);

            if (success)
            {
                CloseHandle(processInfo.hProcess);
                CloseHandle(processInfo.hThread);
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryStartWithProcessStart(string powerShellPath, string encodedCommand)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = powerShellPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(encodedCommand);

            return Process.Start(startInfo) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildPowerShellCommandLine(string powerShellPath, string encodedCommand)
    {
        var sb = new StringBuilder();
        sb.Append('"').Append(powerShellPath).Append('"');
        sb.Append(" -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ");
        sb.Append(encodedCommand);
        return sb.ToString();
    }

    private static string GetPowerShellExecutablePath()
    {
        var systemPowerShellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

        return File.Exists(systemPowerShellPath)
            ? systemPowerShellPath
            : "powershell.exe";
    }

    private static string ToPowerShellSingleQuotedLiteral(string value)
    {
        return $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }
}
