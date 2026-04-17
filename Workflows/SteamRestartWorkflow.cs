using System.Diagnostics;
using SteamGameCustomStatus.Infrastructure;
using SteamGameCustomStatus.Steam;

namespace SteamGameCustomStatus.Workflows;

internal static class SteamRestartWorkflow
{
    public static RenameAndRestartResult RunAfterRename(string newName, bool isSteamLaunch)
    {
        var shortcutInfoResult = SteamShortcutRenamer.GetCurrentShortcutInfoForLaunch();
        if (!shortcutInfoResult.Success || shortcutInfoResult.ShortcutInfo is null)
        {
            return RenameAndRestartResult.Failure(shortcutInfoResult.Message);
        }

        var shortcutInfo = shortcutInfoResult.ShortcutInfo;
        var wasSteamRunning = IsSteamRunning();
        var renameResult = SteamShortcutRenamer.RenameCurrentShortcut(newName);
        if (!renameResult.Success)
        {
            return RenameAndRestartResult.Failure(renameResult.Message);
        }

        if (!wasSteamRunning)
        {
            return RenameAndRestartResult.Success(
                renameResult.Message,
                shouldExitApplication: false);
        }

        if (IsAnotherSteamGameRunning(shortcutInfo.AppId))
        {
            return RenameAndRestartResult.Warning(
                renameResult.Message +
                "\n\nSteam was not restarted now because another game is currently running in it. " +
                "Close that game and restart Steam later to see the new name.");
        }

        if (!SteamLifecycleGuard.TryStartSteamRestartHelper(
                SteamShortcutRenamer.GetSteamExecutablePath(),
                relaunchRunGameId: isSteamLaunch ? shortcutInfo.RunGameId : null,
                waitForExitProcessId: isSteamLaunch ? Environment.ProcessId : null))
        {
            return RenameAndRestartResult.Warning(
                renameResult.Message +
                "\n\nSteam could not be restarted automatically this time. Restart Steam manually to apply the new name.");
        }

        return isSteamLaunch
            ? RenameAndRestartResult.Success(
                "The new name was saved. Steam will close automatically and then start again. " +
                "The app will relaunch through Steam with the new name.",
                shouldExitApplication: true)
            : RenameAndRestartResult.Success(
                "The new name was saved. Steam will close automatically and then start again. " +
                "The current app instance will keep running without restarting.",
                shouldExitApplication: false);
    }

    private static bool IsAnotherSteamGameRunning(uint currentShortcutAppId)
    {
        if (!IsSteamRunning())
        {
            return false;
        }

        var runningAppId = SteamShortcutRenamer.GetRunningSteamAppId();
        return runningAppId != 0 && runningAppId != currentShortcutAppId;
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




