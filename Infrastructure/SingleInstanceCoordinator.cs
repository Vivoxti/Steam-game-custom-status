using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace SteamGameCustomStatus.Infrastructure;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _pipeName;
    private readonly Func<InstanceStartupRequest, InstanceStartupResponse> _requestHandler;
    private readonly Mutex _mutex;

    private CancellationTokenSource? _serverCancellationTokenSource;
    private Task? _serverTask;
    private bool _ownsMutex;
    private bool _isDisposed;

    public SingleInstanceCoordinator(Func<InstanceStartupRequest, InstanceStartupResponse> requestHandler)
    {
        _requestHandler = requestHandler;

        var scopeId = GetCurrentUserScopeId();
        _pipeName = $"SteamGameCustomStatus_{scopeId}_Pipe";
        _mutex = new Mutex(false, $@"Local\SteamGameCustomStatus_{scopeId}_Mutex");
    }

    public SingleInstanceStartupResult RegisterCurrentInstance(bool isSteamLaunch)
    {
        if (TryAcquireMutex(TimeSpan.Zero))
        {
            StartServer();
            return SingleInstanceStartupResult.Primary();
        }

        var response = TryNotifyExistingInstance(new InstanceStartupRequest(isSteamLaunch));
        if (response?.Action == InstanceTransferAction.YieldToNewInstance && WaitForOwnershipTransfer())
        {
            StartServer();
            return SingleInstanceStartupResult.Primary();
        }

        return SingleInstanceStartupResult.HandledByExisting();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        _serverCancellationTokenSource?.Cancel();
        _serverCancellationTokenSource?.Dispose();
        _serverCancellationTokenSource = null;

        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
            _ownsMutex = false;
        }

        _mutex.Dispose();
    }

    private void StartServer()
    {
        if (_serverTask is not null)
        {
            return;
        }

        _serverCancellationTokenSource = new CancellationTokenSource();
        _serverTask = Task.Run(() => ListenForInstanceMessagesAsync(_serverCancellationTokenSource.Token));
    }

    private async Task ListenForInstanceMessagesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            try
            {
                using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true);
                using var writer = CreateAutoFlushWriter(server);

                var requestLine = await reader.ReadLineAsync().ConfigureAwait(false);
                var request = string.IsNullOrWhiteSpace(requestLine)
                    ? null
                    : JsonSerializer.Deserialize<InstanceStartupRequest>(requestLine, JsonOptions);

                var response = request is null
                    ? new InstanceStartupResponse(InstanceTransferAction.ActivateExistingInstance)
                    : _requestHandler(request);

                var responseLine = JsonSerializer.Serialize(response, JsonOptions);
                await writer.WriteLineAsync(responseLine).ConfigureAwait(false);
            }
            catch (IOException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
            }
            catch (JsonException)
            {
            }
        }
    }

    private InstanceStartupResponse? TryNotifyExistingInstance(InstanceStartupRequest request)
    {
        var requestLine = JsonSerializer.Serialize(request, JsonOptions);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.None);
                client.Connect(300);

                using var reader = new StreamReader(client, Encoding.UTF8, false, 1024, true);
                using var writer = CreateAutoFlushWriter(client);

                writer.WriteLine(requestLine);

                var responseLine = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(responseLine))
                {
                    return null;
                }

                return JsonSerializer.Deserialize<InstanceStartupResponse>(responseLine, JsonOptions);
            }
            catch (TimeoutException)
            {
                Thread.Sleep(150);
            }
            catch (IOException)
            {
                Thread.Sleep(150);
            }
        }

        return null;
    }

    private bool WaitForOwnershipTransfer()
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (TryAcquireMutex(TimeSpan.FromMilliseconds(250)))
            {
                return true;
            }
        }

        return false;
    }

    private static StreamWriter CreateAutoFlushWriter(Stream stream)
    {
        return new StreamWriter(stream, new UTF8Encoding(false), 1024, true)
        {
            AutoFlush = true
        };
    }

    private bool TryAcquireMutex(TimeSpan timeout)
    {
        if (_ownsMutex)
        {
            return true;
        }

        try
        {
            _ownsMutex = _mutex.WaitOne(timeout);
            return _ownsMutex;
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
            return true;
        }
    }

    private static string GetCurrentUserScopeId()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var rawScopeId = identity.User?.Value ?? Environment.UserName;

        var builder = new StringBuilder(rawScopeId.Length);
        foreach (var character in rawScopeId)
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        return builder.ToString();
    }
}

internal sealed record InstanceStartupRequest(bool IsSteamLaunch);

internal sealed record InstanceStartupResponse(InstanceTransferAction Action);

internal enum InstanceTransferAction
{
    ActivateExistingInstance,
    YieldToNewInstance
}

internal sealed record SingleInstanceStartupResult(bool ShouldContinueAsPrimary)
{
    public static SingleInstanceStartupResult Primary() => new(true);

    public static SingleInstanceStartupResult HandledByExisting() => new(false);
}
