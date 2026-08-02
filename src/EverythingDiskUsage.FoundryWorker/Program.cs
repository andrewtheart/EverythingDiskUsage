using EverythingDiskUsage.Services.Foundry;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace EverythingDiskUsage.FoundryWorker;

internal static class Program
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly object OutputLock = new();
    private static readonly SemaphoreSlim WorkLock = new(1, 1);
    private static readonly ConcurrentDictionary<int, CancellationTokenSource> InFlight = new();

    private static TextWriter _protocolOutput = TextWriter.Null;
    private static readonly FoundryEngine Engine = new();

    private static async Task<int> Main()
    {
        _protocolOutput = new StreamWriter(Console.OpenStandardOutput(), Utf8NoBom) { AutoFlush = false };
        Console.SetOut(Console.Error);
        using var input = new StreamReader(Console.OpenStandardInput(), Utf8NoBom);

        Send(new FoundryWorkerMessage { Type = FoundryWorkerProtocol.MessageTypes.Ready, Ok = true });

        string? line;
        while ((line = await input.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            line = line.Trim('\uFEFF', '\u200B').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            FoundryWorkerRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<FoundryWorkerRequest>(line, FoundryWorkerProtocol.JsonOptions);
            }
            catch (JsonException exception)
            {
                Console.Error.WriteLine($"Invalid worker request: {exception.Message}");
                continue;
            }

            if (request is null)
            {
                continue;
            }

            if (request.Operation == FoundryWorkerProtocol.Operations.Shutdown)
            {
                break;
            }

            if (request.Operation == FoundryWorkerProtocol.Operations.Cancel)
            {
                if (InFlight.TryGetValue(request.Id, out var source))
                {
                    source.Cancel();
                }

                continue;
            }

            _ = HandleAsync(request);
        }

        return 0;
    }

    private static async Task HandleAsync(FoundryWorkerRequest request)
    {
        using var cancellationSource = new CancellationTokenSource();
        InFlight[request.Id] = cancellationSource;
        await WorkLock.WaitAsync().ConfigureAwait(false);
        try
        {
            switch (request.Operation)
            {
                case FoundryWorkerProtocol.Operations.ListModels:
                    await HandleListModelsAsync(request, cancellationSource.Token).ConfigureAwait(false);
                    break;
                case FoundryWorkerProtocol.Operations.PrepareModel:
                    await Engine.PrepareModelAsync(
                        RequireModelAlias(request),
                        progress => SendProgress(request.Id, progress),
                        cancellationSource.Token).ConfigureAwait(false);
                    SendTerminal(FoundryWorkerProtocol.MessageTypes.Ack, request.Id, payloadJson: null);
                    break;
                case FoundryWorkerProtocol.Operations.Recommend:
                    await HandleRecommendAsync(request, cancellationSource.Token).ConfigureAwait(false);
                    break;
                default:
                    SendError(request.Id, $"Unknown operation '{request.Operation}'.");
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            SendError(request.Id, "The Foundry operation was cancelled.");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Operation '{request.Operation}' failed: {exception}");
            SendError(request.Id, exception.Message);
        }
        finally
        {
            WorkLock.Release();
            InFlight.TryRemove(request.Id, out _);
        }
    }

    private static async Task HandleListModelsAsync(FoundryWorkerRequest request, CancellationToken cancellationToken)
    {
        var models = await Engine.ListModelsAsync(
            progress => SendProgress(request.Id, progress),
            cancellationToken).ConfigureAwait(false);
        SendTerminal(
            FoundryWorkerProtocol.MessageTypes.Models,
            request.Id,
            JsonSerializer.Serialize(models, FoundryWorkerProtocol.JsonOptions));
    }

    private static async Task HandleRecommendAsync(FoundryWorkerRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.PayloadJson))
        {
            throw new InvalidDataException("The recommendation payload is missing.");
        }

        var output = await Engine.RecommendAsync(
            request.PayloadJson,
            RequireModelAlias(request),
            request.TimeoutSeconds,
            progress => SendProgress(request.Id, progress),
            cancellationToken).ConfigureAwait(false);
        SendTerminal(FoundryWorkerProtocol.MessageTypes.Result, request.Id, output);
    }

    private static string RequireModelAlias(FoundryWorkerRequest request)
    {
        return string.IsNullOrWhiteSpace(request.ModelAlias)
            ? throw new InvalidDataException("A model alias is required.")
            : request.ModelAlias;
    }

    private static void SendProgress(int id, FoundryEngine.WorkerProgress progress)
    {
        Send(new FoundryWorkerMessage
        {
            Type = FoundryWorkerProtocol.MessageTypes.Progress,
            Id = id,
            Ok = true,
            Stage = progress.Stage,
            Detail = progress.Detail,
            Percent = progress.Percent
        });
    }

    private static void SendTerminal(string type, int id, string? payloadJson)
    {
        Send(new FoundryWorkerMessage
        {
            Type = type,
            Id = id,
            Ok = true,
            PayloadJson = payloadJson,
            ModelKey = Engine.CurrentModelKey
        });
    }

    private static void SendError(int id, string error)
    {
        Send(new FoundryWorkerMessage
        {
            Type = FoundryWorkerProtocol.MessageTypes.Ack,
            Id = id,
            Ok = false,
            Error = error,
            ModelKey = Engine.CurrentModelKey
        });
    }

    private static void Send(FoundryWorkerMessage message)
    {
        var json = JsonSerializer.Serialize(message, FoundryWorkerProtocol.JsonOptions);
        lock (OutputLock)
        {
            _protocolOutput.WriteLine(json);
            _protocolOutput.Flush();
        }
    }
}
