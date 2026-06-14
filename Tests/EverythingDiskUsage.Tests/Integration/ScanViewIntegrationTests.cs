using EverythingDiskUsage.Services;

namespace EverythingDiskUsage.Tests.Integration;

public sealed class ScanViewIntegrationTests
{
    [Fact]
    public void BuildScanViewSnapshot_FiltersMissingAndOutsideFilesUsingRealFileSystem()
    {
        using var temp = TempDirectory.Create();
        var rootPath = temp.Path;
        WriteFile(Path.Combine(rootPath, "a", "duplicate.bin"), 10);
        WriteFile(Path.Combine(rootPath, "b", "duplicate.bin"), 10);
        WriteFile(Path.Combine(rootPath, "b", "unique.bin"), 25);

        using var outside = TempDirectory.Create();
        WriteFile(Path.Combine(outside.Path, "duplicate.bin"), 10);

        var files = new[]
        {
            TestData.File(rootPath, "a\\duplicate.bin", 10),
            TestData.File(rootPath, "b\\duplicate.bin", 10),
            TestData.File(rootPath, "b\\unique.bin", 25),
            TestData.File(rootPath, "missing.bin", 99),
            TestData.File(outside.Path, "duplicate.bin", 10)
        };

        var snapshot = ScanViewBuilder.BuildScanViewSnapshot(rootPath, files);

        Assert.Equal(3, snapshot.Files.Count);
        Assert.Equal(45, snapshot.Root.SizeBytes);
        Assert.Equal(3, snapshot.Root.FileCount);
        Assert.Equal(2, snapshot.Root.FolderCount);
        Assert.Equal(1, snapshot.Duplicates.TotalGroups);
        Assert.Equal(10, snapshot.Duplicates.TotalWastedBytes);
        Assert.DoesNotContain(snapshot.Files, file => file.Name.Equals("missing.bin", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(snapshot.Files, file => file.FullPath.StartsWith(outside.Path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ShellItemHelpers_DetectAndNormalizeExistingFilesAndDirectories()
    {
        using var temp = TempDirectory.Create();
        var directoryPath = Path.Combine(temp.Path, "folder");
        var filePath = Path.Combine(directoryPath, "file.txt");
        WriteFile(filePath, 5);

        Assert.True(ScanViewBuilder.TryGetExistingShellItem(directoryPath + Path.DirectorySeparatorChar, ShellItemKind.Directory, out var normalizedDirectory));
        Assert.Equal(Path.GetFullPath(directoryPath), normalizedDirectory);

        Assert.True(ScanViewBuilder.TryGetExistingShellItem(filePath, ShellItemKind.File, out var normalizedFile));
        Assert.Equal(Path.GetFullPath(filePath), normalizedFile);

        Assert.Equal("folder", ScanViewBuilder.GetShellItemName(directoryPath, ShellItemKind.Directory));
        Assert.Equal(directoryPath, ScanViewBuilder.GetShellItemParentPath(filePath, ShellItemKind.File));
        Assert.True(ScanViewBuilder.IsFileWithinShellItem(filePath, directoryPath, ShellItemKind.Directory));
        Assert.True(ScanViewBuilder.IsFileWithinShellItem(filePath, filePath, ShellItemKind.File));
    }

    [Fact]
    public void BuildScanViewSnapshot_CanUseInjectedExistenceContract()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "EverythingDiskUsageTests", Guid.NewGuid().ToString("N"));
        var files = new[]
        {
            TestData.File(rootPath, "kept.txt", 100),
            TestData.File(rootPath, "filtered.txt", 200)
        };

        var snapshot = ScanViewBuilder.BuildScanViewSnapshot(rootPath, files, path => path.EndsWith("kept.txt", StringComparison.OrdinalIgnoreCase));

        var file = Assert.Single(snapshot.Files);
        Assert.Equal("kept.txt", file.Name);
        Assert.Equal(100, snapshot.Root.SizeBytes);
    }

    private static void WriteFile(string path, int byteCount)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Enumerable.Range(0, byteCount).Select(index => (byte)(index % 251)).ToArray());
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EverythingDiskUsageTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}