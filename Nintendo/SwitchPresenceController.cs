using System.Diagnostics;
using System.IO;
using System.Net.Http;
using SteamGameCustomStatus.Infrastructure;
using SteamGameCustomStatus.Steam;
using SteamGameCustomStatus.Workflows;
using Threading = System.Windows.Threading;

namespace SteamGameCustomStatus.Nintendo;

/// <summary>
/// Mirrors the watched Nintendo account's current Switch title into Steam: each title gets its own non-Steam
/// shortcut, and a headless holder process launched through that shortcut keeps it "running" while the title is played.
/// All members run on the UI dispatcher.
/// </summary>
internal sealed class SwitchPresenceController : IDisposable
{
    private static readonly TimeSpan StalePresenceTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SteamRestartCooldown = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RelaunchCooldown = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HolderStartVerificationDelay = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan HolderStopWait = TimeSpan.FromSeconds(2);
    private static readonly HttpClient IconHttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly Threading.Dispatcher _dispatcher;
    private readonly Func<bool> _isSteamLaunch;
    private readonly Func<uint, bool> _isOwnSteamAppId;
    private readonly Action _exitForSteamRelaunch;
    private readonly Action<string> _notify;
    private readonly Threading.DispatcherTimer _staleTimer;
    private readonly Dictionary<string, DateTime> _steamRestartAttemptsUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _launchAttemptsUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingNotified = new(StringComparer.OrdinalIgnoreCase);

    private NintendoSettings _settings = new();
    private NintendoPresenceMonitor? _monitor;
    private string? _activeTitle;
    private DateTime _lastPresenceUtc = DateTime.MinValue;
    private bool _isActivating;
    private bool _isDisposed;

    public SwitchPresenceController(
        Threading.Dispatcher dispatcher,
        Func<bool> isSteamLaunch,
        Func<uint, bool> isOwnSteamAppId,
        Action exitForSteamRelaunch,
        Action<string> notify)
    {
        _dispatcher = dispatcher;
        _isSteamLaunch = isSteamLaunch;
        _isOwnSteamAppId = isOwnSteamAppId;
        _exitForSteamRelaunch = exitForSteamRelaunch;
        _notify = notify;
        _staleTimer = new Threading.DispatcherTimer(Threading.DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMinutes(1)
        };
        _staleTimer.Tick += (_, _) => CheckForStalePresence();
        Status = new SwitchPresenceStatus(SwitchPresenceStatusKind.Disabled, "Off");
    }

    public event EventHandler? StatusChanged;

    public SwitchPresenceStatus Status { get; private set; }

    /// <summary>
    /// A copy of the current settings; pass a modified copy to <see cref="ApplySettings"/>.
    /// </summary>
    public NintendoSettings Settings => new()
    {
        Enabled = _settings.Enabled,
        WatchedNsaId = _settings.WatchedNsaId,
        WatchedName = _settings.WatchedName,
        PollIntervalSeconds = _settings.PollIntervalSeconds
    };

    public void Start()
    {
        _settings = NintendoSettings.Load();
        RestartMonitor();
    }

    public void ApplySettings(NintendoSettings settings)
    {
        _settings = settings;
        try
        {
            _settings.Save();
        }
        catch (Exception exception)
        {
            SetStatus(SwitchPresenceStatusKind.Error, $"Settings could not be saved: {exception.Message}");
            return;
        }

        RestartMonitor();
    }

    /// <summary>
    /// Restarts polling, e.g. after the bot account signed in again.
    /// </summary>
    public void Reconnect() => RestartMonitor();

    public void StopHolders()
    {
        StopAllHolders(waitForExit: false);
        _activeTitle = null;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _staleTimer.Stop();
        _monitor?.Dispose();
        _monitor = null;
    }

    private void RestartMonitor()
    {
        _monitor?.Dispose();
        _monitor = null;
        _staleTimer.Stop();
        _pendingNotified.Clear();

        if (!_settings.Enabled)
        {
            StopHolders();
            SetStatus(SwitchPresenceStatusKind.Disabled, "Off");
            return;
        }

        if (!_settings.IsConfigured)
        {
            StopHolders();
            SetStatus(SwitchPresenceStatusKind.NotConfigured, "Not set up");
            return;
        }

        if (!NxapiBridge.IsEmbedded)
        {
            SetStatus(SwitchPresenceStatusKind.Error, "This build does not include the Nintendo bridge.");
            return;
        }

        _lastPresenceUtc = DateTime.UtcNow;
        SetStatus(SwitchPresenceStatusKind.Connecting, "Connecting to Nintendo…");
        _monitor = new NintendoPresenceMonitor(
            _settings.WatchedNsaId!,
            _settings.EffectivePollIntervalSeconds,
            presence => _dispatcher.BeginInvoke(() => OnPresence(presence)),
            (code, message, isFatal) => _dispatcher.BeginInvoke(() => OnError(code, message, isFatal)));
        _monitor.Start();
        _staleTimer.Start();
    }

    private async void OnPresence(NintendoPresence presence)
    {
        if (_isDisposed || _monitor is null)
        {
            return;
        }

        _lastPresenceUtc = DateTime.UtcNow;
        var title = presence.PlayingTitle;
        if (title is null)
        {
            if (_activeTitle is not null)
            {
                StopAllHolders(waitForExit: false);
                _activeTitle = null;
            }

            var isOffline = presence.State is "OFFLINE" or null;
            SetStatus(
                isOffline ? SwitchPresenceStatusKind.Offline : SwitchPresenceStatusKind.Online,
                isOffline ? "Offline" : "Online · not playing");
            return;
        }

        await ActivateAsync(title, presence.Game?.ImageUri);
    }

    private void OnError(string errorCode, string? message, bool isFatal)
    {
        if (_isDisposed || _monitor is null)
        {
            return;
        }

        if (isFatal)
        {
            StopHolders();
            SetStatus(SwitchPresenceStatusKind.Error, DescribeError(errorCode, message));
            return;
        }

        if (Status.Kind is SwitchPresenceStatusKind.Connecting or SwitchPresenceStatusKind.Error)
        {
            SetStatus(SwitchPresenceStatusKind.Error, DescribeError(errorCode, message) + " Retrying…");
        }
    }

    private void CheckForStalePresence()
    {
        if (_activeTitle is null || DateTime.UtcNow - _lastPresenceUtc < StalePresenceTimeout)
        {
            return;
        }

        StopHolders();
        SetStatus(SwitchPresenceStatusKind.Error, "No updates from Nintendo for a while. Retrying…");
    }

    private async Task ActivateAsync(string title, string? imageUri)
    {
        if (_isActivating)
        {
            return;
        }

        _isActivating = true;
        try
        {
            await ActivateCoreAsync(title, imageUri);
        }
        finally
        {
            _isActivating = false;
        }
    }

    private async Task ActivateCoreAsync(string title, string? imageUri)
    {
        var key = SteamShortcutRenamer.GetSwitchGameKey(title);
        var shortcutStatus = SteamShortcutRenamer.GetSwitchGameShortcutStatus(title);

        if (shortcutStatus is { IsUpToDate: true, ShortcutInfo: { } shortcutInfo })
        {
            if (!SwitchPresenceHolder.IsRunning(key) && CanLaunch(title))
            {
                StopAllHolders(waitForExit: true, exceptKey: key);
                LaunchHolder(title, shortcutInfo);
            }

            _activeTitle = title;
            SetStatus(SwitchPresenceStatusKind.Playing, $"Playing {title}", title);
            return;
        }

        await AddShortcutAndRestartSteamAsync(title, key, imageUri);
    }

    private async Task AddShortcutAndRestartSteamAsync(string title, string key, string? imageUri)
    {
        var isSteamRunning = SteamRestartWorkflow.IsSteamRunning();
        if (isSteamRunning && !CanRestartSteam(title))
        {
            if (_activeTitle is not null && !string.Equals(_activeTitle, title, StringComparison.OrdinalIgnoreCase))
            {
                // the previous title is no longer being played
                StopHolders();
            }

            return;
        }

        var iconPath = await TryGetIconAsync(key, imageUri);
        if (_isDisposed)
        {
            return;
        }

        StopAllHolders(waitForExit: true);
        var writeResult = SteamShortcutRenamer.EnsureSwitchGameShortcut(title, iconPath);
        if (!writeResult.Success || writeResult.ShortcutInfo is null)
        {
            _activeTitle = null;
            SetStatus(SwitchPresenceStatusKind.Error, writeResult.Message, title);
            return;
        }

        _activeTitle = title;

        if (!isSteamRunning)
        {
            // Steam reads the new shortcut when this launch starts it.
            LaunchHolder(title, writeResult.ShortcutInfo);
            SetStatus(SwitchPresenceStatusKind.Playing, $"Playing {title}", title);
            return;
        }

        RestartSteamFor(title, writeResult.ShortcutInfo);
    }

    private bool CanRestartSteam(string title)
    {
        if (IsBlockingSteamGameRunning())
        {
            SetStatus(
                SwitchPresenceStatusKind.PendingSteamRestart,
                $"{title} will be added to Steam after the running Steam game closes.",
                title);
            if (_pendingNotified.Add(title))
            {
                _notify($"{title} will appear in Steam once the running Steam game closes.");
            }

            return false;
        }

        if (_steamRestartAttemptsUtc.TryGetValue(title, out var lastAttemptUtc) &&
            DateTime.UtcNow - lastAttemptUtc < SteamRestartCooldown)
        {
            SetStatus(
                SwitchPresenceStatusKind.PendingSteamRestart,
                $"{title} was added, but Steam has not picked it up yet. Restart Steam if it does not show up.",
                title);
            return false;
        }

        return true;
    }

    private void RestartSteamFor(string title, SteamShortcutRenamer.ShortcutInfo gameShortcut)
    {
        _steamRestartAttemptsUtc[title] = DateTime.UtcNow;

        var isSteamLaunch = _isSteamLaunch();
        var ownShortcut = isSteamLaunch ? SteamShortcutRenamer.GetCurrentShortcutInfoForLaunch().ShortcutInfo : null;

        // A Steam-launched tray app is relaunched through its own shortcut; the new instance then starts the holder.
        var relaunchRunGameId = ownShortcut?.RunGameId ?? gameShortcut.RunGameId;
        var waitForExitProcessId = ownShortcut is not null ? Environment.ProcessId : (int?)null;

        if (!SteamLifecycleGuard.TryStartSteamRestartHelper(
                SteamShortcutRenamer.GetSteamExecutablePath(),
                relaunchRunGameId,
                waitForExitProcessId))
        {
            SetStatus(
                SwitchPresenceStatusKind.PendingSteamRestart,
                $"{title} was added. Restart Steam to show it.",
                title);
            return;
        }

        if (ownShortcut is null)
        {
            _launchAttemptsUtc[title] = DateTime.UtcNow;
        }

        SetStatus(SwitchPresenceStatusKind.Playing, $"Playing {title} (restarting Steam…)", title);
        _notify($"Adding {title} to Steam. Steam will restart.");

        if (ownShortcut is not null)
        {
            _exitForSteamRelaunch();
        }
    }

    private bool IsBlockingSteamGameRunning()
    {
        var runningAppId = SteamShortcutRenamer.GetRunningSteamAppId();
        if (runningAppId == 0 || _isOwnSteamAppId(runningAppId))
        {
            return false;
        }

        foreach (var shortcut in SteamShortcutRenamer.GetSwitchGameShortcuts())
        {
            if (shortcut.AppId == runningAppId ||
                SwitchPresenceHolder.IsRunning(SteamShortcutRenamer.GetSwitchGameKey(shortcut.AppName)))
            {
                return false;
            }
        }

        return true;
    }

    private bool CanLaunch(string title)
    {
        return !_launchAttemptsUtc.TryGetValue(title, out var lastLaunchUtc) ||
               DateTime.UtcNow - lastLaunchUtc >= RelaunchCooldown ||
               !string.Equals(_activeTitle, title, StringComparison.OrdinalIgnoreCase);
    }

    private void LaunchHolder(string title, SteamShortcutRenamer.ShortcutInfo shortcutInfo)
    {
        _launchAttemptsUtc[title] = DateTime.UtcNow;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"steam://rungameid/{shortcutInfo.RunGameId}",
                UseShellExecute = true
            });
        }
        catch
        {
            SetStatus(SwitchPresenceStatusKind.Error, $"Steam could not launch {title}.", title);
            return;
        }

        VerifyHolderStartedLater(title, shortcutInfo);
    }

    /// <summary>
    /// If Steam ignores the launch, it most likely has not loaded the shortcut yet; a restart makes it do so.
    /// </summary>
    private async void VerifyHolderStartedLater(string title, SteamShortcutRenamer.ShortcutInfo shortcutInfo)
    {
        await Task.Delay(HolderStartVerificationDelay);

        var key = SteamShortcutRenamer.GetSwitchGameKey(title);
        if (_isDisposed ||
            _monitor is null ||
            !string.Equals(_activeTitle, title, StringComparison.OrdinalIgnoreCase) ||
            SwitchPresenceHolder.IsRunning(key) ||
            !SteamRestartWorkflow.IsSteamRunning() ||
            !CanRestartSteam(title))
        {
            return;
        }

        RestartSteamFor(title, shortcutInfo);
    }

    private void StopAllHolders(bool waitForExit, string? exceptKey = null)
    {
        var keys = SteamShortcutRenamer.GetSwitchGameShortcuts()
            .Select(shortcut => SteamShortcutRenamer.GetSwitchGameKey(shortcut.AppName))
            .ToHashSet(StringComparer.Ordinal);
        if (_activeTitle is not null)
        {
            keys.Add(SteamShortcutRenamer.GetSwitchGameKey(_activeTitle));
        }

        var stoppedKeys = new List<string>();
        foreach (var key in keys)
        {
            if (key == exceptKey || !SwitchPresenceHolder.IsRunning(key))
            {
                continue;
            }

            SwitchPresenceHolder.Stop(key);
            stoppedKeys.Add(key);
        }

        if (!waitForExit)
        {
            return;
        }

        foreach (var key in stoppedKeys)
        {
            SwitchPresenceHolder.WaitForExit(key, HolderStopWait);
        }
    }

    /// <summary>
    /// Downloads the title's Nintendo artwork for the Steam shortcut icon. Cosmetic only: any failure means no icon.
    /// </summary>
    private static async Task<string?> TryGetIconAsync(string key, string? imageUri)
    {
        try
        {
            Directory.CreateDirectory(AppDataLocation.IconsDirectory);
            var existingPath = Directory.EnumerateFiles(AppDataLocation.IconsDirectory, key + ".*").FirstOrDefault();
            if (existingPath is not null)
            {
                return existingPath;
            }

            if (string.IsNullOrWhiteSpace(imageUri) || !Uri.TryCreate(imageUri, UriKind.Absolute, out var uri))
            {
                return null;
            }

            var extension = Path.GetExtension(uri.AbsolutePath);
            if (string.IsNullOrWhiteSpace(extension) || extension.Length > 5)
            {
                extension = ".jpg";
            }

            var targetPath = Path.Combine(AppDataLocation.IconsDirectory, key + extension);
            var bytes = await IconHttpClient.GetByteArrayAsync(uri);
            await File.WriteAllBytesAsync(targetPath, bytes);
            return targetPath;
        }
        catch
        {
            return null;
        }
    }

    private void SetStatus(SwitchPresenceStatusKind kind, string text, string? title = null)
    {
        var status = new SwitchPresenceStatus(kind, text, title);
        if (status == Status)
        {
            return;
        }

        Status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string DescribeError(string errorCode, string? message)
    {
        return errorCode switch
        {
            "not_logged_in" => "The bot account is signed out. Sign in again in Nintendo Switch settings.",
            "auth_failed" => "Nintendo sign-in failed. Sign in with the bot account again.",
            "friend_not_found" => "The watched account is not on the bot account's friend list.",
            "bridge_missing" => "This build does not include the Nintendo bridge.",
            "network" => "Network error while contacting Nintendo.",
            _ => string.IsNullOrWhiteSpace(message) ? "Nintendo bridge error." : message
        };
    }
}

internal enum SwitchPresenceStatusKind
{
    Disabled,
    NotConfigured,
    Connecting,
    Online,
    Offline,
    Playing,
    PendingSteamRestart,
    Error
}

internal sealed record SwitchPresenceStatus(SwitchPresenceStatusKind Kind, string Text, string? Title = null);
