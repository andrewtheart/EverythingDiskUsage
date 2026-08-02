using System.IO;
using System.Text.Json;

namespace EverythingDiskUsage.Services.Foundry;

public static class DuplicateRecommendationValidator
{
    private const string VerificationWarning = "Matches are based on file name and size, not a content hash. Verify contents before deleting anything.";

    public static DuplicateRecommendationResult Validate(
        string rawModelOutput,
        DuplicateRecommendationRequest request,
        string model)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Candidates.Count < 2)
        {
            throw new InvalidDataException("At least two duplicate candidates are required.");
        }

        var candidatePaths = new HashSet<string>(
            request.Candidates.Select(candidate => candidate.Path),
            StringComparer.OrdinalIgnoreCase);
        if (candidatePaths.Count != request.Candidates.Count)
        {
            throw new InvalidDataException("Duplicate candidate paths must be unique.");
        }

        var json = ExtractJsonObject(rawModelOutput);
        RawRecommendation? raw;
        try
        {
            raw = JsonSerializer.Deserialize<RawRecommendation>(json, FoundryWorkerProtocol.JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The model did not return valid recommendation JSON.", exception);
        }

        if (raw is null || string.IsNullOrWhiteSpace(raw.Summary) || string.IsNullOrWhiteSpace(raw.SafetyNote))
        {
            throw new InvalidDataException("The model response must include a summary and safety note.");
        }

        if (raw.Decisions is null || raw.Decisions.Count != candidatePaths.Count)
        {
            throw new InvalidDataException("The model must return exactly one decision for every supplied path.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var decisions = new List<DuplicateRecommendationDecision>(raw.Decisions.Count);
        foreach (var decision in raw.Decisions)
        {
            var path = decision.Path?.Trim();
            if (string.IsNullOrWhiteSpace(path) || !candidatePaths.Contains(path))
            {
                throw new InvalidDataException($"The model referenced an unknown path: '{path ?? "<empty>"}'.");
            }

            if (!seen.Add(path))
            {
                throw new InvalidDataException($"The model returned more than one decision for '{path}'.");
            }

            if (!Enum.TryParse(decision.Action, ignoreCase: true, out DuplicateRecommendationAction action))
            {
                throw new InvalidDataException($"The model returned an unsupported action for '{path}'.");
            }

            if (!double.IsFinite(decision.Confidence) || decision.Confidence is < 0 or > 1)
            {
                throw new InvalidDataException($"The model returned an invalid confidence for '{path}'.");
            }

            if (string.IsNullOrWhiteSpace(decision.Reason))
            {
                throw new InvalidDataException($"The model returned no reason for '{path}'.");
            }

            var safetyAdjusted = action == DuplicateRecommendationAction.DeleteCandidate && IsProtectedPath(path);
            decisions.Add(new DuplicateRecommendationDecision(
                path,
                safetyAdjusted ? DuplicateRecommendationAction.Review : action,
                decision.Confidence,
                safetyAdjusted
                    ? $"Protected Windows or application path. Review manually. Model rationale: {decision.Reason.Trim()}"
                    : decision.Reason.Trim(),
                safetyAdjusted));
        }

        if (seen.Count != candidatePaths.Count)
        {
            throw new InvalidDataException("The model omitted one or more supplied paths.");
        }

        var safetyNote = raw.SafetyNote.Trim();
        if (!safetyNote.Contains("name", StringComparison.OrdinalIgnoreCase) ||
            !safetyNote.Contains("size", StringComparison.OrdinalIgnoreCase))
        {
            safetyNote = $"{safetyNote} {VerificationWarning}";
        }

        return new DuplicateRecommendationResult(
            string.IsNullOrWhiteSpace(model) ? "Local model" : model,
            raw.Summary.Trim(),
            safetyNote,
            decisions);
    }

    private static string ExtractJsonObject(string rawModelOutput)
    {
        if (string.IsNullOrWhiteSpace(rawModelOutput))
        {
            throw new InvalidDataException("The model returned an empty response.");
        }

        var withoutThinking = rawModelOutput;
        var thinkEnd = withoutThinking.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (thinkEnd >= 0)
        {
            withoutThinking = withoutThinking[(thinkEnd + "</think>".Length)..];
        }

        var firstBrace = withoutThinking.IndexOf('{');
        var lastBrace = withoutThinking.LastIndexOf('}');
        if (firstBrace < 0 || lastBrace <= firstBrace)
        {
            throw new InvalidDataException("The model response did not contain a JSON object.");
        }

        return withoutThinking[firstBrace..(lastBrace + 1)];
    }

    private static bool IsProtectedPath(string path)
    {
        var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var protectedRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        };

        return protectedRoots.Any(root =>
            !string.IsNullOrWhiteSpace(root) &&
            (normalized.Equals(root, StringComparison.OrdinalIgnoreCase) ||
             normalized.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
    }

    private sealed class RawRecommendation
    {
        public string? Summary { get; set; }

        public string? SafetyNote { get; set; }

        public List<RawDecision>? Decisions { get; set; }
    }

    private sealed class RawDecision
    {
        public string? Path { get; set; }

        public string? Action { get; set; }

        public double Confidence { get; set; }

        public string? Reason { get; set; }
    }
}
