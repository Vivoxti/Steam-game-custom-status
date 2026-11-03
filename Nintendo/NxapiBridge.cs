using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SteamGameCustomStatus.Infrastructure;

namespace SteamGameCustomStatus.Nintendo;

/// <summary>
/// Runs the embedded nxapi-bridge.exe (Node SEA bundle of nxapi) and parses its JSON output.
/// </summary>
internal static class NxapiBridge
{
    public const string ResourceName = "SteamGameCustomStatus.Assets.nxapi-bridge.exe.br";
    private const string ExecutablePrefix = "nxapi-bridge-";
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(90);
    private static readonly SemaphoreSlim ExtractionLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string? _extractedPath;

    public static bool IsEmbedded => Assembly.GetExecutingAssembly().GetManifestResourceInfo(ResourceName) is not null;

    public static async Task<BridgeResult<AuthUrlInfo>> GetAuthUrlAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["auth-url"], cancellationToken).ConfigureAwait(false);
        return result.Map(root => new AuthUrlInfo(
            root.GetProperty("url").GetString() ?? string.Empty,
            root.GetProperty("verifier").GetString() ?? string.Empty));
    }

    public static async Task<BridgeResult<NintendoAccount>> LoginAsync(string verifier, string redirectUrl, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["login", "--verifier", verifier, "--redirect-url", redirectUrl], cancellationToken).ConfigureAwait(false);
        return result.Map(root => Deserialize<NintendoAccount>(root.GetProperty("account")));
    }

    public static async Task<BridgeResult<NintendoAccount?>> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["status"], cancellationToken).ConfigureAwait(false);
        return result.Map(root =>
            root.TryGetProperty("loggedIn", out var loggedIn) && loggedIn.ValueKind == JsonValueKind.True &&
            root.TryGetProperty("account", out var account)
                ? Deserialize<NintendoAccount>(account)
                : null);
    }

    public static async Task<BridgeResult<bool>> LogoutAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["logout"], cancellationToken).ConfigureAwait(false);
        return result.Map(_ => true);
    }

    public static async Task<BridgeResult<IReadOnlyList<NintendoFriend>>> GetFriendsAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["friends"], cancellationToken).ConfigureAwait(false);
        return result.Map(root =>
            (IReadOnlyList<NintendoFriend>)(Deserialize<List<NintendoFriend>>(root.GetProperty("friends")) ?? []));
    }

    public static async Task<BridgeResult<NintendoPresence>> GetPresenceAsync(string nsaId, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["presence", "--nsaid", nsaId], cancellationToken).ConfigureAwait(false);
        return result.Map(root => Deserialize<NintendoPresence>(root.GetProperty("presence")));
    }

    public static async Task<Process> StartWatchProcessAsync(string nsaId, int intervalSeconds, CancellationToken cancellationToken)
    {
        var executablePath = await EnsureExtractedAsync(cancellationToken).ConfigureAwait(false);
        var startInfo = CreateStartInfo(executablePath, ["watch", "--nsaid", nsaId, "--interval", intervalSeconds.ToString()]);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("nxapi-bridge could not be started.");
    }

    public static BridgeLine ParseLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                var presence = root.TryGetProperty("presence", out var presenceElement)
                    ? Deserialize<NintendoPresence>(presenceElement)
                    : null;
                return new BridgeLine(true, null, null, presence);
            }

            return new BridgeLine(false, GetString(root, "error") ?? "unknown", GetString(root, "message"), null);
        }
        catch (JsonException)
        {
            return new BridgeLine(false, "invalid_output", line, null);
        }
    }

    private static async Task<BridgeResult<JsonElement>> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        string executablePath;
        try
        {
            executablePath = await EnsureExtractedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return BridgeResult<JsonElement>.Failure("bridge_missing", exception.Message);
        }

        using var process = new Process { StartInfo = CreateStartInfo(executablePath, arguments) };
        try
        {
            process.Start();
            process.StandardInput.Close();
        }
        catch (Exception exception)
        {
            return BridgeResult<JsonElement>.Failure("bridge_start_failed", exception.Message);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DefaultCommandTimeout);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return ParseCommandOutput(stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            cancellationToken.ThrowIfCancellationRequested();
            return BridgeResult<JsonElement>.Failure("timeout", "nxapi-bridge did not respond in time.");
        }
    }

    private static BridgeResult<JsonElement> ParseCommandOutput(string stdout, string stderr)
    {
        var lastLine = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

        if (string.IsNullOrWhiteSpace(lastLine))
        {
            var detail = stderr.Trim();
            return BridgeResult<JsonElement>.Failure(
                "invalid_output",
                string.IsNullOrWhiteSpace(detail) ? "nxapi-bridge returned no output." : detail);
        }

        try
        {
            using var document = JsonDocument.Parse(lastLine);
            var root = document.RootElement.Clone();
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                return BridgeResult<JsonElement>.Successful(root);
            }

            return BridgeResult<JsonElement>.Failure(
                GetString(root, "error") ?? "unknown",
                GetString(root, "message") ?? "nxapi-bridge reported an error.");
        }
        catch (JsonException)
        {
            return BridgeResult<JsonElement>.Failure("invalid_output", lastLine);
        }
    }

    private static ProcessStartInfo CreateStartInfo(string executablePath, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppDataLocation.Root
        };

        startInfo.ArgumentList.Add("--data");
        startInfo.ArgumentList.Add(AppDataLocation.NxapiDataDirectory);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static async Task<string> EnsureExtractedAsync(CancellationToken cancellationToken)
    {
        if (_extractedPath is not null && File.Exists(_extractedPath))
        {
            return _extractedPath;
        }

        await ExtractionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_extractedPath is not null && File.Exists(_extractedPath))
            {
                return _extractedPath;
            }

            _extractedPath = await Task.Run(ExtractBridge, cancellationToken).ConfigureAwait(false);
            return _extractedPath;
        }
        finally
        {
            ExtractionLock.Release();
        }
    }

    private static string ExtractBridge()
    {
        var assembly = Assembly.GetExecutingAssembly();
        string hash;
        using (var hashStream = assembly.GetManifestResourceStream(ResourceName)
                                ?? throw new FileNotFoundException("The Nintendo bridge is not included in this build."))
        {
            hash = Convert.ToHexString(SHA256.HashData(hashStream))[..16].ToLowerInvariant();
        }

        var binDirectory = AppDataLocation.BinDirectory;
        Directory.CreateDirectory(binDirectory);
        var targetPath = Path.Combine(binDirectory, ExecutablePrefix + hash + ".exe");

        if (!File.Exists(targetPath))
        {
            var temporaryPath = targetPath + ".tmp";
            using (var resourceStream = assembly.GetManifestResourceStream(ResourceName)!)
            using (var brotliStream = new BrotliStream(resourceStream, CompressionMode.Decompress))
            using (var fileStream = File.Create(temporaryPath))
            {
                brotliStream.CopyTo(fileStream);
            }

            File.Move(temporaryPath, targetPath, overwrite: true);
        }

        DeleteStaleBridges(binDirectory, targetPath);
        return targetPath;
    }

    private static void DeleteStaleBridges(string binDirectory, string currentPath)
    {
        foreach (var file in Directory.EnumerateFiles(binDirectory, ExecutablePrefix + "*"))
        {
            if (string.Equals(file, currentPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch
            {
                // still in use by an older instance; removed on a later start
            }
        }
    }

    internal static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // ignored
        }
    }

    private static T Deserialize<T>(JsonElement element)
    {
        return element.Deserialize<T>(JsonOptions)!;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}

internal sealed record BridgeResult<T>(bool Success, T? Value, string? ErrorCode, string? Message)
{
    public static BridgeResult<T> Successful(T value) => new(true, value, null, null);

    public static BridgeResult<T> Failure(string errorCode, string? message) => new(false, default, errorCode, message);

    public BridgeResult<TResult> Map<TResult>(Func<T, TResult> map)
    {
        if (!Success)
        {
            return BridgeResult<TResult>.Failure(ErrorCode ?? "unknown", Message);
        }

        try
        {
            return BridgeResult<TResult>.Successful(map(Value!));
        }
        catch (Exception exception)
        {
            return BridgeResult<TResult>.Failure("invalid_output", exception.Message);
        }
    }
}

internal sealed record BridgeLine(bool Success, string? ErrorCode, string? Message, NintendoPresence? Presence);

internal sealed record AuthUrlInfo(string Url, string Verifier);

internal sealed record NintendoAccount(string? Naid, string? NsaId, string? Name, string? Nickname, string? ImageUri)
{
    public string DisplayName => Name ?? Nickname ?? NsaId ?? "Nintendo account";
}

internal sealed record NintendoFriend(string NsaId, string? Name, string? ImageUri, NintendoPresence? Presence);

internal sealed record NintendoPresence(string? State, long? UpdatedAt, int? Platform, NintendoGame? Game)
{
    /// <summary>
    /// Same rule nxapi uses for Discord presence: online (or playing) with a named title.
    /// </summary>
    public string? PlayingTitle =>
        State is "ONLINE" or "PLAYING" && !string.IsNullOrWhiteSpace(Game?.Name)
            ? Game!.Name!.Trim()
            : null;
}

internal sealed record NintendoGame(
    string? Name,
    string? ImageUri,
    string? ShopUri,
    string? SysDescription,
    int? TotalPlayTime,
    long? FirstPlayedAt);
