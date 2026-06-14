namespace EverythingDiskUsage.Services;

public interface IDiskUsageAnalyzer
{
    Task<ScanResult> ScanAsync(string rootPath, IProgress<ScanProgress>? progress, CancellationToken cancellationToken);
}
