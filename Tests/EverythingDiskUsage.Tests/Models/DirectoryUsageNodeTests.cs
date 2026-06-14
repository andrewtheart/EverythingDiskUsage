using EverythingDiskUsage.Models;
using EverythingDiskUsage.Services;

namespace EverythingDiskUsage.Tests.Models;

public sealed class DirectoryUsageNodeTests
{
    [Theory]
    [InlineData(-1, "0 B")]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1099511627776, "1 TB")]
    public void FormatBytes_UsesReadableUnitsAndClampsNegativeValues(long bytes, string expected)
    {
        Assert.Equal(expected, DirectoryUsageNode.FormatBytes(bytes));
    }

    [Fact]
    public void BuildRootFromFiles_AggregatesCountsSizesFoldersAndLatestDates()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "EverythingDiskUsageTests", Guid.NewGuid().ToString("N"));
        var older = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var newer = older.AddHours(3);
        var files = new[]
        {
            TestData.File(rootPath, "alpha\\large.bin", 4096, older, older),
            TestData.File(rootPath, "alpha\\nested\\small.txt", 512, newer, null),
            TestData.File(rootPath, "root.log", -10, null, newer)
        };

        var root = ScanViewBuilder.BuildRootFromFiles(rootPath, files);

        Assert.Equal(3, root.FileCount);
        Assert.Equal(4608, root.SizeBytes);
        Assert.Equal(2, root.FolderCount);
        Assert.Equal(1, root.DirectFileCount);
        Assert.Equal(0, root.DirectFileSizeBytes);
        Assert.Equal("100%", root.PercentText);
        Assert.Equal(newer, root.LastModifiedUtc);
        Assert.Equal(newer, root.LastAccessedUtc);

        var alpha = Assert.Single(root.Children);
        Assert.Equal("alpha", alpha.Name);
        Assert.Equal(2, alpha.FileCount);
        Assert.Equal(4608, alpha.SizeBytes);
        Assert.Equal("100%", alpha.PercentText);
        Assert.Equal(1, alpha.DirectFileCount);
        Assert.Equal(4096, alpha.DirectFileSizeBytes);

        var nested = Assert.Single(alpha.Children);
        Assert.Equal("nested", nested.Name);
        Assert.Equal(512, nested.SizeBytes);
        Assert.Equal("11.1%", nested.PercentText);
    }

    [Fact]
    public void BuildRootFromFiles_SortsChildrenBySizeThenName()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "EverythingDiskUsageTests", Guid.NewGuid().ToString("N"));
        var root = ScanViewBuilder.BuildRootFromFiles(rootPath,
        [
            TestData.File(rootPath, "zeta\\a.bin", 100),
            TestData.File(rootPath, "Beta\\a.bin", 200),
            TestData.File(rootPath, "alpha\\a.bin", 200)
        ]);

        Assert.Equal(["alpha", "Beta", "zeta"], root.Children.Select(child => child.Name));
    }
}