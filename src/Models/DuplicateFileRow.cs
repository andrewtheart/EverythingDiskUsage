namespace EverythingDiskUsage.Models;

/// <summary>A single file occurrence within a same-name, same-size duplicate set.</summary>
public sealed record DuplicateFileRow(
    string DuplicateKey,
    string Name,
    string DirectoryPath,
    string ShellItemPath,
    int CopyCount,
    long SizeBytes,
    long WastedBytes)
{
    public string DisplayName => Name;

    public string PathText => DirectoryPath;

    public string DuplicateSetLabel =>
        $"{Name} \u00b7 {CopyCount:N0} copies \u00b7 {DirectoryUsageNode.FormatBytes(SizeBytes)} each \u00b7 {DirectoryUsageNode.FormatBytes(WastedBytes)} reclaimable";

    public string CopyCountText => CopyCount.ToString("N0");

    public string SizeText => DirectoryUsageNode.FormatBytes(SizeBytes);

    public string WastedText => DirectoryUsageNode.FormatBytes(WastedBytes);
}
