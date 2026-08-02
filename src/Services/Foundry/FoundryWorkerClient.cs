using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace EverythingDiskUsage.Services.Foundry;

public sealed class FoundryWorkerClient(IAppLogger logger) : IFoundryLocalModelService, IDisposable
{
    private const string WorkerEnvironmentVariable = "EVERYTHING_DISK_USAGE_FOUNDRY_WORKER";
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ResetSettleDelay = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private Process? _process;
    private TextWriter? _standardInput;
    private PendingRequest? _pending;
    private TaskCompletionSource<bool>? _ready;
    private string? _loadedModelAlias;
    private int _nextRequestId;
    private bool _disposed;

    public async Task<IReadOnlyList<FoundryModelOption>> ListModelsAsync(
        IProgress<FoundryProgress>? progress,
        CancellationToken cancellationToken)
    {
        var message = await SendRequestAsync(
            new FoundryWorkerRequest { Operation = FoundryWorkerProtocol.Operations.ListModels },
            progress,
            cancellationToken).ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(message.PayloadJson)
            ? []
            : JsonSerializer.Deserialize<List<FoundryModelOption>>(
                message.PayloadJson,
                FoundryWorkerProtocol.JsonOptions) ?? [];
    }

    public async Task PrepareModelAsync(
        string modelAlias,
        IProgress<FoundryProgress>? progress,
        CancellationToken cancellationToken)
    {
        var alias = RequireModelAlias(modelAlias);
        await ResetForModelSwitchAsync(alias, cancellationToken).ConfigureAwait(false);
        await SendRequestAsync(
            new FoundryWorkerRequest
            {
                Operation = FoundryWorkerProtocol.Operations.PrepareModel,
                ModelAlias = alias
            },
            progress,
            cancellationToken).ConfigureAwait(false);
        _loadedModelAlias = alias;
    }

    public async Task<DuplicateRecommendationResult> RecommendAsync(
        DuplicateRecommendationRequest request,
        string modelAlias,
        TimeSpan timeout,
        IProgress<FoundryProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Candidates.Count is < 2 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "AI advice supports duplicate sets containing 2 to 100 files.");
        }

        var alias = RequireModelAlias(modelAlias);
        var timeoutSeconds = Math.Clamp((int)Math.Ceiling(timeout.TotalSeconds), 10, 600);
        await ResetForModelSwitchAsync(alias, cancellationToken).ConfigureAwait(false);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            var message = await SendRequestAsync(
                new FoundryWorkerRequest
                {
                    Operation = FoundryWorkerProtocol.Operations.Recommend,
                    ModelAlias = alias,
                    PayloadJson = JsonSerializer.Serialize(request, FoundryWorkerProtocol.JsonOptions),
                    TimeoutSeconds = timeoutSeconds
                },
                progress,
                timeoutSource.Token).ConfigureAwait(false);
            _loadedModelAlias = alias;
            return DuplicateRecommendationValidator.Validate(message.PayloadJson ?? string.Empty, request, message.ModelKey ?? alias);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The local model did not respond within {timeoutSeconds} seconds.");
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ResetWorkerCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task ResetForModelSwitchAsync(string alias, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_loadedModelAlias) ||
            _loadedModelAlias.Equals(alias, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        logger.Info($"Foundry model changed from '{_loadedModelAlias}' to '{alias}'; resetting the isolated worker");
        await ResetAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<FoundryWorkerMessage> SendRequestAsync(
        FoundryWorkerRequest request,
        IProgress<FoundryProgress>? progress,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureWorkerAsync(cancellationToken).ConfigureAwait(false);
            var requestId = Interlocked.Increment(ref _nextRequestId);
            request.Id = requestId;
            var pending = new PendingRequest(requestId, progress);
            _pending = pending;
            using var registration = cancellationToken.Register(
                static state =>
                {
                    var callback = ((FoundryWorkerClient Client, int RequestId))state!;
                    _ = callback.Client.SendCancelAsync(callback.RequestId);
                },
                (this, requestId));

            await SendLineAsync(request).ConfigureAwait(false);
            var message = await pending.Terminal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!message.Ok)
            {
                throw new FoundryWorkerException(message.Error ?? "The local AI worker reported a failure.");
            }

            return message;
        }
        catch (OperationCanceledException)
        {
            await ResetAfterFailureAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await ResetAfterFailureAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _pending = null;
            _operationLock.Release();
        }
    }

    private async Task ResetAfterFailureAsync()
    {
        try
        {
            using var cleanupSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await ResetWorkerCoreAsync(cleanupSource.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.Warning($"Failed to reset the Foundry worker after an operation error: {exception.Message}");
        }
    }

    private async Task EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        if (_standardInput is not null && _process is { HasExited: false })
        {
            return;
        }

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_standardInput is not null && _process is { HasExited: false })
            {
                return;
            }

            var workerPath = ResolveWorkerPath();
            if (!File.Exists(workerPath))
            {
                throw new FileNotFoundException(
                    "The Foundry Local worker is not installed. Rebuild or reinstall Everything Disk Usage.",
                    workerPath);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = workerPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = Utf8NoBom,
                StandardOutputEncoding = Utf8NoBom
            };
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Exited += (_, _) => OnWorkerExited(process);
            if (!process.Start())
            {
                throw new FoundryWorkerException("The Foundry Local worker could not be started.");
            }

            _process = process;
            _standardInput = process.StandardInput;
            _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Task.Run(() => ReadStandardOutputAsync(process), CancellationToken.None);
            _ = Task.Run(() => ReadStandardErrorAsync(process), CancellationToken.None);

            using var readySource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readySource.CancelAfter(ReadyTimeout);
            await _ready.Task.WaitAsync(readySource.Token).ConfigureAwait(false);
            logger.Info($"Foundry worker ready; pid={process.Id}");
        }
        catch
        {
            KillExactWorker(_process);
            _process = null;
            _standardInput = null;
            throw;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task ReadStandardOutputAsync(Process process)
    {
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                FoundryWorkerMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<FoundryWorkerMessage>(line, FoundryWorkerProtocol.JsonOptions);
                }
                catch (JsonException exception)
                {
                    logger.Warning($"Ignored invalid Foundry worker protocol output: {exception.Message}");
                    continue;
                }

                if (message is not null)
                {
                    RouteMessage(message);
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.Warning($"Foundry worker output reader stopped: {exception.Message}");
        }
    }

    private async Task ReadStandardErrorAsync(Process process)
    {
        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    logger.Debug($"Foundry worker: {line}");
                }
            }
        }
        catch
        {
        }
    }

    private void RouteMessage(FoundryWorkerMessage message)
    {
        if (message.Type == FoundryWorkerProtocol.MessageTypes.Ready)
        {
            _ready?.TrySetResult(true);
            return;
        }

        var pending = _pending;
        if (pending is null || pending.Id != message.Id)
        {
            return;
        }

        if (message.Type == FoundryWorkerProtocol.MessageTypes.Progress)
        {
            if (!Enum.TryParse(message.Stage, ignoreCase: true, out FoundryProgressStage stage))
            {
                stage = FoundryProgressStage.Initializing;
            }

            pending.Progress?.Report(new FoundryProgress(stage, message.Detail ?? string.Empty, message.Percent));
            return;
        }

        pending.Terminal.TrySetResult(message);
    }

    private void OnWorkerExited(Process process)
    {
        if (!ReferenceEquals(_process, process))
        {
            return;
        }

        _standardInput = null;
        _loadedModelAlias = null;
        var exitCode = SafeExitCode(process);
        logger.Warning($"Foundry worker exited; pid={SafeProcessId(process)}; exitCode={exitCode}");
        _pending?.Terminal.TrySetException(new FoundryWorkerException($"The Foundry Local worker exited unexpectedly ({exitCode})."));
    }

    private async Task SendLineAsync(FoundryWorkerRequest request)
    {
        var json = JsonSerializer.Serialize(request, FoundryWorkerProtocol.JsonOptions);
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var input = _standardInput ?? throw new FoundryWorkerException("The Foundry worker input stream is unavailable.");
            await input.WriteLineAsync(json).ConfigureAwait(false);
            await input.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendCancelAsync(int requestId)
    {
        try
        {
            await SendLineAsync(new FoundryWorkerRequest
            {
                Operation = FoundryWorkerProtocol.Operations.Cancel,
                Id = requestId
            }).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task ResetWorkerCoreAsync(CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var process = _process;
            _process = null;
            _standardInput = null;
            _ready = null;
            _loadedModelAlias = null;
            _pending?.Terminal.TrySetException(new FoundryWorkerException("The Foundry worker was reset."));
            if (process is null)
            {
                return;
            }

            logger.Info($"Resetting exact Foundry worker process; pid={SafeProcessId(process)}");
            KillExactWorker(process);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }

            await Task.Delay(ResetSettleDelay, cancellationToken).ConfigureAwait(false);
            process.Dispose();
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private static string RequireModelAlias(string modelAlias)
    {
        return string.IsNullOrWhiteSpace(modelAlias)
            ? throw new ArgumentException("Select a Foundry Local model first.", nameof(modelAlias))
            : modelAlias.Trim();
    }

    private static string ResolveWorkerPath()
    {
        var overridePath = Environment.GetEnvironmentVariable(WorkerEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        return Path.Combine(AppContext.BaseDirectory, "foundry-worker", "EverythingDiskUsage.FoundryWorker.exe");
    }

    private static void KillExactWorker(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static int SafeProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch
        {
            return -1;
        }
    }

    private static string SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return "unknown";
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var process = _process;
        _process = null;
        _standardInput = null;
        if (process is { HasExited: false })
        {
            try
            {
                var request = new FoundryWorkerRequest { Operation = FoundryWorkerProtocol.Operations.Shutdown };
                process.StandardInput.WriteLine(JsonSerializer.Serialize(request, FoundryWorkerProtocol.JsonOptions));
                process.StandardInput.Flush();
                if (!process.WaitForExit(1500))
                {
                    KillExactWorker(process);
                }
            }
            catch
            {
                KillExactWorker(process);
            }
        }

        process?.Dispose();
        _pending?.Terminal.TrySetException(new ObjectDisposedException(nameof(FoundryWorkerClient)));
        _operationLock.Dispose();
        _lifecycleLock.Dispose();
        _sendLock.Dispose();
    }

    private sealed class PendingRequest(int id, IProgress<FoundryProgress>? progress)
    {
        public int Id { get; } = id;

        public IProgress<FoundryProgress>? Progress { get; } = progress;

        public TaskCompletionSource<FoundryWorkerMessage> Terminal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed class FoundryWorkerException(string message) : Exception(message);
