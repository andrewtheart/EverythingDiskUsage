using EverythingDiskUsage.Native;
using EverythingDiskUsage.Services;
using System.Text;

namespace EverythingDiskUsage.Tests.Services;

public sealed class DiskUsageAnalyzerTests
{
    [Fact]
    public async Task ScanAsync_QueriesOnceForCountAndOnceForAllResultsAcrossProcessingBatches()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "EverythingDiskUsageTests", Guid.NewGuid().ToString("N"));
        var sdk = new FakeEverythingSdk(rootPath, resultCount: 2001);
        var analyzer = new DiskUsageAnalyzer(new TestLogger(), sdk);

        var result = await analyzer.ScanAsync(rootPath, progress: null, CancellationToken.None);

        Assert.Equal(2, sdk.QueryCallCount);
        Assert.Equal([0u, 0u], sdk.Offsets);
        Assert.Equal([1u, 2001u], sdk.MaximumResults);
        Assert.Equal(
            EverythingSdk.EVERYTHING_REQUEST_FULL_PATH_AND_FILE_NAME | EverythingSdk.EVERYTHING_REQUEST_SIZE,
            sdk.RequestFlags);
        Assert.Equal(2001, result.TotalResults);
        Assert.Equal(2001, result.Files.Count);
        Assert.Equal(2001, result.Root.FileCount);
        Assert.Equal(2_003_001, result.Root.SizeBytes);
        Assert.All(result.Files, file =>
        {
            Assert.Null(file.LastModifiedUtc);
            Assert.Null(file.LastAccessedUtc);
        });
    }

    private sealed class FakeEverythingSdk : IEverythingSdkOperations
    {
        private readonly string[] _paths;
        private uint _maximumResults;

        public FakeEverythingSdk(string rootPath, int resultCount)
        {
            _paths = Enumerable.Range(1, resultCount)
                .Select(index => Path.Combine(rootPath, "files", $"file-{index:D5}.bin"))
                .ToArray();
        }

        public object SyncRoot { get; } = new();
        public int QueryCallCount { get; private set; }
        public List<uint> Offsets { get; } = [];
        public List<uint> MaximumResults { get; } = [];
        public uint RequestFlags { get; private set; }

        public bool IsDBLoaded() => true;
        public void Reset() { }
        public void SetSearch(string search) { }
        public void SetMatchPath(bool enabled) { }
        public void SetMatchCase(bool enabled) { }
        public void SetOffset(uint offset) => Offsets.Add(offset);
        public void SetMax(uint maximumResults)
        {
            _maximumResults = maximumResults;
            MaximumResults.Add(maximumResults);
        }
        public void SetRequestFlags(uint requestFlags) => RequestFlags = requestFlags;
        public bool Query(bool wait)
        {
            QueryCallCount++;
            return true;
        }
        public uint GetNumResults() => Math.Min((uint)_paths.Length, _maximumResults);
        public uint GetTotResults() => (uint)_paths.Length;
        public uint GetLastError() => EverythingSdk.EVERYTHING_OK;
        public bool IsFileResult(uint index) => true;
        public uint GetResultFullPathName(uint index, StringBuilder buffer, uint maximumCount)
        {
            var path = _paths[index];
            buffer.Append(path);
            return (uint)path.Length;
        }
        public bool GetResultSize(uint index, out long sizeBytes)
        {
            sizeBytes = index + 1;
            return true;
        }
        public string ErrorMessage(uint code) => EverythingSdk.ErrorMessage(code);
    }
}