namespace EverythingDiskUsage.Models;

public sealed record FileUsageItem(
    string Name,
    string FullPath,
    string DirectoryPath,
    long SizeBytes,
    DateTime? LastModifiedUtc,
    DateTime? LastAccessedUtc)
{
    public string SizeText => DirectoryUsageNode.FormatBytes(SizeBytes);

    public string LastModifiedText => FormatDate(LastModifiedUtc);

    public string LastAccessedText => FormatDate(LastAccessedUtc);

    private static string FormatDate(DateTime? utcDate)
    {
        return utcDate is null ? string.Empty : utcDate.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }
}
