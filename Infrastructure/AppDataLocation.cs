using System.IO;

namespace SteamGameCustomStatus.Infrastructure;

/// <summary>
/// Portable data folder next to the executable, with a per-user fallback when the executable folder is read-only.
/// </summary>
internal static class AppDataLocation
{
    private const string PortableFolderName = "SteamGameCustomStatus.data";

    private static readonly Lazy<string> RootLazy = new(ResolveRoot);

    public static string Root => RootLazy.Value;

    public static string NintendoSettingsPath => Path.Combine(Root, "nintendo.json");

    public static string NxapiDataDirectory => Path.Combine(Root, "nxapi");

    public static string BinDirectory => Path.Combine(Root, "bin");

    public static string IconsDirectory => Path.Combine(Root, "icons");

    public static string WebViewDataDirectory => Path.Combine(Root, "webview");

    private static string ResolveRoot()
    {
        var executableDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrWhiteSpace(executableDirectory))
        {
            var portablePath = Path.Combine(executableDirectory, PortableFolderName);
            if (IsWritableDirectory(portablePath))
            {
                return portablePath;
            }
        }

        var fallbackPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamGameCustomStatus");
        Directory.CreateDirectory(fallbackPath);
        return fallbackPath;
    }

    private static bool IsWritableDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probePath = Path.Combine(path, ".write-probe");
            File.WriteAllText(probePath, string.Empty);
            File.Delete(probePath);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
