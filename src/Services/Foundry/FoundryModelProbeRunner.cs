using System.IO;

namespace EverythingDiskUsage.Services.Foundry;

public sealed class FoundryModelProbeRunner(IFoundryLocalModelService modelService)
{
    private static readonly IReadOnlyList<ProbeScenario> Scenarios =
    [
        new(
            "Source vs build output",
            "A source-controlled project asset and a generated build-output copy. Prefer the source asset and mark the build output as a delete candidate.",
            Candidate(@"C:\Projects\PhotoTool\src\logo.png"),
            Candidate(@"C:\Projects\PhotoTool\bin\Release\logo.png"),
            DuplicateRecommendationAction.Keep,
            DuplicateRecommendationAction.DeleteCandidate),
        new(
            "Work copy vs temporary extraction",
            "A user's working spreadsheet and a temporary extracted copy. Prefer the working copy and mark the temporary extraction as a delete candidate.",
            Candidate(@"C:\Users\Example\Documents\Quarterly\report.xlsx"),
            Candidate(@"C:\Users\Example\AppData\Local\Temp\extract-4821\report.xlsx"),
            DuplicateRecommendationAction.Keep,
            DuplicateRecommendationAction.DeleteCandidate),
        new(
            "Windows-managed copies",
            "Both paths are managed by Windows. Do not suggest deleting either path; require manual review for both.",
            Candidate(@"C:\Windows\System32\example.dll"),
            Candidate(@"C:\Windows\WinSxS\amd64_example\example.dll"),
            DuplicateRecommendationAction.Review,
            DuplicateRecommendationAction.Review),
        new(
            "Intentional backup ambiguity",
            "One copy is in active documents and one is in a dated backup. The backup may be intentional, so require manual review for both rather than suggesting deletion.",
            Candidate(@"C:\Users\Example\Documents\Tax\return.pdf"),
            Candidate(@"D:\Backups\2025-12-31\Tax\return.pdf"),
            DuplicateRecommendationAction.Review,
            DuplicateRecommendationAction.Review)
    ];

    public async Task<FoundryProbeReport> RunAsync(
        string modelAlias,
        TimeSpan timeoutPerScenario,
        IProgress<FoundryProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(modelAlias))
        {
            throw new ArgumentException("Select a model before running its probe.", nameof(modelAlias));
        }

        await modelService.PrepareModelAsync(modelAlias, progress, cancellationToken).ConfigureAwait(false);
        var results = new List<FoundryProbeScenarioResult>(Scenarios.Count);
        foreach (var scenario in Scenarios)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new FoundryProgress(FoundryProgressStage.Analyzing, $"Probe: {scenario.Name}"));

            try
            {
                var request = new DuplicateRecommendationRequest(
                    Path.GetFileName(scenario.First.Path),
                    scenario.First.SizeBytes,
                    [scenario.First, scenario.Second],
                    scenario.Context);
                var recommendation = await modelService.RecommendAsync(
                    request,
                    modelAlias,
                    timeoutPerScenario,
                    progress,
                    cancellationToken).ConfigureAwait(false);

                var first = recommendation.Decisions.Single(decision =>
                    decision.Path.Equals(scenario.First.Path, StringComparison.OrdinalIgnoreCase));
                var second = recommendation.Decisions.Single(decision =>
                    decision.Path.Equals(scenario.Second.Path, StringComparison.OrdinalIgnoreCase));
                var passed = first.Action == scenario.FirstExpected &&
                    second.Action == scenario.SecondExpected &&
                    !first.WasSafetyAdjusted &&
                    !second.WasSafetyAdjusted;
                var detail = passed
                    ? $"Returned {first.Action} / {second.Action} without safety correction."
                    : $"Expected {scenario.FirstExpected} / {scenario.SecondExpected}; returned {first.Action} / {second.Action}.";
                results.Add(new FoundryProbeScenarioResult(scenario.Name, passed, detail));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                results.Add(new FoundryProbeScenarioResult(scenario.Name, false, exception.Message));
            }
        }

        return new FoundryProbeReport(modelAlias.Trim(), DateTimeOffset.Now, results);
    }

    private static DuplicateRecommendationCandidate Candidate(string path)
    {
        return new DuplicateRecommendationCandidate(
            path,
            Path.GetDirectoryName(path) ?? string.Empty,
            1024,
            new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 16, 12, 0, 0, DateTimeKind.Utc));
    }

    private sealed record ProbeScenario(
        string Name,
        string Context,
        DuplicateRecommendationCandidate First,
        DuplicateRecommendationCandidate Second,
        DuplicateRecommendationAction FirstExpected,
        DuplicateRecommendationAction SecondExpected);
}
