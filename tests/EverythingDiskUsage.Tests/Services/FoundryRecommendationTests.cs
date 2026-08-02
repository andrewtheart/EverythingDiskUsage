using EverythingDiskUsage.Services.Foundry;
using System.IO;
using System.Text.Json;

namespace EverythingDiskUsage.Tests.Services;

public sealed class FoundryRecommendationTests
{
    [Fact]
    public void Validate_AcceptsCompleteStructuredRecommendation()
    {
        var request = Request(@"C:\Work\a.txt", @"C:\Temp\a.txt");
        var raw = JsonSerializer.Serialize(new
        {
            summary = "The work copy appears canonical.",
            safetyNote = "Matching uses name and size only; verify both files.",
            decisions = new object[]
            {
                new { path = @"C:\Work\a.txt", action = "Keep", confidence = 0.92, reason = "Working location." },
                new { path = @"C:\Temp\a.txt", action = "DeleteCandidate", confidence = 0.86, reason = "Temporary location." }
            }
        });

        var result = DuplicateRecommendationValidator.Validate(raw, request, "test-model");

        Assert.Equal("test-model", result.Model);
        Assert.Equal(2, result.Decisions.Count);
        Assert.Equal(DuplicateRecommendationAction.Keep, result.Decisions[0].Action);
        Assert.Equal(DuplicateRecommendationAction.DeleteCandidate, result.Decisions[1].Action);
        Assert.All(result.Decisions, decision => Assert.False(decision.WasSafetyAdjusted));
    }

    [Fact]
    public void Validate_RejectsInventedPath()
    {
        var request = Request(@"C:\Work\a.txt", @"C:\Temp\a.txt");
        var raw = """
            {
              "summary": "Unsafe response",
              "safetyNote": "Matched by name and size.",
              "decisions": [
                { "path": "C:\\Work\\a.txt", "action": "Keep", "confidence": 0.9, "reason": "Work" },
                { "path": "C:\\Invented\\a.txt", "action": "DeleteCandidate", "confidence": 0.9, "reason": "Invented" }
              ]
            }
            """;

        var exception = Assert.Throws<InvalidDataException>(() =>
            DuplicateRecommendationValidator.Validate(raw, request, "test-model"));

        Assert.Contains("unknown path", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_DowngradesProtectedPathDeletionForManualReview()
    {
        var windowsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "example.dll");
        var otherPath = @"C:\Temp\example.dll";
        var request = Request(windowsPath, otherPath);
        var raw = JsonSerializer.Serialize(new
        {
            summary = "One path is protected.",
            safetyNote = "Matched by name and size only.",
            decisions = new object[]
            {
                new { path = windowsPath, action = "DeleteCandidate", confidence = 0.8, reason = "Incorrect model advice." },
                new { path = otherPath, action = "Keep", confidence = 0.7, reason = "Other copy." }
            }
        });

        var result = DuplicateRecommendationValidator.Validate(raw, request, "test-model");
        var protectedDecision = Assert.Single(result.Decisions, decision => decision.Path == windowsPath);

        Assert.Equal(DuplicateRecommendationAction.Review, protectedDecision.Action);
        Assert.True(protectedDecision.WasSafetyAdjusted);
    }

    [Fact]
    public async Task ProbeRunner_VerifiesAllDeterministicScenarios()
    {
        var service = new ProbeModelService();
        var runner = new FoundryModelProbeRunner(service);

        var report = await runner.RunAsync("probe-model", TimeSpan.FromSeconds(10), null, CancellationToken.None);

        Assert.True(report.Passed);
        Assert.Equal(4, report.Scenarios.Count);
        Assert.Equal(4, service.RecommendationCount);
        Assert.Equal("probe-model", service.PreparedModel);
    }

    private static DuplicateRecommendationRequest Request(string firstPath, string secondPath)
    {
        return new DuplicateRecommendationRequest(
            Path.GetFileName(firstPath),
            1024,
            [Candidate(firstPath), Candidate(secondPath)]);
    }

    private static DuplicateRecommendationCandidate Candidate(string path)
    {
        return new DuplicateRecommendationCandidate(path, Path.GetDirectoryName(path) ?? string.Empty, 1024, null, null);
    }

    private sealed class ProbeModelService : IFoundryLocalModelService
    {
        public int RecommendationCount { get; private set; }

        public string? PreparedModel { get; private set; }

        public Task<IReadOnlyList<FoundryModelOption>> ListModelsAsync(IProgress<FoundryProgress>? progress, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<FoundryModelOption>>([]);
        }

        public Task PrepareModelAsync(string modelAlias, IProgress<FoundryProgress>? progress, CancellationToken cancellationToken)
        {
            PreparedModel = modelAlias;
            return Task.CompletedTask;
        }

        public Task<DuplicateRecommendationResult> RecommendAsync(
            DuplicateRecommendationRequest request,
            string modelAlias,
            TimeSpan timeout,
            IProgress<FoundryProgress>? progress,
            CancellationToken cancellationToken)
        {
            RecommendationCount++;
            var context = request.Context ?? string.Empty;
            var actions = context.Contains("source-controlled", StringComparison.OrdinalIgnoreCase) ||
                context.Contains("temporary extracted", StringComparison.OrdinalIgnoreCase)
                ? new[] { DuplicateRecommendationAction.Keep, DuplicateRecommendationAction.DeleteCandidate }
                : new[] { DuplicateRecommendationAction.Review, DuplicateRecommendationAction.Review };
            var decisions = request.Candidates.Select((candidate, index) =>
                new DuplicateRecommendationDecision(candidate.Path, actions[index], 0.9, "Probe response.")).ToList();
            return Task.FromResult(new DuplicateRecommendationResult(modelAlias, "Probe", "Matched by name and size.", decisions));
        }

        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
