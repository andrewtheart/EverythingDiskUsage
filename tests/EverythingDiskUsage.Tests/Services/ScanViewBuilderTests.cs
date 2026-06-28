using EverythingDiskUsage.Models;
using EverythingDiskUsage.Services;

namespace EverythingDiskUsage.Tests.Services;

public sealed class ScanViewBuilderTests
{
    [Fact]
    public void BuildDuplicateSnapshot_GroupsSameNameAndSizeCaseInsensitively()
    {
        var rootPath = NewRootPath();
        var files = new[]
        {
            TestData.File(rootPath, "a\\Report.txt", 200),
            TestData.File(rootPath, "b\\report.TXT", 200),
            TestData.File(rootPath, "c\\report.txt", 100),
            TestData.File(rootPath, "d\\empty.txt", 0),
            TestData.File(rootPath, "e\\empty.txt", 0)
        };

        var snapshot = ScanViewBuilder.BuildDuplicateSnapshot(files);

        Assert.Equal(files.Length, snapshot.SourceFileCount);
        Assert.Equal(1, snapshot.TotalGroups);
        Assert.Equal(200, snapshot.TotalWastedBytes);
        Assert.Equal(3, snapshot.Rows.Count);
        Assert.True(snapshot.Rows[0].IsGroup);
        Assert.Equal("2 copies", snapshot.Rows[0].PathText);
        Assert.Equal(2, snapshot.Rows[0].CopyCount);
        Assert.All(snapshot.Rows.Skip(1), row => Assert.False(row.IsGroup));
        Assert.Equal("1 group \u00b7 200 B wasted", ScanViewBuilder.GetDuplicateSummaryText(snapshot));
    }

    [Fact]
    public void BuildDuplicateSnapshot_ReturnsNoDuplicateSummaryWhenOnlyUniqueFilesExist()
    {
        var rootPath = NewRootPath();
        var snapshot = ScanViewBuilder.BuildDuplicateSnapshot(
        [
            TestData.File(rootPath, "a.txt", 100),
            TestData.File(rootPath, "b.txt", 100)
        ]);

        Assert.Empty(snapshot.Rows);
        Assert.Equal("No duplicates found", ScanViewBuilder.GetDuplicateSummaryText(snapshot));
    }

    [Fact]
    public void BuildVisibleFileDetails_GroupsScopedFilesAndOrdersByTotalSize()
    {
        var rootPath = NewRootPath();
        var allFiles = ScanViewBuilder.SortFiles(new[]
        {
            TestData.File(rootPath, "scope\\one\\same.bin", 100),
            TestData.File(rootPath, "scope\\two\\same.bin", 300),
            TestData.File(rootPath, "scope\\one\\unique.txt", 50),
            TestData.File(rootPath, "other\\same.bin", 500)
        }).ToList();
        var root = ScanViewBuilder.BuildRootFromFiles(rootPath, allFiles);
        var scope = Assert.Single(root.Children, child => child.Name.Equals("scope", StringComparison.OrdinalIgnoreCase));

        var details = ScanViewBuilder.BuildVisibleFileDetails(scope, allFiles);

        Assert.Equal(2, details.GroupCount);
        Assert.Equal("3 files", details.SummaryText);
        Assert.Equal("same.bin", details.Rows[0].Name);
        Assert.True(details.Rows[0].IsGroup);
        Assert.Equal("[multiple]", details.Rows[0].PathText);
        Assert.Equal(400, details.Rows[0].SizeBytes);
        Assert.Equal(["same.bin", "same.bin"], details.Rows.Skip(1).Take(2).Select(row => row.Name));
        Assert.Equal("unique.txt", details.Rows[3].Name);
        Assert.NotNull(details.Rows[3].ShellItemPath);
    }

    [Fact]
    public void BuildVisibleFileDetails_RespectsVisibleRowLimit()
    {
        var rootPath = NewRootPath();
        var allFiles = Enumerable.Range(1, 20)
            .Select(index => TestData.File(rootPath, $"file-{index:00}.bin", index))
            .ToList();
        var root = ScanViewBuilder.BuildRootFromFiles(rootPath, allFiles);

        var details = ScanViewBuilder.BuildVisibleFileDetails(root, allFiles, maxVisibleRows: 5);

        Assert.Equal(5, details.Rows.Count);
        Assert.Equal("Showing 5 rows from 20 files", details.SummaryText);
    }

    [Fact]
    public void BuildSlices_ReturnsTopChildrenDirectFilesAndOtherBucket()
    {
        var rootPath = NewRootPath();
        var files = Enumerable.Range(1, 11)
            .Select(index => TestData.File(rootPath, $"folder-{index:00}\\file.bin", index * 100L))
            .Append(TestData.File(rootPath, "root.bin", 75))
            .ToList();
        var root = ScanViewBuilder.BuildRootFromFiles(rootPath, files);

        var slices = ScanViewBuilder.BuildSlices(root);

        Assert.Equal(10, slices.Count);
        Assert.Equal("folder-11", slices[0].Label);
        Assert.True(slices[0].IsSelectable);
        Assert.Equal("Other", slices[^1].Label);
        Assert.False(slices[^1].IsSelectable);
        Assert.Same(root, slices[^1].ExplorerNode);
        Assert.All(slices, slice => Assert.True(slice.CanOpenInExplorer));
        Assert.Equal(100d, slices.Sum(slice => slice.Percent), precision: 8);
    }

    [Fact]
    public void BuildSlices_ReturnsEmptyListForEmptyNode()
    {
        var root = new DirectoryUsageNode("empty", NewRootPath());

        Assert.Empty(ScanViewBuilder.BuildSlices(root));
    }

    [Fact]
    public void SortFiles_OrdersBySizeNameAndPath()
    {
        var rootPath = NewRootPath();
        var sorted = ScanViewBuilder.SortFiles(new[]
        {
            TestData.File(rootPath, "b\\same.txt", 20),
            TestData.File(rootPath, "a\\same.txt", 20),
            TestData.File(rootPath, "z.txt", 5),
            TestData.File(rootPath, "alpha.txt", 20)
        }).ToList();

        Assert.Equal(["alpha.txt", "same.txt", "same.txt", "z.txt"], sorted.Select(file => file.Name));
        Assert.EndsWith(@"a\same.txt", sorted[1].FullPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PathHelpers_MapRenamedDirectoriesFilesAndSelections()
    {
        var rootPath = NewRootPath();
        var oldDirectory = Path.Combine(rootPath, "old");
        var newDirectory = Path.Combine(rootPath, "new");
        var nestedFile = TestData.File(rootPath, "old\\child\\a.txt", 10);

        Assert.True(ScanViewBuilder.TryMapRenamedFile(nestedFile, oldDirectory, newDirectory, ShellItemKind.Directory, out var mappedDirectoryFile));
        Assert.EndsWith(@"new\child\a.txt", mappedDirectoryFile.FullPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(@"new\child", mappedDirectoryFile.DirectoryPath, StringComparison.OrdinalIgnoreCase);

        var newFilePath = Path.Combine(rootPath, "old", "renamed.txt");
        Assert.True(ScanViewBuilder.TryMapRenamedFile(nestedFile, nestedFile.FullPath, newFilePath, ShellItemKind.File, out var mappedFile));
        Assert.Equal("renamed.txt", mappedFile.Name);
        Assert.Equal(newFilePath, mappedFile.FullPath);

        var transformedSelection = ScanViewBuilder.TransformDirectoryPathAfterRename(
            Path.Combine(oldDirectory, "child"),
            oldDirectory,
            newDirectory,
            ShellItemKind.Directory);
        Assert.EndsWith(@"new\child", transformedSelection, StringComparison.OrdinalIgnoreCase);

        var parentSelection = ScanViewBuilder.ChooseSelectionAfterDelete(Path.Combine(oldDirectory, "child"), oldDirectory, ShellItemKind.Directory);
        Assert.Equal(Path.GetFullPath(rootPath), parentSelection);
        Assert.Equal(Path.Combine(oldDirectory, "child"), ScanViewBuilder.ChooseSelectionAfterDelete(Path.Combine(oldDirectory, "child"), oldDirectory, ShellItemKind.File));
    }

    [Theory]
    [InlineData("delete", "Remove", true)]
    [InlineData("", "&Delete", true)]
    [InlineData("open", "Open", false)]
    public void IsDeleteCommand_MatchesVerbOrMenuText(string verb, string menuText, bool expected)
    {
        Assert.Equal(expected, ScanViewBuilder.IsDeleteCommand(verb, menuText));
    }

    [Theory]
    [InlineData("rename", "Change", true)]
    [InlineData("", "&Rename", true)]
    [InlineData("open", "Open", false)]
    public void IsRenameCommand_MatchesVerbOrMenuText(string verb, string menuText, bool expected)
    {
        Assert.Equal(expected, ScanViewBuilder.IsRenameCommand(verb, menuText));
    }

    private static string NewRootPath() => Path.Combine(Path.GetTempPath(), "EverythingDiskUsageTests", Guid.NewGuid().ToString("N"));
}