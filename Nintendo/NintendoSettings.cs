using System.IO;
using System.Text.Json;
using SteamGameCustomStatus.Infrastructure;

namespace SteamGameCustomStatus.Nintendo;

internal sealed class NintendoSettings
{
    public const int DefaultPollIntervalSeconds = 120;
    public const int MinPollIntervalSeconds = 60;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public bool Enabled { get; set; }

    public string? WatchedNsaId { get; set; }

    public string? WatchedName { get; set; }

    public int PollIntervalSeconds { get; set; } = DefaultPollIntervalSeconds;

    public int EffectivePollIntervalSeconds => Math.Max(MinPollIntervalSeconds, PollIntervalSeconds);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(WatchedNsaId);

    public static NintendoSettings Load()
    {
        try
        {
            var path = AppDataLocation.NintendoSettingsPath;
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<NintendoSettings>(File.ReadAllText(path), JsonOptions) ?? new NintendoSettings();
            }
        }
        catch
        {
            // ignored: fall back to defaults
        }

        return new NintendoSettings();
    }

    public void Save()
    {
        var path = AppDataLocation.NintendoSettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
