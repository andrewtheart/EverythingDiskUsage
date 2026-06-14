namespace EverythingDiskUsage.Models;

public sealed record FileDetailRow(
    string Name,
    string PathText,
    string? ShellItemPath,
    int FileCount,
    long SizeBytes,
    DateTime? LastModifiedUtc,
    DateTime? LastAccessedUtc,
    bool IsGroup,
    bool DatesAreMultiple)
{
    public string DisplayName => IsGroup ? Name : "    " + Name;

    public string FileCountText => FileCount.ToString("N0");

    public string SizeText => DirectoryUsageNode.FormatBytes(SizeBytes);

    public string LastModifiedText => FormatDate(LastModifiedUtc, DatesAreMultiple);

    public string LastAccessedText => FormatDate(LastAccessedUtc, DatesAreMultiple);

    public static FileDetailRow FromFile(FileUsageItem file)
    {
        return new FileDetailRow(
            file.Name,
            file.DirectoryPath,
            file.FullPath,
            1,
            file.SizeBytes,
            file.LastModifiedUtc,
            file.LastAccessedUtc,
            IsGroup: false,
            DatesAreMultiple: false);
    }

    public static FileDetailRow FromGroup(
        string name,
        string pathText,
        int fileCount,
        long sizeBytes,
        DateTime? lastModifiedUtc,
        DateTime? lastAccessedUtc)
    {
        return new FileDetailRow(
            name,
            pathText,
            fileCount == 1 && pathText != "[multiple]" ? System.IO.Path.Combine(pathText, name) : null,
            fileCount,
            sizeBytes,
            fileCount > 1 ? null : lastModifiedUtc,
            fileCount > 1 ? null : lastAccessedUtc,
            IsGroup: true,
            DatesAreMultiple: fileCount > 1);
    }

    private static string FormatDate(DateTime? utcDate, bool datesAreMultiple)
    {
        if (datesAreMultiple)
        {
            return "[multiple]";
        }

        return utcDate is null ? string.Empty : utcDate.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }
}
