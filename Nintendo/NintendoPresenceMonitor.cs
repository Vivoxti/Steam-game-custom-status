using System.Diagnostics;

namespace SteamGameCustomStatus.Nintendo;

/// <summary>
/// Keeps one long-running <c>nxapi-bridge watch</c> process alive and reports each poll result.
/// Callbacks run on a thread-pool thread.
/// </summary>
internal sealed class NintendoPresenceMonitor : IDisposable
{
    private static readonly TimeSpan MinRestartDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxRestartDelay = TimeSpan.FromMinutes(5);
    private static readonly HashSet<string> FatalErrorCodes = ["not_logged_in", "auth_failed", "friend_not_found", "bridge_missing"];

    private readonly string _nsaId;
    private readonly int _intervalSeconds;
    private readonly Action<NintendoPresence> _onPresence;
    private readonly Action<string, string?, bool> _onError;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _loopTask;

    public NintendoPresenceMonitor(
        string nsaId,
        int intervalSeconds,
        Action<NintendoPresence> onPresence,
        Action<string, string?, bool> onError)
    {
        _nsaId = nsaId;
        _intervalSeconds = intervalSeconds;
        _onPresence = onPresence;
        _onError = onError;
    }

    public void Start()
    {
        _loopTask ??= Task.Run(() => RunLoopAsync(_cancellation.Token));
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        try
        {
            _loopTask?.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // ignored
        }

        _cancellation.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        var restartDelay = MinRestartDelay;

        while (!cancellationToken.IsCancellationRequested)
        {
            string? fatalErrorCode = null;
            var receivedPresence = false;

            try
            {
                using var process = await NxapiBridge.StartWatchProcessAsync(_nsaId, _intervalSeconds, cancellationToken).ConfigureAwait(false);
                using var registration = cancellationToken.Register(() => StopProcess(process));
                _ = DrainAsync(process.StandardError);

                while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var parsed = NxapiBridge.ParseLine(line);
                    if (parsed is { Success: true, Presence: not null })
                    {
                        receivedPresence = true;
                        _onPresence(parsed.Presence);
                        continue;
                    }

                    var errorCode = parsed.ErrorCode ?? "unknown";
                    var isFatal = FatalErrorCodes.Contains(errorCode);
                    if (isFatal)
                    {
                        fatalErrorCode = errorCode;
                    }

                    _onError(errorCode, parsed.Message, isFatal);
                }

                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                var errorCode = exception is System.IO.FileNotFoundException ? "bridge_missing" : "bridge_start_failed";
                if (FatalErrorCodes.Contains(errorCode))
                {
                    fatalErrorCode = errorCode;
                }

                _onError(errorCode, exception.Message, fatalErrorCode is not null);
            }

            if (fatalErrorCode is not null || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            restartDelay = receivedPresence
                ? MinRestartDelay
                : TimeSpan.FromTicks(Math.Min(restartDelay.Ticks * 2, MaxRestartDelay.Ticks));

            try
            {
                await Task.Delay(restartDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static async Task DrainAsync(System.IO.StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is not null)
            {
            }
        }
        catch
        {
            // ignored
        }
    }

    private static void StopProcess(Process process)
    {
        try
        {
            process.StandardInput.Close();
            if (!process.WaitForExit(2000))
            {
                NxapiBridge.TryKill(process);
            }
        }
        catch
        {
            NxapiBridge.TryKill(process);
        }
    }
}
