namespace EverythingDiskUsage.Models;

/// <summary>
/// A row in the Duplicates grid. Either a group header (IsGroup=true) that summarises
/// one set of same-name/same-size files, or an individual file occurrence (IsGroup=false).
/// </summary>
public sealed record DuplicateFileRow(
    string Name,
    string PathText,
    string? ShellItemPath,
    int CopyCount,
    long SizeBytes,
    long WastedBytes,
    bool IsGroup)
{
    /// <summary>Individual file rows are indented so they visually nest under the group header.</summary>
    public string DisplayName => IsGroup ? Name : "    " + Name;

    /// <summary>Number of copies — shown on group headers only.</summary>
    public string CopyCountText => IsGroup ? CopyCount.ToString("N0") : string.Empty;

    /// <summary>Per-file size.</summary>
    public string SizeText => DirectoryUsageNode.FormatBytes(SizeBytes);

    /// <summary>Bytes that could be freed by deleting all but one copy — shown on group headers only.</summary>
    public string WastedText => IsGroup ? DirectoryUsageNode.FormatBytes(WastedBytes) : string.Empty;
}
