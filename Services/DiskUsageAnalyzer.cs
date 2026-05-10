using EverythingDiskUsage.Models;
using EverythingDiskUsage.Native;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace EverythingDiskUsage.Services;

public sealed record ScanProgress(long FilesProcessed, long TotalResults, long BytesProcessed);

public sealed record ScanResult(DirectoryUsageNode Root, IReadOnlyList<FileUsageItem> Files, long TotalResults, TimeSpan Elapsed);

public sealed class DiskUsageAnalyzer
{
    private const uint SdkResultBatchSize = 2000;
    private const int FileProgressLogInterval = 10_000;
    private const int InitialFileSampleLogCount = 10;

    private delegate bool ResultFileTimeGetter(uint index, out long fileTime);

    public Task<ScanResult> ScanAsync(string rootPath, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        AppLogger.Info($"DiskUsageAnalyzer.ScanAsync requested; rootPath='{rootPath}', cancellationRequested={cancellationToken.IsCancellationRequested}");
        return Task.Run(() => ScanCore(rootPath, progress, cancellationToken), cancellationToken);
    }

    private static ScanResult ScanCore(string rootPath, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var scanId = Guid.NewGuid().ToString("N")[..8];
        var stopwatch = Stopwatch.StartNew();
        var normalizedRoot = NormalizeRoot(rootPath);
        var root = new DirectoryUsageNode(GetRootDisplayName(normalizedRoot), normalizedRoot);
        var files = new List<FileUsageItem>();
        long totalResults = 0;
        long filesProcessed = 0;
        long bytesProcessed = 0;
        long nonFileResultsSkipped = 0;
        long pathReadFailures = 0;
        long outsideRootSkipped = 0;
        long invalidDirectorySkipped = 0;
        long missingSizeCount = 0;
        long missingModifiedDateCount = 0;
        long missingAccessedDateCount = 0;
        long zeroSizeCount = 0;
        var lastProcessingLog = Stopwatch.StartNew();

        AppLogger.Info($"[{scanId}] ScanCore starting; inputRoot='{rootPath}', normalizedRoot='{normalizedRoot}', rootDisplayName='{root.DisplayName}'");

        var sdkLockWait = Stopwatch.StartNew();
        AppLogger.Debug($"[{scanId}] Waiting for Everything SDK lock");

        lock (EverythingSdk.Lock)
        {
            sdkLockWait.Stop();
            AppLogger.Debug($"[{scanId}] Everything SDK lock acquired; waitMs={sdkLockWait.ElapsedMilliseconds}");

            try
            {
                AppLogger.Info($"[{scanId}] SDK call: Everything_IsDBLoaded starting");
                if (!EverythingSdk.IsDBLoaded())
                {
                    AppLogger.Warning($"[{scanId}] SDK call: Everything_IsDBLoaded returned false");
                    throw new InvalidOperationException("Everything database is not loaded. Start Everything Search and wait for indexing to finish.");
                }
                AppLogger.Info($"[{scanId}] SDK call: Everything_IsDBLoaded returned true");

                AppLogger.Debug($"[{scanId}] SDK call: Everything_Reset before query starting");
                EverythingSdk.Reset();
                AppLogger.Debug($"[{scanId}] SDK call: Everything_Reset before query completed");

                var query = BuildQuery(normalizedRoot);
                var requestFlags =
                    EverythingSdk.EVERYTHING_REQUEST_FULL_PATH_AND_FILE_NAME |
                    EverythingSdk.EVERYTHING_REQUEST_SIZE |
                    EverythingSdk.EVERYTHING_REQUEST_DATE_MODIFIED |
                    EverythingSdk.EVERYTHING_REQUEST_DATE_ACCESSED;

                AppLogger.Info($"[{scanId}] SDK query configuration; query='{query}', requestFlags={requestFlags} ({DescribeRequestFlags(requestFlags)}), matchPath=true, matchCase=false, batchSize={SdkResultBatchSize}");

                AppLogger.Debug($"[{scanId}] SDK call: Everything_SetSearch starting");
                EverythingSdk.SetSearch(query);
                AppLogger.Debug($"[{scanId}] SDK call: Everything_SetSearch completed");

                AppLogger.Debug($"[{scanId}] SDK call: Everything_SetMatchPath(true) starting");
                EverythingSdk.SetMatchPath(true);
                AppLogger.Debug($"[{scanId}] SDK call: Everything_SetMatchPath(true) completed");

                AppLogger.Debug($"[{scanId}] SDK call: Everything_SetMatchCase(false) starting");
                EverythingSdk.SetMatchCase(false);
                AppLogger.Debug($"[{scanId}] SDK call: Everything_SetMatchCase(false) completed");

                AppLogger.Debug($"[{scanId}] SDK call: Everything_SetRequestFlags starting; flags={requestFlags}");
                EverythingSdk.SetRequestFlags(requestFlags);
                AppLogger.Debug($"[{scanId}] SDK call: Everything_SetRequestFlags completed");

                var buffer = new StringBuilder(1024);
                var lastProgress = Stopwatch.StartNew();
                var batchOffset = 0u;
                var batchNumber = 0;

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    batchNumber++;
                    AppLogger.Debug($"[{scanId}] SDK call: Everything_SetOffset({batchOffset}) starting; batch={batchNumber}");
                    EverythingSdk.SetOffset(batchOffset);
                    AppLogger.Debug($"[{scanId}] SDK call: Everything_SetOffset({batchOffset}) completed; batch={batchNumber}");

                    AppLogger.Debug($"[{scanId}] SDK call: Everything_SetMax({SdkResultBatchSize}) starting; batch={batchNumber}");
                    EverythingSdk.SetMax(SdkResultBatchSize);
                    AppLogger.Debug($"[{scanId}] SDK call: Everything_SetMax({SdkResultBatchSize}) completed; batch={batchNumber}");

                    var sdkQueryStopwatch = Stopwatch.StartNew();
                    AppLogger.Info($"[{scanId}] SDK call: Everything_Query(bWait=true) starting; batch={batchNumber}; offset={batchOffset}; max={SdkResultBatchSize}");
                    if (!EverythingSdk.Query(bWait: true))
                    {
                        sdkQueryStopwatch.Stop();
                        var errorCode = EverythingSdk.GetLastError();
                        AppLogger.Error($"[{scanId}] SDK call: Everything_Query failed; batch={batchNumber}; offset={batchOffset}; elapsedMs={sdkQueryStopwatch.ElapsedMilliseconds}; errorCode={errorCode}; errorMessage='{EverythingSdk.ErrorMessage(errorCode)}'");
                        throw new InvalidOperationException($"Everything SDK query failed: {EverythingSdk.ErrorMessage(errorCode)}");
                    }
                    sdkQueryStopwatch.Stop();

                    var batchResultCount = EverythingSdk.GetNumResults();
                    var totalMatches = EverythingSdk.GetTotResults();
                    if (totalMatches > 0)
                    {
                        totalResults = totalMatches;
                    }

                    AppLogger.Info($"[{scanId}] SDK call: Everything_Query succeeded; batch={batchNumber}; offset={batchOffset}; elapsedMs={sdkQueryStopwatch.ElapsedMilliseconds}; batchResults={batchResultCount}; totalMatches={totalMatches}");
                    progress?.Report(new ScanProgress(filesProcessed, totalResults, bytesProcessed));

                    if (batchNumber == 1)
                    {
                        AppLogger.Debug($"[{scanId}] Initial progress reported; totalMatches={totalMatches}");
                    }

                    if (batchResultCount == 0)
                    {
                        AppLogger.Info($"[{scanId}] No SDK results returned for batch; ending scan loop; batch={batchNumber}; offset={batchOffset}; totalMatches={totalMatches}");
                        break;
                    }

                    for (uint index = 0; index < batchResultCount; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var absoluteIndex = batchOffset + index;

                        if (!EverythingSdk.IsFileResult(index))
                        {
                            nonFileResultsSkipped++;
                            continue;
                        }

                        buffer.Clear();
                        var pathLength = EverythingSdk.GetResultFullPathName(index, buffer, (uint)buffer.Capacity);
                        if (pathLength == 0)
                        {
                            pathReadFailures++;
                            AppLogger.Warning($"[{scanId}] SDK result skipped because Everything_GetResultFullPathName returned 0; batch={batchNumber}; batchIndex={index}; absoluteIndex={absoluteIndex}");
                            continue;
                        }

                        if (pathLength >= buffer.Capacity)
                        {
                            var previousCapacity = buffer.Capacity;
                            buffer.Capacity = checked((int)pathLength + 1);
                            buffer.Clear();
                            EverythingSdk.GetResultFullPathName(index, buffer, (uint)buffer.Capacity);
                            AppLogger.Debug($"[{scanId}] Path buffer resized; batch={batchNumber}; batchIndex={index}; absoluteIndex={absoluteIndex}; previousCapacity={previousCapacity}; requestedLength={pathLength}; newCapacity={buffer.Capacity}");
                        }

                        var filePath = buffer.ToString();
                        if (!IsInsideRoot(filePath, normalizedRoot))
                        {
                            outsideRootSkipped++;
                            if (outsideRootSkipped <= 20)
                            {
                                AppLogger.Warning($"[{scanId}] SDK result skipped because it is outside normalized root; batch={batchNumber}; batchIndex={index}; absoluteIndex={absoluteIndex}; path='{filePath}'");
                            }
                            continue;
                        }

                        var hasSize = EverythingSdk.GetResultSize(index, out var sdkSizeBytes);
                        if (!hasSize)
                        {
                            missingSizeCount++;
                        }

                        var sizeBytes = hasSize ? Math.Max(0, sdkSizeBytes) : 0;
                        if (sizeBytes == 0)
                        {
                            zeroSizeCount++;
                        }

                        var lastModifiedUtc = GetResultDateUtc(index, EverythingSdk.GetResultDateModified);
                        var lastAccessedUtc = GetResultDateUtc(index, EverythingSdk.GetResultDateAccessed);
                        if (lastModifiedUtc is null)
                        {
                            missingModifiedDateCount++;
                        }

                        if (lastAccessedUtc is null)
                        {
                            missingAccessedDateCount++;
                        }

                        if (!TryAddFile(root, normalizedRoot, filePath, sizeBytes, lastModifiedUtc, lastAccessedUtc, out var fileItem))
                        {
                            invalidDirectorySkipped++;
                            AppLogger.Warning($"[{scanId}] File skipped because directory could not be resolved; batch={batchNumber}; batchIndex={index}; absoluteIndex={absoluteIndex}; path='{filePath}'");
                            continue;
                        }

                        files.Add(fileItem);
                        filesProcessed++;
                        bytesProcessed += sizeBytes;

                        if (ShouldLogFileResult(filesProcessed))
                        {
                            AppLogger.Trace($"[{scanId}] File accepted; batch={batchNumber}; batchIndex={index}; absoluteIndex={absoluteIndex}; filesProcessed={filesProcessed}; sizeBytes={sizeBytes}; lastModifiedUtc='{FormatUtc(lastModifiedUtc)}'; lastAccessedUtc='{FormatUtc(lastAccessedUtc)}'; path='{filePath}'");
                        }

                        if (filesProcessed % FileProgressLogInterval == 0 || lastProcessingLog.ElapsedMilliseconds >= 5000)
                        {
                            AppLogger.Info($"[{scanId}] SDK result processing progress; batch={batchNumber}; absoluteIndex={absoluteIndex}; totalResults={totalResults}; filesProcessed={filesProcessed}; bytesProcessed={bytesProcessed}; elapsedMs={stopwatch.ElapsedMilliseconds}; skippedNonFiles={nonFileResultsSkipped}; skippedOutsideRoot={outsideRootSkipped}; pathReadFailures={pathReadFailures}");
                            lastProcessingLog.Restart();
                        }

                        if (filesProcessed % 2000 == 0 || lastProgress.ElapsedMilliseconds >= 250)
                        {
                            progress?.Report(new ScanProgress(filesProcessed, totalResults, bytesProcessed));
                            lastProgress.Restart();
                        }
                    }

                    var nextOffset = batchOffset + batchResultCount;
                    if (totalResults > 0 && nextOffset >= totalResults)
                    {
                        AppLogger.Info($"[{scanId}] Last SDK batch reached total match count; batch={batchNumber}; nextOffset={nextOffset}; totalMatches={totalResults}");
                        break;
                    }

                    if (batchResultCount < SdkResultBatchSize)
                    {
                        AppLogger.Info($"[{scanId}] SDK batch returned fewer results than max; ending scan loop; batch={batchNumber}; batchResults={batchResultCount}; batchSize={SdkResultBatchSize}");
                        break;
                    }

                    batchOffset = nextOffset;
                }
                AppLogger.Info($"[{scanId}] SDK result loop completed; totalResults={totalResults}; filesProcessed={filesProcessed}; bytesProcessed={bytesProcessed}; skippedNonFiles={nonFileResultsSkipped}; pathReadFailures={pathReadFailures}; skippedOutsideRoot={outsideRootSkipped}; invalidDirectorySkipped={invalidDirectorySkipped}; missingSize={missingSizeCount}; zeroSize={zeroSizeCount}; missingModifiedDate={missingModifiedDateCount}; missingAccessedDate={missingAccessedDateCount}");
            }
            catch (OperationCanceledException)
            {
                AppLogger.Warning($"[{scanId}] ScanCore cancellation observed; filesProcessed={filesProcessed}; bytesProcessed={bytesProcessed}; elapsedMs={stopwatch.ElapsedMilliseconds}");
                throw;
            }
            catch (DllNotFoundException ex)
            {
                AppLogger.Error($"[{scanId}] Everything64.dll was not found while calling SDK", ex);
                throw new InvalidOperationException("Everything64.dll was not found beside the application executable.", ex);
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[{scanId}] ScanCore failed; filesProcessed={filesProcessed}; totalResults={totalResults}; bytesProcessed={bytesProcessed}; elapsedMs={stopwatch.ElapsedMilliseconds}", ex);
                throw;
            }
            finally
            {
                TryResetSdk(scanId);
                AppLogger.Debug($"[{scanId}] Everything SDK lock scope exiting");
            }
        }

        AppLogger.Info($"[{scanId}] Finalizing directory statistics starting; rootSizeBytes={root.SizeBytes}; childCount={root.Children.Count}");
        root.FinalizeStats(root.SizeBytes);
        stopwatch.Stop();
        AppLogger.Info($"[{scanId}] Finalizing directory statistics completed; folders={root.FolderCount}; rootFiles={root.FileCount}; elapsedMs={stopwatch.ElapsedMilliseconds}");
        progress?.Report(new ScanProgress(filesProcessed, totalResults, bytesProcessed));
        AppLogger.Info($"[{scanId}] ScanCore completed; filesReturned={files.Count}; totalResults={totalResults}; bytesProcessed={bytesProcessed}; elapsedMs={stopwatch.ElapsedMilliseconds}");
        return new ScanResult(root, files, totalResults, stopwatch.Elapsed);
    }

    private static string BuildQuery(string normalizedRoot)
    {
        var searchableRoot = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(searchableRoot))
        {
            searchableRoot = normalizedRoot;
        }

        var escapedRoot = searchableRoot.Replace("\"", "\"\"", StringComparison.Ordinal);
        var query = $"file: \"{escapedRoot}\"";
        AppLogger.Debug($"BuildQuery; normalizedRoot='{normalizedRoot}', searchableRoot='{searchableRoot}', query='{query}'");
        return query;
    }

    private static bool TryAddFile(
        DirectoryUsageNode root,
        string normalizedRoot,
        string filePath,
        long sizeBytes,
        DateTime? lastModifiedUtc,
        DateTime? lastAccessedUtc,
        out FileUsageItem fileItem)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (directory is null)
        {
            AppLogger.Warning($"TryAddFile failed because Path.GetDirectoryName returned null; filePath='{filePath}'");
            fileItem = null!;
            return false;
        }

        root.AddAggregateFile(sizeBytes, lastModifiedUtc, lastAccessedUtc);
        var relativeDirectory = Path.GetRelativePath(normalizedRoot, directory);
        var current = root;

        if (!string.IsNullOrWhiteSpace(relativeDirectory) && relativeDirectory != ".")
        {
            foreach (var part in relativeDirectory.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                var childPath = Path.Combine(current.FullPath, part);
                current = current.GetOrAddChild(part, childPath);
                current.AddAggregateFile(sizeBytes, lastModifiedUtc, lastAccessedUtc);
            }
        }

        current.AddDirectFile(sizeBytes);
        fileItem = new FileUsageItem(
            Path.GetFileName(filePath),
            filePath,
            directory,
            sizeBytes,
            lastModifiedUtc,
            lastAccessedUtc);
        return true;
    }

    private static DateTime? GetResultDateUtc(uint index, ResultFileTimeGetter getter)
    {
        if (!getter(index, out var fileTime) || fileTime <= 0)
        {
            return null;
        }

        try
        {
            return DateTime.FromFileTimeUtc(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            AppLogger.Warning($"SDK date conversion failed because file time was out of range; resultIndex={index}; fileTime={fileTime}");
            return null;
        }
    }

    private static bool IsInsideRoot(string filePath, string normalizedRoot)
    {
        return filePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRoot(string rootPath)
    {
        var fullPath = Path.GetFullPath(rootPath);
        var normalizedRoot = fullPath.EndsWith(Path.DirectorySeparatorChar) ? fullPath : fullPath + Path.DirectorySeparatorChar;
        AppLogger.Debug($"NormalizeRoot; input='{rootPath}', fullPath='{fullPath}', normalizedRoot='{normalizedRoot}'");
        return normalizedRoot;
    }

    private static string GetRootDisplayName(string normalizedRoot)
    {
        var trimmed = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.EndsWith(':'))
        {
            AppLogger.Debug($"GetRootDisplayName resolved drive root; normalizedRoot='{normalizedRoot}'");
            return normalizedRoot;
        }

        var displayName = Path.GetFileName(trimmed);
        AppLogger.Debug($"GetRootDisplayName resolved folder; normalizedRoot='{normalizedRoot}', displayName='{displayName}'");
        return displayName;
    }

    private static void TryResetSdk(string scanId)
    {
        try
        {
            AppLogger.Debug($"[{scanId}] SDK cleanup: Everything_Reset starting");
            EverythingSdk.Reset();
            AppLogger.Debug($"[{scanId}] SDK cleanup: Everything_Reset completed");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[{scanId}] SDK cleanup: Everything_Reset failed", ex);
        }
    }

    private static bool ShouldLogFileResult(long filesProcessed)
    {
        return AppLogger.LogEachSdkFile || filesProcessed <= InitialFileSampleLogCount || filesProcessed % FileProgressLogInterval == 0;
    }

    private static string DescribeRequestFlags(uint requestFlags)
    {
        var flags = new List<string>();
        if ((requestFlags & EverythingSdk.EVERYTHING_REQUEST_FULL_PATH_AND_FILE_NAME) != 0)
        {
            flags.Add("FULL_PATH_AND_FILE_NAME");
        }

        if ((requestFlags & EverythingSdk.EVERYTHING_REQUEST_SIZE) != 0)
        {
            flags.Add("SIZE");
        }

        if ((requestFlags & EverythingSdk.EVERYTHING_REQUEST_DATE_MODIFIED) != 0)
        {
            flags.Add("DATE_MODIFIED");
        }

        if ((requestFlags & EverythingSdk.EVERYTHING_REQUEST_DATE_ACCESSED) != 0)
        {
            flags.Add("DATE_ACCESSED");
        }

        return string.Join(",", flags);
    }

    private static string FormatUtc(DateTime? dateTimeUtc)
    {
        return dateTimeUtc is null ? string.Empty : dateTimeUtc.Value.ToString("O");
    }
}