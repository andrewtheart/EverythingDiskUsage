namespace EverythingDiskUsage.Services.Foundry;

public interface IFoundryLocalModelService
{
    Task<IReadOnlyList<FoundryModelOption>> ListModelsAsync(
        IProgress<FoundryProgress>? progress,
        CancellationToken cancellationToken);

    Task PrepareModelAsync(
        string modelAlias,
        IProgress<FoundryProgress>? progress,
        CancellationToken cancellationToken);

    Task<DuplicateRecommendationResult> RecommendAsync(
        DuplicateRecommendationRequest request,
        string modelAlias,
        TimeSpan timeout,
        IProgress<FoundryProgress>? progress,
        CancellationToken cancellationToken);

    Task ResetAsync(CancellationToken cancellationToken);
}
