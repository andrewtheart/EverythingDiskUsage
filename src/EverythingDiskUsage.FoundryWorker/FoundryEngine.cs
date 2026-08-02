using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using EverythingDiskUsage.Services.Foundry;
using Microsoft.AI.Foundry.Local;
using System.Diagnostics;
using System.Text.Json;

namespace EverythingDiskUsage.FoundryWorker;

internal sealed class FoundryEngine
{
    private const string AppName = "EverythingDiskUsage";
    private const string SystemPrompt = """
        You are a conservative duplicate-file advisor running locally on the user's computer.
        The files were matched only by identical file name and byte size, not by a content hash.
        Never claim that two files have identical content. Never instruct the application to delete files.

        Return exactly one JSON object with this schema:
        {
          "summary": "short overall assessment",
          "safetyNote": "must mention name-and-size matching and manual verification before deletion",
          "decisions": [
            {
              "path": "an exact path copied from the input",
              "action": "Keep|DeleteCandidate|Review",
              "confidence": 0.0,
              "reason": "brief path-based rationale"
            }
          ]
        }

        Rules:
        - Return exactly one decision for every supplied candidate, in input order.
        - Copy each path byte-for-byte from the input. Never invent, normalize, shorten, or omit a path.
        - Keep means the path appears to be the canonical copy.
        - DeleteCandidate means a path appears redundant because it is generated output, a cache, or a temporary extraction. It remains advice only.
        - Review means evidence is ambiguous, the copy may be an intentional backup/archive, or any path is managed by Windows or an installed application.
        - Never mark Windows, Program Files, ProgramData, system, package-store, cloud-sync, backup, or archive paths as DeleteCandidate without unambiguous user context.
        - If the supplied context says a backup may be intentional or requires review, use Review.
        - Base decisions only on supplied metadata and path semantics. Do not assume file contents.
        - Confidence must be a finite number from 0 through 1.
        - Output JSON only. Do not wrap it in Markdown or return ordinary text.
        """;

    private static readonly string[] PreferredAliases =
        ["phi-4-mini", "phi-3.5-mini", "qwen2.5-3b", "qwen2.5-1.5b", "phi-3-mini", "phi-4", "mistral-7b"];

    private ICatalog? _catalog;
    private IModel? _model;
    private OpenAIChatClient? _chatClient;

    public string? CurrentModelKey => _model?.Id ?? _model?.Alias;

    public async Task<IReadOnlyList<WorkerModelOption>> ListModelsAsync(
        Action<WorkerProgress> report,
        CancellationToken cancellationToken)
    {
        var catalog = await EnsureCatalogAsync(report, cancellationToken).ConfigureAwait(false);
        var models = await catalog.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        if (models is null)
        {
            return [];
        }

        var candidates = models
            .Where(model => model is not null && IsTextChatModel(AliasOf(model), model.Info?.Task))
            .Select(model => model!)
            .ToList();
        var recommendedAlias = candidates
            .OrderBy(model => Rank(AliasOf(model)))
            .ThenBy(model => model.Info?.FileSizeMb ?? int.MaxValue)
            .Select(AliasOf)
            .FirstOrDefault();

        var options = new List<WorkerModelOption>(candidates.Count);
        foreach (var model in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var alias = AliasOf(model);
            var cached = await model.IsCachedAsync(cancellationToken).ConfigureAwait(false);
            var sizeMb = model.Info?.FileSizeMb;
            options.Add(new WorkerModelOption(
                alias,
                alias,
                model.Id,
                sizeMb is > 0 ? sizeMb.Value * 1024L * 1024L : null,
                cached,
                alias.Equals(recommendedAlias, StringComparison.OrdinalIgnoreCase)));
        }

        return options
            .OrderByDescending(option => option.IsRecommended)
            .ThenByDescending(option => option.IsCached)
            .ThenBy(option => Rank(option.Alias))
            .ThenBy(option => option.SizeBytes ?? long.MaxValue)
            .ToList();
    }

    public async Task PrepareModelAsync(
        string modelAlias,
        Action<WorkerProgress> report,
        CancellationToken cancellationToken)
    {
        if (_chatClient is not null &&
            (_model?.Alias?.Equals(modelAlias, StringComparison.OrdinalIgnoreCase) == true ||
             _model?.Id?.Equals(modelAlias, StringComparison.OrdinalIgnoreCase) == true))
        {
            return;
        }

        if (_model is not null)
        {
            throw new InvalidOperationException("A different model is already loaded. Reset the worker before changing models.");
        }

        var catalog = await EnsureCatalogAsync(report, cancellationToken).ConfigureAwait(false);
        var model = await catalog.GetModelAsync(modelAlias, cancellationToken).ConfigureAwait(false) ??
            await catalog.GetModelVariantAsync(modelAlias, cancellationToken).ConfigureAwait(false) ??
            throw new InvalidOperationException($"Foundry Local model '{modelAlias}' was not found.");

        var cached = await model.IsCachedAsync(cancellationToken).ConfigureAwait(false);
        if (!cached)
        {
            report(new WorkerProgress("DownloadingModel", model.Alias ?? modelAlias, 0));
        }

        await model.DownloadAsync(
            percent =>
            {
                if (!cached)
                {
                    report(new WorkerProgress("DownloadingModel", model.Alias ?? modelAlias, percent));
                }
            },
            cancellationToken).ConfigureAwait(false);

        report(new WorkerProgress("LoadingModel", model.Alias ?? modelAlias));
        _model = model;
        await model.LoadAsync(cancellationToken).ConfigureAwait(false);
        _chatClient = await model.GetChatClientAsync(cancellationToken).ConfigureAwait(false);
        ConfigureChatSettings(_chatClient, model.Alias ?? modelAlias);
    }

    public async Task<string> RecommendAsync(
        string payloadJson,
        string modelAlias,
        int timeoutSeconds,
        Action<WorkerProgress> report,
        CancellationToken cancellationToken)
    {
        await PrepareModelAsync(modelAlias, report, cancellationToken).ConfigureAwait(false);
        var request = JsonSerializer.Deserialize<WorkerDuplicateRequest>(payloadJson, FoundryWorkerProtocol.JsonOptions) ??
            throw new InvalidDataException("The duplicate recommendation request was invalid.");
        if (request.Candidates is null || request.Candidates.Count is < 2 or > 100)
        {
            throw new InvalidDataException("The duplicate recommendation request must contain 2 to 100 candidates.");
        }

        report(new WorkerProgress("Analyzing", $"Analyzing {request.Candidates.Count} paths with {_model?.Alias ?? modelAlias}"));
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 10, 600)));
        var messages = new List<ChatMessage>
        {
            ChatMessage.FromSystem(SystemPrompt),
            ChatMessage.FromUser(payloadJson)
        };
        var stopwatch = Stopwatch.StartNew();
        var response = await _chatClient!.CompleteChatAsync(messages, timeoutSource.Token).ConfigureAwait(false);
        stopwatch.Stop();
        Console.Error.WriteLine($"Model '{CurrentModelKey}' completed duplicate advice in {stopwatch.ElapsedMilliseconds} ms.");
        return response?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
    }

    private static void ConfigureChatSettings(OpenAIChatClient chatClient, string alias)
    {
        var settings = chatClient.Settings;
        var reasoning = alias.Contains("reason", StringComparison.OrdinalIgnoreCase) ||
            alias.Contains("deepseek-r1", StringComparison.OrdinalIgnoreCase);
        settings.Temperature = reasoning ? 0.7f : 0f;
        settings.TopP = reasoning ? 0.95f : 1f;
        settings.MaxTokens = reasoning ? 4096 : 512;
        settings.RandomSeed = 0;
        settings.FrequencyPenalty = reasoning ? 0f : 0.6f;
        settings.PresencePenalty = reasoning ? 0f : 0.3f;
        settings.ResponseFormat = new Microsoft.AI.Foundry.Local.OpenAI.ResponseFormatExtended { Type = "json_object" };
    }

    private async Task<ICatalog> EnsureCatalogAsync(
        Action<WorkerProgress> report,
        CancellationToken cancellationToken)
    {
        if (_catalog is not null)
        {
            return _catalog;
        }

        report(new WorkerProgress("Initializing", "Initializing Foundry Local"));
        if (!FoundryLocalManager.IsInitialized)
        {
            await FoundryLocalManager.CreateAsync(
                new Configuration { AppName = AppName },
                FoundryStderrLogger.Instance,
                cancellationToken).ConfigureAwait(false);
        }

        var manager = FoundryLocalManager.Instance;
        await manager.DownloadAndRegisterEpsAsync(
            (name, percent) =>
            {
                if (percent < 100)
                {
                    report(new WorkerProgress("DownloadingRuntime", name, percent));
                }
            },
            cancellationToken).ConfigureAwait(false);
        _catalog = await manager.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        return _catalog;
    }

    private static bool IsTextChatModel(string alias, string? task)
    {
        var value = $"{alias} {task}";
        return !new[] { "embed", "audio", "transcription", "whisper", "speech", "vision", "image", "rerank" }
            .Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static string AliasOf(IModel model)
    {
        return !string.IsNullOrWhiteSpace(model.Alias) ? model.Alias : model.Info?.Alias ?? model.Id;
    }

    private static int Rank(string alias)
    {
        for (var index = 0; index < PreferredAliases.Length; index++)
        {
            if (alias.Contains(PreferredAliases[index], StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    internal sealed record WorkerProgress(string Stage, string Detail, double? Percent = null);

    internal sealed record WorkerModelOption(
        string Alias,
        string DisplayName,
        string? Id,
        long? SizeBytes,
        bool IsCached,
        bool IsRecommended);

    private sealed class WorkerDuplicateRequest
    {
        public string? FileName { get; set; }

        public long SizeBytes { get; set; }

        public List<WorkerDuplicateCandidate>? Candidates { get; set; }

        public string? Context { get; set; }
    }

    private sealed class WorkerDuplicateCandidate
    {
        public string? Path { get; set; }

        public string? DirectoryPath { get; set; }

        public long SizeBytes { get; set; }

        public DateTime? LastModifiedUtc { get; set; }

        public DateTime? LastAccessedUtc { get; set; }
    }
}
