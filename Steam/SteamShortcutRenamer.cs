using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace SteamGameCustomStatus.Steam;

internal static class SteamShortcutRenamer
{
    public static ShortcutRegistrationStatus GetCurrentShortcutRegistrationStatus()
    {
        var shortcutInfoResult = FindCurrentShortcutInfo();
        if (shortcutInfoResult.Success && shortcutInfoResult.ShortcutInfo is not null)
        {
            var currentName = string.IsNullOrWhiteSpace(shortcutInfoResult.ShortcutInfo.AppName)
                ? null
                : shortcutInfoResult.ShortcutInfo.AppName;

            var description = currentName is null
                ? "The current executable was found among Steam non-Steam games."
                : $"Name: {currentName}";

            return ShortcutRegistrationStatus.Registered(description, currentName);
        }

        return ShortcutRegistrationStatus.NotRegistered(
            "The current executable was not found among Steam non-Steam games.",
            "Tip: add the published executable of this app to Steam as a non-Steam game, not a Debug build or a copy from another folder.",
            shortcutInfoResult.Message);
    }

    public static RenameLookupResult FindCurrentShortcut()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return RenameLookupResult.Failure("Could not determine the path to the current executable.");
        }

        var steamPath = TryGetSteamPath();
        if (steamPath is null)
        {
            return RenameLookupResult.Failure("Could not find the Steam folder.");
        }

        var shortcutFiles = GetShortcutFiles(steamPath).ToArray();
        if (shortcutFiles.Length == 0)
        {
            return RenameLookupResult.Failure("No shortcuts.vdf files were found. First add this executable to Steam as a non-Steam game.");
        }

        var loadedAnyShortcutFile = false;
        string? shortcutFileLoadError = null;

        foreach (var shortcutFile in shortcutFiles)
        {
            if (!TryLoadShortcutFile(shortcutFile, out var file, out var loadErrorMessage))
            {
                shortcutFileLoadError ??= loadErrorMessage;
                continue;
            }

            loadedAnyShortcutFile = true;
            var shortcut = file.FindByExecutablePath(processPath);
            if (shortcut is not null)
            {
                return RenameLookupResult.Found(shortcut.AppName);
            }
        }

        if (!loadedAnyShortcutFile)
        {
            return RenameLookupResult.Failure(shortcutFileLoadError ?? "Steam shortcut files could not be read.");
        }

        return RenameLookupResult.Failure(
            $"No non-Steam shortcut was found for this executable: {processPath}\n\n" +
            "Make sure Steam contains the currently published executable, not a different build or path.");
    }

    public static RenameResult RenameCurrentShortcut(string newName)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return RenameResult.Failure("Could not determine the path to the current executable.");
        }

        var steamPath = TryGetSteamPath();
        if (steamPath is null)
        {
            return RenameResult.Failure("Could not find the Steam folder.");
        }

        var shortcutFiles = GetShortcutFiles(steamPath).ToArray();
        if (shortcutFiles.Length == 0)
        {
            return RenameResult.Failure("No shortcuts.vdf files were found.");
        }

        var updatedEntries = 0;
        var loadedAnyShortcutFile = false;
        string? shortcutFileLoadError = null;

        foreach (var shortcutFile in shortcutFiles)
        {
            if (!TryLoadShortcutFile(shortcutFile, out var file, out var loadErrorMessage))
            {
                shortcutFileLoadError ??= loadErrorMessage;
                continue;
            }

            loadedAnyShortcutFile = true;
            if (!file.TryRenameByExecutablePath(processPath, newName, out var renamedInFile))
            {
                continue;
            }

            if (renamedInFile == 0)
            {
                continue;
            }

            try
            {
                CreateBackup(shortcutFile);
                file.Save(shortcutFile);
            }
            catch
            {
                return RenameResult.Failure(BuildShortcutFileWriteErrorMessage(shortcutFile));
            }

            updatedEntries += renamedInFile;
        }

        if (updatedEntries == 0)
        {
            if (!loadedAnyShortcutFile)
            {
                return RenameResult.Failure(shortcutFileLoadError ?? "Steam shortcut files could not be read.");
            }

            return RenameResult.Failure(
                $"No non-Steam shortcut was found for this executable: {processPath}\n\n" +
                "Make sure Steam contains the currently published executable.");
        }

        var message = "Steam shortcut name updated.";

        return RenameResult.Successful(message);
    }

    internal static ShortcutInfoResult GetCurrentShortcutInfoForLaunch()
    {
        return FindCurrentShortcutInfo();
    }

    internal static string? GetSteamExecutablePath()
    {
        return TryGetSteamExePath();
    }

    internal static bool IsCurrentShortcutActiveInSteam()
    {
        var shortcutInfoResult = FindCurrentShortcutInfo();
        if (!shortcutInfoResult.Success || shortcutInfoResult.ShortcutInfo is null)
        {
            return false;
        }

        var runningAppId = GetRunningSteamAppId();
        return runningAppId != 0 && runningAppId == shortcutInfoResult.ShortcutInfo.AppId;
    }

    internal static uint GetRunningSteamAppId()
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

    internal static bool TryClearRunningAppId(uint expectedAppId)
    {
        if (expectedAppId == 0)
        {
            return false;
        }

        try
        {
            using var steamKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", writable: true);
            if (steamKey is null)
            {
                return false;
            }

            var rawValue = steamKey.GetValue("RunningAppID");
            var currentAppId = rawValue switch
            {
                int intValue when intValue >= 0 => unchecked((uint)intValue),
                long longValue when longValue >= 0 && longValue <= uint.MaxValue => (uint)longValue,
                string stringValue when uint.TryParse(stringValue, out var parsed) => parsed,
                _ => 0u
            };

            if (currentAppId != expectedAppId)
            {
                return false;
            }

            steamKey.SetValue("RunningAppID", 0, RegistryValueKind.DWord);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static OperationResult CreateDesktopShortcutForCurrentShortcut()
    {
        var shortcutInfoResult = FindCurrentShortcutInfo();
        if (!shortcutInfoResult.Success || shortcutInfoResult.ShortcutInfo is null)
        {
            return OperationResult.Failure(shortcutInfoResult.Message);
        }

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
        {
            return OperationResult.Failure("Could not determine the path to the current executable.");
        }

        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktopPath) || !Directory.Exists(desktopPath))
        {
            return OperationResult.Failure("Could not find the Desktop folder.");
        }

        var exeFileName = Path.GetFileNameWithoutExtension(processPath);
        var fileName = SanitizeFileName(exeFileName);
        var shortcutPath = Path.Combine(desktopPath, fileName + ".lnk");
        var legacyShortcutPath = Path.Combine(desktopPath, fileName + ".url");
        var url = $"steam://rungameid/{shortcutInfoResult.ShortcutInfo.RunGameId}";

        var explorerPath = GetExplorerExecutablePath();
        if (string.IsNullOrWhiteSpace(explorerPath))
        {
            return OperationResult.Failure("Could not find Windows Explorer for Desktop shortcut creation.");
        }

        if (File.Exists(shortcutPath))
        {
            if (!TryReadShellLink(shortcutPath, out var existingTargetPath, out var existingArguments))
            {
                return OperationResult.Failure("The existing Desktop shortcut could not be read.");
            }

            if (ShellLinkMatches(existingTargetPath, existingArguments, explorerPath, url))
            {
                return OperationResult.Successful("Desktop shortcut is already on the Desktop.");
            }

            return OperationResult.Failure(
                $"A different Desktop shortcut named \"{Path.GetFileName(shortcutPath)}\" already exists.");
        }

        if (File.Exists(legacyShortcutPath))
        {
            if (!TryReadInternetShortcutUrl(legacyShortcutPath, out var existingUrl))
            {
                return OperationResult.Failure("The existing legacy Desktop shortcut could not be read.");
            }

            if (!string.Equals(existingUrl, url, StringComparison.OrdinalIgnoreCase))
            {
                return OperationResult.Failure(
                    $"A different Desktop shortcut named \"{Path.GetFileName(legacyShortcutPath)}\" already exists.");
            }

            try
            {
                File.SetAttributes(legacyShortcutPath, FileAttributes.Normal);
                File.Delete(legacyShortcutPath);
            }
            catch
            {
                return OperationResult.Failure("The existing legacy Desktop shortcut could not be replaced.");
            }
        }

        if (!TryCreateShellLink(
                shortcutPath,
                explorerPath,
                url,
                Path.GetDirectoryName(processPath) ?? desktopPath,
                processPath,
                $"Launch {shortcutInfoResult.ShortcutInfo.AppName} via Steam"))
        {
            return OperationResult.Failure("The Desktop shortcut could not be created.");
        }

        return OperationResult.Successful("Desktop shortcut created.");
    }

    public static OperationResult OpenSteamForAddingCurrentExecutable()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
        {
            return OperationResult.Failure("Could not determine the path to the current executable.");
        }

        var manualInstruction =
            "If the Add a Non-Steam Game window does not open automatically, in Steam open " +
            "\"Games → Add a Non-Steam Game to My Library\" and select this executable:\n\n" +
            processPath;

        if (TryStartWithShell("steam://open/addnonsteamgame"))
        {
            return OperationResult.Successful("Steam was opened for adding a non-Steam game.\n\n" + manualInstruction);
        }

        var steamExePath = TryGetSteamExePath();
        if (!string.IsNullOrWhiteSpace(steamExePath) && TryStartWithShell(steamExePath))
        {
            return OperationResult.Successful("Steam was opened.\n\n" + manualInstruction);
        }

        return OperationResult.Failure(
            "Could not open Steam automatically.\n\n" +
            "Make sure Steam is installed, then add this executable to the library manually:\n\n" +
            processPath);
    }

    private static ShortcutInfoResult FindCurrentShortcutInfo()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return ShortcutInfoResult.Failure("Could not determine the path to the current executable.");
        }

        var steamPath = TryGetSteamPath();
        if (steamPath is null)
        {
            return ShortcutInfoResult.Failure("Could not find the Steam folder.");
        }

        var shortcutFiles = GetShortcutFiles(steamPath).ToArray();
        if (shortcutFiles.Length == 0)
        {
            return ShortcutInfoResult.Failure("No shortcuts.vdf files were found.");
        }

        var loadedAnyShortcutFile = false;
        string? shortcutFileLoadError = null;

        foreach (var shortcutFile in shortcutFiles)
        {
            if (!TryLoadShortcutFile(shortcutFile, out var file, out var loadErrorMessage))
            {
                shortcutFileLoadError ??= loadErrorMessage;
                continue;
            }

            loadedAnyShortcutFile = true;
            var shortcut = file.FindByExecutablePath(processPath);
            if (shortcut is not null)
            {
                return ShortcutInfoResult.Found(shortcut.ToShortcutInfo());
            }
        }

        if (!loadedAnyShortcutFile)
        {
            return ShortcutInfoResult.Failure(shortcutFileLoadError ?? "Steam shortcut files could not be read.");
        }

        return ShortcutInfoResult.Failure(
            $"No non-Steam shortcut was found for this executable: {processPath}\n\n" +
            "Make sure Steam contains the currently published executable.");
    }

    private static string? TryGetSteamPath()
    {
        try
        {
            using var steamKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var steamPath = steamKey?.GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(steamPath) && Directory.Exists(steamPath))
            {
                return steamPath;
            }

            var steamExe = steamKey?.GetValue("SteamExe") as string;
            if (!string.IsNullOrWhiteSpace(steamExe))
            {
                var steamDirectory = Path.GetDirectoryName(steamExe);
                if (!string.IsNullOrWhiteSpace(steamDirectory) && Directory.Exists(steamDirectory))
                {
                    return steamDirectory;
                }
            }
        }
        catch
        {
        }

        var defaultSteamPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam");

        return Directory.Exists(defaultSteamPath) ? defaultSteamPath : null;
    }

    private static string? TryGetSteamExePath()
    {
        try
        {
            using var steamKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var steamExe = steamKey?.GetValue("SteamExe") as string;
            if (!string.IsNullOrWhiteSpace(steamExe) && File.Exists(steamExe))
            {
                return steamExe;
            }
        }
        catch
        {
        }

        var steamPath = TryGetSteamPath();
        if (string.IsNullOrWhiteSpace(steamPath))
        {
            return null;
        }

        var defaultPath = Path.Combine(steamPath, "steam.exe");
        return File.Exists(defaultPath) ? defaultPath : null;
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

    private static string? GetExplorerExecutablePath()
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windowsDirectory))
        {
            return null;
        }

        var explorerPath = Path.Combine(windowsDirectory, "explorer.exe");
        return File.Exists(explorerPath) ? explorerPath : null;
    }

    private static bool TryCreateShellLink(
        string shortcutPath,
        string targetPath,
        string arguments,
        string workingDirectory,
        string iconPath,
        string description)
    {
        object? shellObject = null;
        object? shortcutObject = null;

        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return false;
            }

            shellObject = Activator.CreateInstance(shellType);
            if (shellObject is null)
            {
                return false;
            }

            shortcutObject = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                shellObject,
                [shortcutPath]);

            if (shortcutObject is null)
            {
                return false;
            }

            var shortcutType = shortcutObject.GetType();
            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcutObject, [targetPath]);
            shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcutObject, [arguments]);
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcutObject, [workingDirectory]);
            shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcutObject, [$"{iconPath},0"]);
            shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcutObject, [description]);
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcutObject, null);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            ReleaseComObject(shortcutObject);
            ReleaseComObject(shellObject);
        }
    }

    private static bool TryReadShellLink(string shortcutPath, out string? targetPath, out string? arguments)
    {
        object? shellObject = null;
        object? shortcutObject = null;

        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                targetPath = null;
                arguments = null;
                return false;
            }

            shellObject = Activator.CreateInstance(shellType);
            if (shellObject is null)
            {
                targetPath = null;
                arguments = null;
                return false;
            }

            shortcutObject = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                shellObject,
                [shortcutPath]);

            if (shortcutObject is null)
            {
                targetPath = null;
                arguments = null;
                return false;
            }

            var shortcutType = shortcutObject.GetType();
            targetPath = shortcutType.InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcutObject, null) as string;
            arguments = shortcutType.InvokeMember("Arguments", BindingFlags.GetProperty, null, shortcutObject, null) as string;
            return true;
        }
        catch
        {
            targetPath = null;
            arguments = null;
            return false;
        }
        finally
        {
            ReleaseComObject(shortcutObject);
            ReleaseComObject(shellObject);
        }
    }

    private static bool TryReadInternetShortcutUrl(string shortcutPath, out string? url)
    {
        try
        {
            var existingLines = File.ReadAllLines(shortcutPath);
            var existingUrlLine = existingLines.FirstOrDefault(line => line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase));
            url = existingUrlLine is null ? null : existingUrlLine[4..].Trim();
            return !string.IsNullOrWhiteSpace(url);
        }
        catch
        {
            url = null;
            return false;
        }
    }

    private static bool ShellLinkMatches(string? targetPath, string? arguments, string expectedTargetPath, string expectedArguments)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return false;
        }

        return string.Equals(NormalizePath(targetPath), NormalizePath(expectedTargetPath), StringComparison.OrdinalIgnoreCase) &&
               string.Equals(NormalizeShellArgument(arguments), NormalizeShellArgument(expectedArguments), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string NormalizeShellArgument(string? argument)
    {
        return string.IsNullOrWhiteSpace(argument)
            ? string.Empty
            : argument.Trim().Trim('"');
    }

    private static void ReleaseComObject(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            Marshal.FinalReleaseComObject(instance);
        }
    }

    private static IEnumerable<string> GetShortcutFiles(string steamPath)
    {
        var userdataPath = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdataPath))
        {
            yield break;
        }

        string[] userDirectories;
        try
        {
            userDirectories = Directory.GetDirectories(userdataPath);
        }
        catch
        {
            yield break;
        }

        foreach (var userDirectory in userDirectories)
        {
            var directoryName = Path.GetFileName(userDirectory);
            if (!ulong.TryParse(directoryName, out _))
            {
                continue;
            }

            var shortcutPath = Path.Combine(userDirectory, "config", "shortcuts.vdf");
            if (File.Exists(shortcutPath))
            {
                yield return shortcutPath;
            }
        }
    }

    private static bool TryLoadShortcutFile(string shortcutFile, out SteamShortcutsFile file, out string loadErrorMessage)
    {
        try
        {
            file = SteamShortcutsFile.Load(shortcutFile);
            loadErrorMessage = string.Empty;
            return true;
        }
        catch
        {
            file = null!;
            loadErrorMessage = BuildShortcutFileReadErrorMessage(shortcutFile);
            return false;
        }
    }

    private static void CreateBackup(string shortcutFile)
    {
        var backupPath = shortcutFile + ".bak";
        DeleteLegacyBackups(shortcutFile, backupPath);

        if (File.Exists(backupPath))
        {
            File.SetAttributes(backupPath, FileAttributes.Normal);
            File.Delete(backupPath);
        }

        File.Copy(shortcutFile, backupPath, overwrite: false);
    }

    private static void DeleteLegacyBackups(string shortcutFile, string currentBackupPath)
    {
        var directory = Path.GetDirectoryName(shortcutFile);
        var fileName = Path.GetFileName(shortcutFile);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName) || !Directory.Exists(directory))
        {
            return;
        }

        foreach (var backupFile in Directory.EnumerateFiles(directory, fileName + ".*.bak"))
        {
            if (string.Equals(backupFile, currentBackupPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var backupFileName = Path.GetFileName(backupFile);
            var timestampStartIndex = fileName.Length + 1;
            var timestampLength = backupFileName.Length - timestampStartIndex - ".bak".Length;
            if (timestampLength <= 0)
            {
                continue;
            }

            var timestamp = backupFileName.Substring(timestampStartIndex, timestampLength);
            if (!DateTime.TryParseExact(
                    timestamp,
                    "yyyyMMddHHmmss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _))
            {
                continue;
            }

            File.SetAttributes(backupFile, FileAttributes.Normal);
            File.Delete(backupFile);
        }
    }

    private static string BuildShortcutFileReadErrorMessage(string shortcutFile)
    {
        return $"Steam shortcut data could not be read from:\n{shortcutFile}\n\nClose Steam and try again. If the problem continues, restore the file from its backup.";
    }

    private static string BuildShortcutFileWriteErrorMessage(string shortcutFile)
    {
        return $"Steam shortcut data could not be updated in:\n{shortcutFile}\n\nClose Steam and try again. If the problem continues, restore the file from shortcuts.vdf.bak.";
    }

    internal sealed record RenameLookupResult(bool Success, string Message, string? CurrentName)
    {
        public static RenameLookupResult Found(string? currentName) => new(true, string.Empty, currentName);

        public static RenameLookupResult Failure(string message) => new(false, message, null);
    }

    internal sealed record ShortcutRegistrationStatus(
        bool IsRegistered,
        string Description,
        string? Hint,
        string? Tooltip,
        string? CurrentName)
    {
        public static ShortcutRegistrationStatus Registered(string description, string? currentName) =>
            new(true, description, null, null, currentName);

        public static ShortcutRegistrationStatus NotRegistered(string description, string hint, string? tooltip) =>
            new(false, description, hint, tooltip, null);
    }

    internal sealed record RenameResult(bool Success, string Message)
    {
        public static RenameResult Successful(string message) => new(true, message);

        public static RenameResult Failure(string message) => new(false, message);
    }

    internal sealed record OperationResult(bool Success, string Message)
    {
        public static OperationResult Successful(string message) => new(true, message);

        public static OperationResult Failure(string message) => new(false, message);
    }

    internal sealed record ShortcutInfoResult(bool Success, string Message, ShortcutInfo? ShortcutInfo)
    {
        public static ShortcutInfoResult Found(ShortcutInfo shortcutInfo) => new(true, string.Empty, shortcutInfo);

        public static ShortcutInfoResult Failure(string message) => new(false, message, null);
    }

    internal sealed record ShortcutInfo(string AppName, uint AppId, ulong RunGameId);

    private static uint ComputeNonSteamShortcutAppId(string executableValue, string appName)
    {
        var payload = string.Concat(executableValue, appName);
        var bytes = Encoding.UTF8.GetBytes(payload);
        var crc = 0xFFFFFFFFu;

        foreach (var currentByte in bytes)
        {
            crc ^= currentByte;
            for (var bit = 0; bit < 8; bit++)
            {
                var mask = (crc & 1u) != 0 ? 0xEDB88320u : 0u;
                crc = (crc >> 1) ^ mask;
            }
        }

        return (~crc) | 0x80000000u;
    }

    private sealed class SteamShortcutsFile
    {
        private const byte ObjectType = 0x00;
        private const byte StringType = 0x01;
        private const byte Int32Type = 0x02;
        private const byte EndType = 0x08;

        private static readonly Encoding TextEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        private readonly VdfObject _root;

        private SteamShortcutsFile(VdfObject root)
        {
            _root = root;
        }

        public static SteamShortcutsFile Load(string path)
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, TextEncoding, leaveOpen: false);

            var rootType = reader.ReadByte();
            if (rootType != ObjectType)
            {
                throw new InvalidDataException("Invalid shortcuts.vdf: the root object is missing.");
            }

            var rootName = ReadNullTerminatedString(reader);
            if (!string.Equals(rootName, "shortcuts", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Invalid shortcuts.vdf: unexpected root key.");
            }

            var root = ReadObject(reader);
            return new SteamShortcutsFile(root);
        }

        public ShortcutEntry? FindByExecutablePath(string executablePath)
        {
            var normalizedExecutablePath = NormalizeExecutablePath(executablePath);

            foreach (var property in _root.Properties)
            {
                if (property.Type != ObjectType || property.Value is not VdfObject entryObject)
                {
                    continue;
                }

                var entry = new ShortcutEntry(entryObject);
                if (entry.MatchesExecutablePath(normalizedExecutablePath))
                {
                    return entry;
                }
            }

            return null;
        }

        public bool TryRenameByExecutablePath(string executablePath, string newName, out int renamedEntries)
        {
            renamedEntries = 0;
            var normalizedExecutablePath = NormalizeExecutablePath(executablePath);

            foreach (var property in _root.Properties)
            {
                if (property.Type != ObjectType || property.Value is not VdfObject entryObject)
                {
                    continue;
                }

                var entry = new ShortcutEntry(entryObject);
                if (!entry.MatchesExecutablePath(normalizedExecutablePath))
                {
                    continue;
                }

                entry.SetAppName(newName);
                entry.RefreshAppId();
                renamedEntries++;
            }

            return renamedEntries > 0;
        }

        public void Save(string path)
        {
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream, TextEncoding, leaveOpen: false);

            writer.Write(ObjectType);
            WriteNullTerminatedString(writer, "shortcuts");
            WriteObject(writer, _root);
            writer.Write(EndType);
            writer.Write(EndType);
        }

        private static VdfObject ReadObject(BinaryReader reader)
        {
            var result = new VdfObject();

            while (true)
            {
                var valueType = reader.ReadByte();
                if (valueType == EndType)
                {
                    return result;
                }

                var name = ReadNullTerminatedString(reader);
                object value = valueType switch
                {
                    ObjectType => ReadObject(reader),
                    StringType => ReadNullTerminatedString(reader),
                    Int32Type => reader.ReadInt32(),
                    _ => throw new InvalidDataException($"Unsupported VDF field type: 0x{valueType:X2}.")
                };

                result.Properties.Add(new VdfProperty(valueType, name, value));
            }
        }

        private static void WriteObject(BinaryWriter writer, VdfObject obj)
        {
            foreach (var property in obj.Properties)
            {
                writer.Write(property.Type);
                WriteNullTerminatedString(writer, property.Name);

                switch (property.Type)
                {
                    case ObjectType:
                        WriteObject(writer, (VdfObject)property.Value);
                        writer.Write(EndType);
                        break;
                    case StringType:
                        WriteNullTerminatedString(writer, (string)property.Value);
                        break;
                    case Int32Type:
                        writer.Write((int)property.Value);
                        break;
                    default:
                        throw new InvalidDataException($"Unsupported VDF field type: 0x{property.Type:X2}.");
                }
            }
        }

        private static string ReadNullTerminatedString(BinaryReader reader)
        {
            using var buffer = new MemoryStream();

            while (true)
            {
                var nextByte = reader.ReadByte();
                if (nextByte == 0)
                {
                    return TextEncoding.GetString(buffer.ToArray());
                }

                buffer.WriteByte(nextByte);
            }
        }

        private static void WriteNullTerminatedString(BinaryWriter writer, string value)
        {
            var bytes = TextEncoding.GetBytes(value);
            writer.Write(bytes);
            writer.Write((byte)0);
        }

        private static string NormalizeExecutablePath(string path)
        {
            var trimmed = path.Trim();

            if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed.Contains("\" ", StringComparison.Ordinal))
            {
                var closingQuoteIndex = trimmed.IndexOf("\" ", StringComparison.Ordinal);
                trimmed = trimmed[1..closingQuoteIndex];
            }
            else
            {
                trimmed = trimmed.Trim('"');
            }

            if (!Path.IsPathFullyQualified(trimmed))
            {
                return trimmed;
            }

            return Path.GetFullPath(trimmed)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        public sealed class ShortcutEntry
        {
            private readonly VdfObject _entryObject;

            public ShortcutEntry(VdfObject entryObject)
            {
                _entryObject = entryObject;
            }

            public string? AppName => GetString("AppName") ?? GetString("appname");

            public uint? AppId => GetUInt32("appid");

            private string? ExecutableValue => GetString("Exe") ?? GetString("exe");

            public bool MatchesExecutablePath(string normalizedExecutablePath)
            {
                var entryExecutablePath = GetString("Exe") ?? GetString("exe");
                if (string.IsNullOrWhiteSpace(entryExecutablePath))
                {
                    return false;
                }

                var normalizedEntryPath = NormalizeExecutablePath(entryExecutablePath);
                return string.Equals(normalizedEntryPath, normalizedExecutablePath, StringComparison.OrdinalIgnoreCase);
            }

            public void SetAppName(string newName)
            {
                if (TrySetString("AppName", newName) || TrySetString("appname", newName))
                {
                    return;
                }

                _entryObject.Properties.Insert(0, new VdfProperty(StringType, "AppName", newName));
            }

            public void RefreshAppId()
            {
                var executableValue = ExecutableValue;
                var appName = AppName;
                if (string.IsNullOrWhiteSpace(executableValue) || string.IsNullOrWhiteSpace(appName))
                {
                    return;
                }

                SetUInt32("appid", ComputeNonSteamShortcutAppId(executableValue, appName));
            }

            public ShortcutInfo ToShortcutInfo()
            {
                var appName = AppName ?? "Steam Game Custom Status";
                var appId = AppId ?? 0;
                var runGameId = ((ulong)appId << 32) | 0x02000000UL;
                return new ShortcutInfo(appName, appId, runGameId);
            }

            private string? GetString(string name)
            {
                foreach (var property in _entryObject.Properties)
                {
                    if (property.Type == StringType && string.Equals(property.Name, name, StringComparison.Ordinal))
                    {
                        return property.Value as string;
                    }
                }

                return null;
            }

            private bool TrySetString(string name, string value)
            {
                for (var i = 0; i < _entryObject.Properties.Count; i++)
                {
                    var property = _entryObject.Properties[i];
                    if (property.Type != StringType || !string.Equals(property.Name, name, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    _entryObject.Properties[i] = property with { Value = value };
                    return true;
                }

                return false;
            }

            private uint? GetUInt32(string name)
            {
                foreach (var property in _entryObject.Properties)
                {
                    if (property.Type == Int32Type && string.Equals(property.Name, name, StringComparison.Ordinal))
                    {
                        return unchecked((uint)(int)property.Value);
                    }
                }

                return null;
            }

            private void SetUInt32(string name, uint value)
            {
                var intValue = unchecked((int)value);
                for (var i = 0; i < _entryObject.Properties.Count; i++)
                {
                    var property = _entryObject.Properties[i];
                    if (property.Type != Int32Type || !string.Equals(property.Name, name, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    _entryObject.Properties[i] = property with { Value = intValue };
                    return;
                }

                _entryObject.Properties.Add(new VdfProperty(Int32Type, name, intValue));
            }
        }

        public sealed class VdfObject
        {
            public List<VdfProperty> Properties { get; } = [];
        }

        public sealed record VdfProperty(byte Type, string Name, object Value);
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "Steam Non-Steam Game" : sanitized;
    }
}
