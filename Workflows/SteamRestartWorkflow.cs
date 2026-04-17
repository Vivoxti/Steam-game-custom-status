using System.Diagnostics;
using Microsoft.Win32;
using SteamGameCustomStatus.Steam;

namespace SteamGameCustomStatus.Workflows;

internal static class SteamRestartWorkflow
{
    private const string HelperModeArgument = "--steam-restart-helper";
    private const string NewNameArgument = "--new-name";
    private const string RelaunchRunGameIdArgument = "--relaunch-rungameid";
    private const string WaitForExitProcessIdArgument = "--wait-for-exit-pid";

    public static bool TryHandleHelperLaunch(string[] args)
    {
        if (!TryParseHelperArguments(args, out var newName, out var relaunchRunGameId, out var waitForExitProcessId))
        {
            return false;
        }

        RunHelper(newName!, relaunchRunGameId, waitForExitProcessId);
        return true;
    }

    public static RenameAndRestartResult RunAfterRename(string newName, bool isSteamLaunch)
    {
        var shortcutInfoResult = SteamShortcutRenamer.GetCurrentShortcutInfoForLaunch();
        if (!shortcutInfoResult.Success || shortcutInfoResult.ShortcutInfo is null)
        {
            return RenameAndRestartResult.Failure(shortcutInfoResult.Message);
        }

        var shortcutInfo = shortcutInfoResult.ShortcutInfo;
        var wasSteamRunning = IsSteamRunning();

        if (!wasSteamRunning)
        {
            var renameResult = SteamShortcutRenamer.RenameCurrentShortcut(newName);
            if (!renameResult.Success)
            {
                return RenameAndRestartResult.Failure(renameResult.Message);
            }

            return RenameAndRestartResult.Success(
                renameResult.Message,
                shouldExitApplication: false);
        }

        if (IsAnotherSteamGameRunning(shortcutInfo.AppId))
        {
            var renameResult = SteamShortcutRenamer.RenameCurrentShortcut(newName);
            if (!renameResult.Success)
            {
                return RenameAndRestartResult.Failure(renameResult.Message);
            }

            return RenameAndRestartResult.Warning(
                renameResult.Message +
                "\n\nSteam was not restarted now because another game is currently running in it. " +
                "Close that game and restart Steam later to see the new name.");
        }

        if (!TryStartHelperProcess(
                newName,
                relaunchRunGameId: isSteamLaunch ? shortcutInfo.RunGameId : null,
                waitForExitProcessId: isSteamLaunch ? Environment.ProcessId : null))
        {
            return RenameAndRestartResult.Failure(
                "Could not start the automatic process to apply the new name. Try again.");
        }

        return isSteamLaunch
            ? RenameAndRestartResult.Success(
                "Steam will close automatically, then the entry will be renamed, and then Steam will start again. " +
                "The app will relaunch through Steam with the new name.",
                shouldExitApplication: true)
            : RenameAndRestartResult.Success(
                "Steam will close automatically, then the entry will be renamed, and then Steam will start again. " +
                "The current app instance will keep running without restarting.",
                shouldExitApplication: false);
    }

    private static bool TryParseHelperArguments(string[] args, out string? newName, out ulong? relaunchRunGameId, out int? waitForExitProcessId)
    {
        newName = null;
        relaunchRunGameId = null;
        waitForExitProcessId = null;

        if (!args.Any(argument => string.Equals(argument, HelperModeArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        newName = TryGetStringArgument(args, NewNameArgument);
        relaunchRunGameId = TryGetUnsignedLongArgument(args, RelaunchRunGameIdArgument);
        waitForExitProcessId = TryGetIntArgument(args, WaitForExitProcessIdArgument);
        return !string.IsNullOrWhiteSpace(newName);
    }

    private static string? TryGetStringArgument(IReadOnlyList<string> args, string argumentName)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], argumentName, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static ulong? TryGetUnsignedLongArgument(IReadOnlyList<string> args, string argumentName)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (!string.Equals(args[index], argumentName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ulong.TryParse(args[index + 1], out var value))
            {
                return value;
            }

            return null;
        }

        return null;
    }

    private static int? TryGetIntArgument(IReadOnlyList<string> args, string argumentName)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (!string.Equals(args[index], argumentName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (int.TryParse(args[index + 1], out var value) && value > 0)
            {
                return value;
            }

            return null;
        }

        return null;
    }

    private static bool IsAnotherSteamGameRunning(uint currentShortcutAppId)
    {
        if (!IsSteamRunning())
        {
            return false;
        }

        var runningAppId = GetRunningSteamAppId();
        return runningAppId != 0 && runningAppId != currentShortcutAppId;
    }

    private static uint GetRunningSteamAppId()
    {
        try
        {
            using var steamKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var rawValue = steamKey?.GetValue("RunningAppID");
            return rawValue switch
            {
                int intValue when intValue >= 0 => unchecked((uint)intValue),
                long longValue when longValue >= 0 && longValue <= uint.MaxValue => (uint)longValue,
                string stringValue when uint.TryParse(stringValue, out var parsed) => parsed,
                _ => 0
            };
        }
        catch
        {
            return 0;
        }
    }

    private static bool TryStartHelperProcess(string newName, ulong? relaunchRunGameId, int? waitForExitProcessId)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = processPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            startInfo.ArgumentList.Add(HelperModeArgument);
            startInfo.ArgumentList.Add(NewNameArgument);
            startInfo.ArgumentList.Add(newName);

            if (relaunchRunGameId.HasValue)
            {
                startInfo.ArgumentList.Add(RelaunchRunGameIdArgument);
                startInfo.ArgumentList.Add(relaunchRunGameId.Value.ToString());
            }

            if (waitForExitProcessId.HasValue)
            {
                startInfo.ArgumentList.Add(WaitForExitProcessIdArgument);
                startInfo.ArgumentList.Add(waitForExitProcessId.Value.ToString());
            }

            return Process.Start(startInfo) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static void RunHelper(string newName, ulong? relaunchRunGameId, int? waitForExitProcessId)
    {
        if (waitForExitProcessId.HasValue)
        {
            WaitForProcessExit(waitForExitProcessId.Value, TimeSpan.FromSeconds(15));
        }

        var steamExePath = SteamShortcutRenamer.GetSteamExecutablePath();
        StopSteam();
        SteamShortcutRenamer.RenameCurrentShortcut(newName);
        StartSteam(steamExePath);
        WaitForSteamStartup(TimeSpan.FromSeconds(15));
        Thread.Sleep(3000);

        if (relaunchRunGameId.HasValue)
        {
            LaunchViaSteam(relaunchRunGameId.Value);
        }
    }

    private static void WaitForProcessExit(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch
        {
        }
    }

    private static void StopSteam()
    {
        CloseSteamMainProcesses();
        WaitForSteamShutdown(TimeSpan.FromSeconds(10));

        if (IsSteamRunning())
        {
            KillSteamMainProcesses();
            WaitForSteamShutdown(TimeSpan.FromSeconds(10));
        }
    }

    private static void CloseSteamMainProcesses()
    {
        foreach (var process in Process.GetProcessesByName("steam"))
        {
            try
            {
                if (process.HasExited)
                {
                    continue;
                }

                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    process.CloseMainWindow();
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void KillSteamMainProcesses()
    {
        foreach (var process in Process.GetProcessesByName("steam"))
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: false);
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void WaitForSteamShutdown(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsSteamRunning())
            {
                return;
            }

            Thread.Sleep(250);
        }
    }

    private static void WaitForSteamStartup(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (IsSteamRunning())
            {
                return;
            }

            Thread.Sleep(250);
        }
    }

    private static bool IsSteamRunning()
    {
        foreach (var process in Process.GetProcessesByName("steam"))
        {
            try
            {
                if (!process.HasExited)
                {
                    return true;
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    private static void StartSteam(string? steamExePath)
    {
        if (!string.IsNullOrWhiteSpace(steamExePath) && TryStartWithShell(steamExePath))
        {
            return;
        }

        TryStartWithShell("steam://open/main");
    }

    private static void LaunchViaSteam(ulong runGameId)
    {
        var target = $"steam://rungameid/{runGameId}";

        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (TryStartWithShell(target))
            {
                return;
            }

            Thread.Sleep(1500);
        }
    }

    private static bool TryStartWithShell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });

            return true;
        }
        catch
        {
            return false;
        }
    }

    internal sealed record RenameAndRestartResult(bool IsSuccess, bool IsWarning, bool ShouldExitApplication, string Message)
    {
        public static RenameAndRestartResult Success(string message, bool shouldExitApplication) =>
            new(true, false, shouldExitApplication, message);

        public static RenameAndRestartResult Warning(string message) =>
            new(true, true, false, message);

        public static RenameAndRestartResult Failure(string message) =>
            new(false, true, false, message);
    }
}




