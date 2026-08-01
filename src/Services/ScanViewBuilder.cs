using EverythingDiskUsage.Models;
using System.IO;
using MediaColor = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace EverythingDiskUsage.Services;

public enum ShellItemKind
{
    File,
    Directory
}

public sealed record DuplicateRowsSnapshot(
    IReadOnlyList<DuplicateFileRow> Rows,
    int SourceFileCount,
    int TotalGroups,
    long TotalWastedBytes,
    int MaxGroups);

public sealed record FileDetailsSnapshot(
    IReadOnlyList<FileDetailRow> Rows,
    int GroupCount,
    string SummaryText);

public sealed record ScanViewSnapshot(
    DirectoryUsageNode Root,
    IReadOnlyList<FileUsageItem> Files,
    DuplicateRowsSnapshot Duplicates);

public static class ScanViewBuilder
{
    public const int MaxVisibleFileRows = 1000;
    public const int MaxDuplicateGroups = 500;

    private static readonly MediaColor[] SliceColors =
    [
        MediaColor.FromRgb(21, 122, 140),
        MediaColor.FromRgb(235, 159, 54),
        MediaColor.FromRgb(83, 139, 82),
        MediaColor.FromRgb(190, 80, 72),
        MediaColor.FromRgb(82, 116, 174),
        MediaColor.FromRgb(137, 104, 168),
        MediaColor.FromRgb(78, 163, 151),
        MediaColor.FromRgb(205, 119, 71),
        MediaColor.FromRgb(99, 121, 133),
        MediaColor.FromRgb(48, 64, 84)
    ];

    public static ScanViewSnapshot BuildScanViewSnapshot(string rootPath, IReadOnlyList<FileUsageItem> files)
    {
        return BuildScanViewSnapshot(rootPath, files, File.Exists);
    }

    public static ScanViewSnapshot BuildScanViewSnapshot(
        string rootPath,
        IReadOnlyList<FileUsageItem> files,
        Func<string, bool> fileExists)
    {
        var normalizedRoot = NormalizeDirectoryScope(rootPath);
        var existingFiles = SortFiles(files
            .Where(file => IsInsideRoot(file.FullPath, normalizedRoot) && fileExists(file.FullPath)))
            .ToList();
        var root = BuildRootFromFiles(normalizedRoot, existingFiles);
        var duplicates = BuildDuplicateSnapshot(existingFiles);
        return new ScanViewSnapshot(root, existingFiles, duplicates);
    }

    public static DirectoryUsageNode BuildRootFromFiles(string rootPath, IEnumerable<FileUsageItem> files)
    {
        var normalizedRoot = NormalizeDirectoryScope(rootPath);
        var root = new DirectoryUsageNode(GetRootDisplayName(normalizedRoot), normalizedRoot);

        foreach (var file in files)
        {
            TryAddFileToNodeTree(root, normalizedRoot, file);
        }

        root.FinalizeStats(root.SizeBytes);
        return root;
    }

    public static IEnumerable<DirectoryUsageNode> FlattenDirectories(DirectoryUsageNode root)
    {
        var pendingNodes = new Stack<DirectoryUsageNode>();
        pendingNodes.Push(root);

        while (pendingNodes.Count > 0)
        {
            var node = pendingNodes.Pop();
            yield return node;

            for (var childIndex = node.Children.Count - 1; childIndex >= 0; childIndex--)
            {
                pendingNodes.Push(node.Children[childIndex]);
            }
        }
    }

    public static DirectoryUsageNode? FindDirectoryByPath(DirectoryUsageNode root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return FlattenDirectories(root).FirstOrDefault(node => IsSameDirectory(node.FullPath, path));
    }

    public static DirectoryUsageNode? FindNearestExistingParentNode(DirectoryUsageNode root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var current = path;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var node = FindDirectoryByPath(root, current);
            if (node is not null)
            {
                return node;
            }

            var trimmed = current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Directory.GetParent(trimmed);
            if (parent is null)
            {
                return null;
            }

            current = parent.FullName;
        }

        return null;
    }

    public static FileDetailsSnapshot BuildVisibleFileDetails(
        DirectoryUsageNode node,
        IReadOnlyList<FileUsageItem> allFilesSorted,
        int maxVisibleRows = MaxVisibleFileRows)
    {
        if (allFilesSorted.Count == 0 || maxVisibleRows <= 0)
        {
            return new FileDetailsSnapshot(Array.Empty<FileDetailRow>(), GroupCount: 0, SummaryText: string.Empty);
        }

        var scopePath = NormalizeDirectoryScope(node.FullPath);
        var scopedFiles = allFilesSorted
            .Where(file => file.FullPath.StartsWith(scopePath, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var fileGroups = scopedFiles
            .GroupBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var groupFiles = group
                    .OrderByDescending(file => file.SizeBytes)
                    .ThenBy(file => file.DirectoryPath, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new
                {
                    Name = group.Key,
                    Files = groupFiles,
                    SizeBytes = groupFiles.Sum(file => file.SizeBytes),
                    LastModifiedUtc = MaxDate(groupFiles.Select(file => file.LastModifiedUtc)),
                    LastAccessedUtc = MaxDate(groupFiles.Select(file => file.LastAccessedUtc))
                };
            })
            .OrderByDescending(group => group.SizeBytes)
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = new List<FileDetailRow>();
        foreach (var group in fileGroups)
        {
            if (rows.Count >= maxVisibleRows)
            {
                break;
            }

            var pathText = group.Files.Count > 1 ? "[multiple]" : group.Files[0].DirectoryPath;
            rows.Add(FileDetailRow.FromGroup(
                group.Name,
                pathText,
                group.Files.Count,
                group.SizeBytes,
                group.LastModifiedUtc,
                group.LastAccessedUtc));

            if (group.Files.Count <= 1)
            {
                continue;
            }

            foreach (var file in group.Files)
            {
                if (rows.Count >= maxVisibleRows)
                {
                    break;
                }

                rows.Add(FileDetailRow.FromFile(file));
            }
        }

        var summaryText = rows.Count >= maxVisibleRows && node.FileCount > rows.Count
            ? $"Showing {rows.Count:N0} rows from {node.FileCount:N0} files"
            : $"{node.FileCount:N0} files";

        return new FileDetailsSnapshot(rows, fileGroups.Count, summaryText);
    }

    public static DuplicateRowsSnapshot BuildDuplicateSnapshot(IReadOnlyList<FileUsageItem> files)
    {
        var rows = new List<DuplicateFileRow>();
        if (files.Count == 0)
        {
            return new DuplicateRowsSnapshot(rows, files.Count, TotalGroups: 0, TotalWastedBytes: 0L, MaxDuplicateGroups);
        }

        var allGroups = files
            .Where(f => f.SizeBytes > 0)
            .GroupBy(f => (Name: f.Name.ToLowerInvariant(), f.SizeBytes))
            .Where(g => g.Count() >= 2)
            .Select(g =>
            {
                var groupFiles = g.OrderBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase).ToList();
                return new
                {
                    Name = groupFiles[0].Name,
                    g.Key.SizeBytes,
                    Files = groupFiles,
                    WastedBytes = (long)(groupFiles.Count - 1) * g.Key.SizeBytes
                };
            })
            .OrderByDescending(g => g.WastedBytes)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totalGroups = allGroups.Count;
        var totalWasted = allGroups.Sum(g => g.WastedBytes);

        foreach (var group in allGroups.Take(MaxDuplicateGroups))
        {
            rows.Add(new DuplicateFileRow(
                group.Name,
                $"{group.Files.Count} copies",
                ShellItemPath: null,
                group.Files.Count,
                group.SizeBytes,
                group.WastedBytes,
                IsGroup: true));

            foreach (var file in group.Files)
            {
                rows.Add(new DuplicateFileRow(
                    file.Name,
                    file.DirectoryPath,
                    file.FullPath,
                    CopyCount: 1,
                    file.SizeBytes,
                    WastedBytes: 0L,
                    IsGroup: false));
            }
        }

        return new DuplicateRowsSnapshot(rows, files.Count, totalGroups, totalWasted, MaxDuplicateGroups);
    }

    public static string GetDuplicateSummaryText(DuplicateRowsSnapshot snapshot)
    {
        if (snapshot.SourceFileCount == 0)
        {
            return string.Empty;
        }

        return snapshot.TotalGroups == 0
            ? "No duplicates found"
            : snapshot.TotalGroups > snapshot.MaxGroups
                ? $"Top {snapshot.MaxGroups:N0} of {snapshot.TotalGroups:N0} groups \u00b7 {DirectoryUsageNode.FormatBytes(snapshot.TotalWastedBytes)} wasted"
                : $"{snapshot.TotalGroups:N0} group{(snapshot.TotalGroups == 1 ? string.Empty : "s")} \u00b7 {DirectoryUsageNode.FormatBytes(snapshot.TotalWastedBytes)} wasted";
    }

    public static IReadOnlyList<PieSlice> BuildSlices(DirectoryUsageNode node)
    {
        if (node.SizeBytes <= 0)
        {
            return Array.Empty<PieSlice>();
        }

        var pieces = node.Children
            .Where(child => child.SizeBytes > 0)
            .Select(child => (Label: child.DisplayName, child.SizeBytes, Node: (DirectoryUsageNode?)child))
            .ToList();

        if (node.DirectFileSizeBytes > 0)
        {
            pieces.Add(("Files in this folder", node.DirectFileSizeBytes, null));
        }

        var orderedPieces = pieces
            .OrderByDescending(piece => piece.SizeBytes)
            .ToList();

        var visiblePieces = orderedPieces.Take(9).ToList();
        var otherBytes = orderedPieces.Skip(9).Sum(piece => piece.SizeBytes);
        if (otherBytes > 0)
        {
            visiblePieces.Add(("Other", otherBytes, null));
        }

        var slices = new List<PieSlice>(visiblePieces.Count);
        for (var i = 0; i < visiblePieces.Count; i++)
        {
            var brush = new SolidColorBrush(SliceColors[i % SliceColors.Length]);
            brush.Freeze();
            var percent = visiblePieces[i].SizeBytes * 100d / node.SizeBytes;
            var explorerNode = visiblePieces[i].Node ?? node;
            slices.Add(new PieSlice(visiblePieces[i].Label, visiblePieces[i].SizeBytes, percent, brush, visiblePieces[i].Node, explorerNode));
        }

        return slices;
    }

    public static IEnumerable<FileUsageItem> SortFiles(IEnumerable<FileUsageItem> files)
    {
        return files
            .OrderByDescending(file => file.SizeBytes)
            .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase);
    }

    public static bool TryMapRenamedFile(FileUsageItem file, string oldPath, string newPath, ShellItemKind itemKind, out FileUsageItem updatedFile)
    {
        if (!IsFileWithinShellItem(file.FullPath, oldPath, itemKind))
        {
            updatedFile = file;
            return false;
        }

        var trimmedOldPath = itemKind == ShellItemKind.Directory ? TrimDirectoryPath(oldPath) : oldPath;
        var trimmedNewPath = itemKind == ShellItemKind.Directory ? TrimDirectoryPath(newPath) : newPath;
        var newFullPath = itemKind == ShellItemKind.Directory
            ? trimmedNewPath + file.FullPath[trimmedOldPath.Length..]
            : newPath;
        var directoryPath = Path.GetDirectoryName(newFullPath) ?? string.Empty;
        updatedFile = file with
        {
            Name = Path.GetFileName(newFullPath),
            FullPath = newFullPath,
            DirectoryPath = directoryPath
        };
        return true;
    }

    public static bool IsFileWithinShellItem(string filePath, string shellItemPath, ShellItemKind itemKind)
    {
        if (itemKind == ShellItemKind.File)
        {
            return string.Equals(Path.GetFullPath(filePath), Path.GetFullPath(shellItemPath), StringComparison.OrdinalIgnoreCase);
        }

        return IsInsideRoot(filePath, NormalizeDirectoryScope(shellItemPath));
    }

    public static string? TransformDirectoryPathAfterRename(string? path, string oldPath, string newPath, ShellItemKind itemKind)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (itemKind == ShellItemKind.File)
        {
            return path;
        }

        var oldScope = NormalizeDirectoryScope(oldPath);
        if (!IsInsideRoot(path, oldScope) && !IsSameDirectory(path, oldPath))
        {
            return path;
        }

        var trimmedOld = TrimDirectoryPath(oldPath);
        var trimmedNew = TrimDirectoryPath(newPath);
        return trimmedNew + TrimDirectoryPath(path)[trimmedOld.Length..];
    }

    public static string? ChooseSelectionAfterDelete(string? previousSelectionPath, string deletedPath, ShellItemKind itemKind)
    {
        if (string.IsNullOrWhiteSpace(previousSelectionPath))
        {
            return null;
        }

        if (itemKind == ShellItemKind.File)
        {
            return previousSelectionPath;
        }

        return IsInsideRoot(previousSelectionPath, NormalizeDirectoryScope(deletedPath)) || IsSameDirectory(previousSelectionPath, deletedPath)
            ? Directory.GetParent(TrimDirectoryPath(deletedPath))?.FullName
            : previousSelectionPath;
    }

    public static bool TryGetExistingShellItem(string path, ShellItemKind itemKind, out string existingPath)
    {
        existingPath = NormalizeShellItemPath(path, itemKind);
        return PathExists(existingPath, itemKind);
    }

    public static bool PathExists(string path, ShellItemKind itemKind)
    {
        return itemKind == ShellItemKind.Directory ? Directory.Exists(path) : File.Exists(path);
    }

    public static string NormalizeShellItemPath(string path, ShellItemKind itemKind)
    {
        var fullPath = Path.GetFullPath(path);
        return itemKind == ShellItemKind.Directory
            ? TrimDirectoryPath(fullPath)
            : fullPath;
    }

    public static string GetShellItemName(string path, ShellItemKind itemKind)
    {
        var normalized = NormalizeShellItemPath(path, itemKind);
        return itemKind == ShellItemKind.Directory && IsDriveRoot(normalized)
            ? normalized
            : Path.GetFileName(normalized);
    }

    public static string? GetShellItemParentPath(string path, ShellItemKind itemKind)
    {
        var normalized = NormalizeShellItemPath(path, itemKind);
        return itemKind == ShellItemKind.Directory
            ? Directory.GetParent(normalized)?.FullName
            : Path.GetDirectoryName(normalized);
    }

    public static bool IsDeleteCommand(string verb, string menuText)
    {
        return verb.Equals("delete", StringComparison.OrdinalIgnoreCase)
            || menuText.Replace("&", string.Empty, StringComparison.Ordinal).Contains("delete", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRenameCommand(string verb, string menuText)
    {
        return verb.Equals("rename", StringComparison.OrdinalIgnoreCase)
            || menuText.Replace("&", string.Empty, StringComparison.Ordinal).Contains("rename", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSameDirectory(string left, string right)
    {
        var normalizedLeft = TrimDirectoryPath(left);
        var normalizedRight = TrimDirectoryPath(right);
        return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    public static string EnsureTrailingSeparator(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return fullPath.EndsWith(Path.DirectorySeparatorChar) ? fullPath : fullPath + Path.DirectorySeparatorChar;
    }

    public static string NormalizeDirectoryScope(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return fullPath.EndsWith(Path.DirectorySeparatorChar) ? fullPath : fullPath + Path.DirectorySeparatorChar;
    }

    public static string TrimDirectoryPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrWhiteSpace(root) && string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static bool IsDriveRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        return !string.IsNullOrWhiteSpace(root) && string.Equals(Path.GetFullPath(path), root, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsInsideRoot(string filePath, string normalizedRoot)
    {
        return Path.GetFullPath(filePath).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    public static string GetRootDisplayName(string normalizedRoot)
    {
        var trimmed = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.EndsWith(':') ? normalizedRoot : Path.GetFileName(trimmed);
    }

    private static bool TryAddFileToNodeTree(DirectoryUsageNode root, string normalizedRoot, FileUsageItem file)
    {
        if (!IsInsideRoot(file.FullPath, normalizedRoot))
        {
            return false;
        }

        root.AddAggregateFile(file.SizeBytes, file.LastModifiedUtc, file.LastAccessedUtc);

        var relativeDirectory = Path.GetRelativePath(normalizedRoot, file.DirectoryPath);
        var current = root;
        if (!string.IsNullOrWhiteSpace(relativeDirectory) && relativeDirectory != ".")
        {
            foreach (var part in relativeDirectory.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                var childPath = Path.Combine(current.FullPath, part);
                current = current.GetOrAddChild(part, childPath);
                current.AddAggregateFile(file.SizeBytes, file.LastModifiedUtc, file.LastAccessedUtc);
            }
        }

        current.AddDirectFile(file.SizeBytes);
        return true;
    }

    private static DateTime? MaxDate(IEnumerable<DateTime?> dates)
    {
        DateTime? maxDate = null;
        foreach (var date in dates)
        {
            if (date is not null && (maxDate is null || date.Value > maxDate.Value))
            {
                maxDate = date;
            }
        }

        return maxDate;
    }
}