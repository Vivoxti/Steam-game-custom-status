using SteamGameCustomStatus.Infrastructure;
using SteamGameCustomStatus.Steam;

namespace SteamGameCustomStatus.Nintendo;

/// <summary>
/// Headless mode Steam launches from a per-game Switch shortcut. The process only exists so Steam reports the
/// shortcut as running; it exits when the tray app signals it or when the tray app is gone.
/// </summary>
internal static class SwitchPresenceHolder
{
    private static readonly TimeSpan PrimaryCheckInterval = TimeSpan.FromSeconds(2);
    private const int MissingPrimaryChecksBeforeExit = 3;

    public static bool TryGetHoldKey(IReadOnlyList<string> arguments, out string key)
    {
        for (var i = 0; i < arguments.Count - 1; i++)
        {
            if (string.Equals(arguments[i], SteamShortcutRenamer.SwitchGameLaunchMarker, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(arguments[i + 1]))
            {
                key = arguments[i + 1].Trim();
                return true;
            }
        }

        key = string.Empty;
        return false;
    }

    public static void Run(string key)
    {
        using var stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, GetStopEventName(key), out var createdNew);
        if (!createdNew)
        {
            // another holder already represents this title
            return;
        }

        var missingPrimaryChecks = 0;
        while (!stopEvent.WaitOne(PrimaryCheckInterval))
        {
            if (Mutex.TryOpenExisting(SingleInstanceCoordinator.PrimaryMutexName, out var primaryMutex))
            {
                primaryMutex.Dispose();
                missingPrimaryChecks = 0;
                continue;
            }

            if (++missingPrimaryChecks >= MissingPrimaryChecksBeforeExit)
            {
                return;
            }
        }
    }

    public static bool IsRunning(string key)
    {
        if (!EventWaitHandle.TryOpenExisting(GetStopEventName(key), out var stopEvent))
        {
            return false;
        }

        stopEvent.Dispose();
        return true;
    }

    public static void Stop(string key)
    {
        if (!EventWaitHandle.TryOpenExisting(GetStopEventName(key), out var stopEvent))
        {
            return;
        }

        using (stopEvent)
        {
            stopEvent.Set();
        }
    }

    public static void WaitForExit(string key, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (IsRunning(key) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(100);
        }
    }

    private static string GetStopEventName(string key)
    {
        return $@"Local\SteamGameCustomStatus_{SingleInstanceCoordinator.GetCurrentUserScopeId()}_Hold_{key}";
    }
}
