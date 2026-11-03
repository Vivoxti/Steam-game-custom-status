using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace SteamGameCustomStatus.Steam;

/// <summary>
/// Per-game non-Steam shortcuts used to mirror Nintendo Switch presence. Each one points at this executable
/// with <see cref="SwitchGameLaunchMarker"/> in its launch options, so it never matches the app's own shortcut.
/// </summary>
internal static partial class SteamShortcutRenamer
{
    internal const string SwitchGameLaunchMarker = "--switch-presence-hold";
    private const string SwitchGameTag = "Nintendo Switch";

    internal static string GetSwitchGameKey(string gameName)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(gameName.Trim().ToUpperInvariant()));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    internal static string BuildSwitchGameLaunchOptions(string key) => $"{SwitchGameLaunchMarker} {key}";

    /// <summary>
    /// Finds the shortcut for a Switch title and reports whether Steam can already launch it as-is.
    /// </summary>
    internal static SwitchGameShortcutStatus GetSwitchGameShortcutStatus(string gameName)
    {
        var processPath = Environment.ProcessPath;
        var shortcutFile = TryGetSwitchGameShortcutFile();
        if (string.IsNullOrWhiteSpace(processPath) || shortcutFile is null || !File.Exists(shortcutFile))
        {
            return new SwitchGameShortcutStatus(null, IsUpToDate: false);
        }

        if (!TryLoadShortcutFile(shortcutFile, out var file, out _))
        {
            return new SwitchGameShortcutStatus(null, IsUpToDate: false);
        }

        var entry = file.FindByLaunchOptions(BuildSwitchGameLaunchOptions(GetSwitchGameKey(gameName)));
        if (entry is null)
        {
            return new SwitchGameShortcutStatus(null, IsUpToDate: false);
        }

        var isUpToDate = entry.MatchesExecutablePath(SteamShortcutsFile.NormalizePath(processPath));
        return new SwitchGameShortcutStatus(entry.ToShortcutInfo(), isUpToDate);
    }

    /// <summary>
    /// Adds (or repoints) the shortcut for a Switch title. Steam only sees the change after it restarts.
    /// </summary>
    internal static SwitchGameShortcutWriteResult EnsureSwitchGameShortcut(string gameName, string? iconPath)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return SwitchGameShortcutWriteResult.Failure("Could not determine the path to the current executable.");
        }

        var shortcutFile = TryGetSwitchGameShortcutFile();
        if (shortcutFile is null)
        {
            return SwitchGameShortcutWriteResult.Failure("Could not find the Steam user folder. Sign in to Steam at least once.");
        }

        SteamShortcutsFile file;
        if (File.Exists(shortcutFile))
        {
            if (!TryLoadShortcutFile(shortcutFile, out file, out var loadErrorMessage))
            {
                return SwitchGameShortcutWriteResult.Failure(loadErrorMessage);
            }
        }
        else
        {
            file = SteamShortcutsFile.CreateEmpty();
        }

        var appName = gameName.Trim();
        var launchOptions = BuildSwitchGameLaunchOptions(GetSwitchGameKey(appName));
        var quotedExecutable = Quote(processPath);
        var quotedStartDirectory = Quote(Path.GetDirectoryName(processPath) ?? string.Empty);

        var entry = file.FindByLaunchOptions(launchOptions);
        if (entry is not null && entry.MatchesExecutablePath(SteamShortcutsFile.NormalizePath(processPath)))
        {
            return SwitchGameShortcutWriteResult.Unchanged(entry.ToShortcutInfo());
        }

        var newAppId = ComputeNonSteamShortcutAppId(quotedExecutable, appName);
        if (file.ContainsAppId(newAppId, except: entry))
        {
            return SwitchGameShortcutWriteResult.Failure(
                $"Another Steam shortcut already uses the name \"{appName}\" for this executable.");
        }

        if (entry is null)
        {
            entry = file.AddShortcut(appName, quotedExecutable, quotedStartDirectory, iconPath ?? string.Empty, launchOptions, SwitchGameTag);
        }
        else
        {
            entry.SetExecutable(quotedExecutable, quotedStartDirectory);
        }

        entry.RefreshAppId();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(shortcutFile)!);
            if (File.Exists(shortcutFile))
            {
                CreateBackup(shortcutFile);
            }

            file.Save(shortcutFile);
        }
        catch
        {
            return SwitchGameShortcutWriteResult.Failure(BuildShortcutFileWriteErrorMessage(shortcutFile));
        }

        return SwitchGameShortcutWriteResult.Written(entry.ToShortcutInfo());
    }

    internal static IReadOnlyList<ShortcutInfo> GetSwitchGameShortcuts()
    {
        var shortcutFile = TryGetSwitchGameShortcutFile();
        if (shortcutFile is null || !File.Exists(shortcutFile) || !TryLoadShortcutFile(shortcutFile, out var file, out _))
        {
            return [];
        }

        return file.GetSwitchGameEntries().Select(entry => entry.ToShortcutInfo()).ToArray();
    }

    /// <summary>
    /// The shortcuts.vdf of the Steam user that is signed in now, falling back to the file holding this app's own
    /// shortcut, then to the most recently written one.
    /// </summary>
    private static string? TryGetSwitchGameShortcutFile()
    {
        var steamPath = TryGetSteamPath();
        if (steamPath is null)
        {
            return null;
        }

        var activeUserId = TryGetActiveSteamUserId();
        if (activeUserId != 0)
        {
            var activeUserDirectory = Path.Combine(steamPath, "userdata", activeUserId.ToString());
            if (Directory.Exists(activeUserDirectory))
            {
                return Path.Combine(activeUserDirectory, "config", "shortcuts.vdf");
            }
        }

        var shortcutFiles = GetShortcutFiles(steamPath).ToArray();
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            foreach (var shortcutFile in shortcutFiles)
            {
                if (TryLoadShortcutFile(shortcutFile, out var file, out _) && file.FindByExecutablePath(processPath) is not null)
                {
                    return shortcutFile;
                }
            }
        }

        return shortcutFiles
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static uint TryGetActiveSteamUserId()
    {
        try
        {
            using var activeProcessKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            return activeProcessKey?.GetValue("ActiveUser") switch
            {
                int intValue => unchecked((uint)intValue),
                long longValue and >= 0 and <= uint.MaxValue => (uint)longValue,
                _ => 0
            };
        }
        catch
        {
            return 0;
        }
    }

    private static string Quote(string value) => $"\"{value}\"";

    private sealed partial class SteamShortcutsFile
    {
        public static SteamShortcutsFile CreateEmpty() => new(new VdfObject());

        public static string NormalizePath(string path) => NormalizeExecutablePath(path);

        public ShortcutEntry? FindByLaunchOptions(string launchOptions)
        {
            return GetEntries().FirstOrDefault(entry =>
                string.Equals(entry.LaunchOptions?.Trim(), launchOptions, StringComparison.Ordinal));
        }

        public IEnumerable<ShortcutEntry> GetSwitchGameEntries()
        {
            return GetEntries().Where(entry => entry.IsSwitchGameShortcut);
        }

        public bool ContainsAppId(uint appId, ShortcutEntry? except)
        {
            return GetEntries().Any(entry =>
                entry.ToShortcutInfo().AppId == appId &&
                (except is null || !string.Equals(entry.LaunchOptions, except.LaunchOptions, StringComparison.Ordinal)));
        }

        public ShortcutEntry AddShortcut(
            string appName,
            string quotedExecutable,
            string quotedStartDirectory,
            string iconPath,
            string launchOptions,
            string tag)
        {
            var nextIndex = 0;
            foreach (var property in _root.Properties)
            {
                if (int.TryParse(property.Name, out var index) && index >= nextIndex)
                {
                    nextIndex = index + 1;
                }
            }

            var tags = new VdfObject();
            tags.Properties.Add(new VdfProperty(StringType, "0", tag));

            var entryObject = new VdfObject();
            entryObject.Properties.AddRange(
            [
                new VdfProperty(Int32Type, "appid", 0),
                new VdfProperty(StringType, "AppName", appName),
                new VdfProperty(StringType, "Exe", quotedExecutable),
                new VdfProperty(StringType, "StartDir", quotedStartDirectory),
                new VdfProperty(StringType, "icon", iconPath),
                new VdfProperty(StringType, "ShortcutPath", string.Empty),
                new VdfProperty(StringType, "LaunchOptions", launchOptions),
                new VdfProperty(Int32Type, "IsHidden", 0),
                new VdfProperty(Int32Type, "AllowDesktopConfig", 1),
                new VdfProperty(Int32Type, "AllowOverlay", 1),
                new VdfProperty(Int32Type, "OpenVR", 0),
                new VdfProperty(Int32Type, "Devkit", 0),
                new VdfProperty(StringType, "DevkitGameID", string.Empty),
                new VdfProperty(Int32Type, "DevkitOverrideAppID", 0),
                new VdfProperty(Int32Type, "LastPlayTime", 0),
                new VdfProperty(StringType, "FlatpakAppID", string.Empty),
                new VdfProperty(StringType, "sortas", string.Empty),
                new VdfProperty(ObjectType, "tags", tags)
            ]);

            _root.Properties.Add(new VdfProperty(ObjectType, nextIndex.ToString(), entryObject));
            return new ShortcutEntry(entryObject);
        }

        private IEnumerable<ShortcutEntry> GetEntries()
        {
            foreach (var property in _root.Properties)
            {
                if (property.Type == ObjectType && property.Value is VdfObject entryObject)
                {
                    yield return new ShortcutEntry(entryObject);
                }
            }
        }
    }
}

internal sealed record SwitchGameShortcutStatus(SteamShortcutRenamer.ShortcutInfo? ShortcutInfo, bool IsUpToDate);

internal sealed record SwitchGameShortcutWriteResult(
    bool Success,
    bool FileChanged,
    SteamShortcutRenamer.ShortcutInfo? ShortcutInfo,
    string Message)
{
    public static SwitchGameShortcutWriteResult Written(SteamShortcutRenamer.ShortcutInfo info) => new(true, true, info, string.Empty);

    public static SwitchGameShortcutWriteResult Unchanged(SteamShortcutRenamer.ShortcutInfo info) => new(true, false, info, string.Empty);

    public static SwitchGameShortcutWriteResult Failure(string message) => new(false, false, null, message);
}
