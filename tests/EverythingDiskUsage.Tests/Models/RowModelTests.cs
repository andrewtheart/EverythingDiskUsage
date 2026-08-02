using EverythingDiskUsage.Models;

namespace EverythingDiskUsage.Tests.Models;

public sealed class RowModelTests
{
    [Fact]
    public void FileDetailRow_FromSingleFileGroup_ExposesShellPathAndDates()
    {
        var modifiedUtc = new DateTime(2026, 6, 14, 8, 15, 0, DateTimeKind.Utc);
        var accessedUtc = modifiedUtc.AddMinutes(30);
        var row = FileDetailRow.FromGroup("report.csv", @"C:\Data", 1, 2048, modifiedUtc, accessedUtc);

        Assert.Equal("report.csv", row.DisplayName);
        Assert.Equal(@"C:\Data\report.csv", row.ShellItemPath);
        Assert.Equal("1", row.FileCountText);
        Assert.Equal("2 KB", row.SizeText);
        Assert.True(row.IsGroup);
        Assert.NotEqual("[multiple]", row.LastModifiedText);
        Assert.NotEqual("[multiple]", row.LastAccessedText);
    }

    [Fact]
    public void FileDetailRow_FromMultiFileGroup_HidesShellPathAndShowsMultipleDates()
    {
        var row = FileDetailRow.FromGroup("report.csv", "[multiple]", 2, 4096, DateTime.UtcNow, DateTime.UtcNow);

        Assert.Null(row.ShellItemPath);
        Assert.Equal("2", row.FileCountText);
        Assert.Equal("[multiple]", row.LastModifiedText);
        Assert.Equal("[multiple]", row.LastAccessedText);
        Assert.True(row.DatesAreMultiple);
    }

    [Fact]
    public void FileDetailRow_FromFile_IndentsFileRows()
    {
        var file = new FileUsageItem("a.txt", @"C:\Data\a.txt", @"C:\Data", 100, null, null);
        var row = FileDetailRow.FromFile(file);

        Assert.Equal("    a.txt", row.DisplayName);
        Assert.Equal(file.FullPath, row.ShellItemPath);
        Assert.False(row.IsGroup);
    }

    [Fact]
    public void DuplicateFileRow_FormatsOccurrenceAndDuplicateSetMetadata()
    {
        var row = new DuplicateFileRow("A.ZIP\0" + 1024, "a.zip", @"C:\Data", @"C:\Data\a.zip", 3, 1024, 2048);

        Assert.Equal("a.zip", row.DisplayName);
        Assert.Equal(@"C:\Data", row.PathText);
        Assert.Equal("3", row.CopyCountText);
        Assert.Equal("1 KB", row.SizeText);
        Assert.Equal("2 KB", row.WastedText);
        Assert.Contains("3 copies", row.DuplicateSetLabel, StringComparison.Ordinal);
    }
}