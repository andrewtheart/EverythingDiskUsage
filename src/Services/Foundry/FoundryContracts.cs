namespace EverythingDiskUsage.Services.Foundry;

public enum DuplicateRecommendationAction
{
    Keep,
    DeleteCandidate,
    Review
}

public enum FoundryProgressStage
{
    Initializing,
    DownloadingRuntime,
    DownloadingModel,
    LoadingModel,
    Analyzing
}

public sealed record FoundryProgress(
    FoundryProgressStage Stage,
    string Detail,
    double? Percent = null);

public sealed record FoundryModelOption(
    string Alias,
    string DisplayName,
    string? Id,
    long? SizeBytes,
    bool IsCached,
    bool IsRecommended)
{
    public string DisplayLabel
    {
        get
        {
            var parts = new List<string>();
            if (IsRecommended)
            {
                parts.Add("recommended");
            }

            if (IsCached)
            {
                parts.Add("downloaded");
            }

            if (SizeBytes is > 0)
            {
                parts.Add(Models.DirectoryUsageNode.FormatBytes(SizeBytes.Value));
            }

            return parts.Count == 0 ? DisplayName : $"{DisplayName} ({string.Join(", ", parts)})";
        }
    }
}

public sealed record DuplicateRecommendationCandidate(
    string Path,
    string DirectoryPath,
    long SizeBytes,
    DateTime? LastModifiedUtc,
    DateTime? LastAccessedUtc);

public sealed record DuplicateRecommendationRequest(
    string FileName,
    long SizeBytes,
    IReadOnlyList<DuplicateRecommendationCandidate> Candidates,
    string? Context = null);

public sealed record DuplicateRecommendationDecision(
    string Path,
    DuplicateRecommendationAction Action,
    double Confidence,
    string Reason,
    bool WasSafetyAdjusted = false)
{
    public string ActionText => Action switch
    {
        DuplicateRecommendationAction.DeleteCandidate => "Delete candidate",
        _ => Action.ToString()
    };

    public string ConfidenceText => $"{Confidence:P0}";
}

public sealed record DuplicateRecommendationResult(
    string Model,
    string Summary,
    string SafetyNote,
    IReadOnlyList<DuplicateRecommendationDecision> Decisions);

public sealed record FoundryProbeScenarioResult(
    string Name,
    bool Passed,
    string Detail);

public sealed record FoundryProbeReport(
    string Model,
    DateTimeOffset CompletedAt,
    IReadOnlyList<FoundryProbeScenarioResult> Scenarios)
{
    public bool Passed => Scenarios.Count > 0 && Scenarios.All(scenario => scenario.Passed);

    public string Summary => Passed
        ? $"Passed {Scenarios.Count}/{Scenarios.Count} checks"
        : $"Passed {Scenarios.Count(scenario => scenario.Passed)}/{Scenarios.Count} checks";
}
