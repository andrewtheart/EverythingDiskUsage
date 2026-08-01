using EverythingDiskUsage.Models;
using EverythingDiskUsage.Native;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace EverythingDiskUsage.Services;

public sealed record ScanProgress(long FilesProcessed, long TotalResults, long BytesProcessed);

public sealed record ScanResult(DirectoryUsageNode Root, IReadOnlyList<FileUsageItem> Files, long TotalResults, TimeSpan Elapsed);

public sealed class DiskUsageAnalyzer : IDiskUsageAnalyzer
{
    private const uint ResultProcessingBatchSize = 2000;
    private const uint QueryProbeResultCount = 1;
    private const int FileProgressLogInterval = 10_000;
    private const int InitialFileSampleLogCount = 10;
    private readonly IAppLogger _logger;
    private readonly IEverythingSdkOperations _sdk;

    public DiskUsageAnalyzer()
        : this(new AppLoggerAdapter(), new EverythingSdkOperations())
    {
    }

    public DiskUsageAnalyzer(IAppLogger logger)
        : this(logger, new EverythingSdkOperations())
    {
    }

    internal DiskUsageAnalyzer(IAppLogger logger, IEverythingSdkOperations sdk)
    {
        _logger = logger;
        _sdk = sdk;
    }

    public Task<ScanResult> ScanAsync(string rootPath, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        _logger.Info($"DiskUsageAnalyzer.ScanAsync requested; rootPath='{rootPath}', cancellationRequested={cancellationToken.IsCancellationRequested}");
        return Task.Run(() => ScanCore(rootPath, progress, cancellationToken, _logger, _sdk), cancellationToken);
    }

    private static ScanResult ScanCore(
        string rootPath,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken,
        IAppLogger logger,
        IEverythingSdkOperations sdk)
    {
        var scanId = Guid.NewGuid().ToString("N")[..8];
        var stopwatch = Stopwatch.StartNew();
        var normalizedRoot = NormalizeRoot(rootPath, logger);
        var root = new DirectoryUsageNode(GetRootDisplayName(normalizedRoot, logger), normalizedRoot);
        var files = new List<FileUsageItem>();
        long totalResults = 0;
        long filesProcessed = 0;
        long bytesProcessed = 0;
        long nonFileResultsSkipped = 0;
        long pathReadFailures = 0;
        long outsideRootSkipped = 0;
        long invalidDirectorySkipped = 0;
        long missingSizeCount = 0;
        long zeroSizeCount = 0;
        var lastProcessingLog = Stopwatch.StartNew();

        logger.Info($"[{scanId}] ScanCore starting; inputRoot='{rootPath}', normalizedRoot='{normalizedRoot}', rootDisplayName='{root.DisplayName}'");

        var sdkLockWait = Stopwatch.StartNew();
        logger.Debug($"[{scanId}] Waiting for Everything SDK lock");

        lock (sdk.SyncRoot)
        {
            sdkLockWait.Stop();
            logger.Debug($"[{scanId}] Everything SDK lock acquired; waitMs={sdkLockWait.ElapsedMilliseconds}");

            try
            {
                logger.Debug($"[{scanId}] SDK call: Everything_IsDBLoaded starting");
                if (!sdk.IsDBLoaded())
                {
                    logger.Warning($"[{scanId}] SDK call: Everything_IsDBLoaded returned false");
                    throw new InvalidOperationException("Everything database is not loaded. Start Everything Search and wait for indexing to finish.");
                }
                logger.Debug($"[{scanId}] SDK call: Everything_IsDBLoaded returned true");

                logger.Debug($"[{scanId}] SDK call: Everything_Reset before query starting");
                sdk.Reset();
                logger.Debug($"[{scanId}] SDK call: Everything_Reset before query completed");

                var query = BuildQuery(normalizedRoot, logger);
                var requestFlags =
                    EverythingSdk.EVERYTHING_REQUEST_FULL_PATH_AND_FILE_NAME |
                    EverythingSdk.EVERYTHING_REQUEST_SIZE;

                logger.Info($"[{scanId}] SDK query configuration; query='{query}', requestFlags={requestFlags} ({DescribeRequestFlags(requestFlags)}), matchPath=true, matchCase=false, processingBatchSize={ResultProcessingBatchSize}");

                logger.Debug($"[{scanId}] SDK call: Everything_SetSearch starting");
                sdk.SetSearch(query);
                logger.Debug($"[{scanId}] SDK call: Everything_SetSearch completed");

                logger.Debug($"[{scanId}] SDK call: Everything_SetMatchPath(true) starting");
                sdk.SetMatchPath(true);
                logger.Debug($"[{scanId}] SDK call: Everything_SetMatchPath(true) completed");

                logger.Debug($"[{scanId}] SDK call: Everything_SetMatchCase(false) starting");
                sdk.SetMatchCase(false);
                logger.Debug($"[{scanId}] SDK call: Everything_SetMatchCase(false) completed");

                logger.Debug($"[{scanId}] SDK call: Everything_SetRequestFlags starting; flags={requestFlags}");
                sdk.SetRequestFlags(requestFlags);
                logger.Debug($"[{scanId}] SDK call: Everything_SetRequestFlags completed");

                sdk.SetOffset(0);
                sdk.SetMax(QueryProbeResultCount);

                var probeStopwatch = Stopwatch.StartNew();
                logger.Debug($"[{scanId}] SDK count probe starting; max={QueryProbeResultCount}");
                if (!sdk.Query(wait: true))
                {
                    probeStopwatch.Stop();
                    var errorCode = sdk.GetLastError();
                    logger.Error($"[{scanId}] SDK count probe failed; elapsedMs={probeStopwatch.ElapsedMilliseconds}; errorCode={errorCode}; errorMessage='{sdk.ErrorMessage(errorCode)}'");
                    throw new InvalidOperationException($"Everything SDK query failed: {sdk.ErrorMessage(errorCode)}");
                }
                probeStopwatch.Stop();

                totalResults = sdk.GetTotResults();
                logger.Info($"[{scanId}] SDK count probe completed; totalMatches={totalResults}; elapsedMs={probeStopwatch.ElapsedMilliseconds}");
                progress?.Report(new ScanProgress(filesProcessed, totalResults, bytesProcessed));

                if (totalResults == 0)
                {
                    logger.Info($"[{scanId}] SDK query returned no matches");
                }
                else
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sdk.SetOffset(0);
                    sdk.SetMax(checked((uint)totalResults));

                    var sdkQueryStopwatch = Stopwatch.StartNew();
                    logger.Info($"[{scanId}] SDK full-result query starting; max={totalResults}");
                    if (!sdk.Query(wait: true))
                    {
                        sdkQueryStopwatch.Stop();
                        var errorCode = sdk.GetLastError();
                        logger.Error($"[{scanId}] SDK full-result query failed; elapsedMs={sdkQueryStopwatch.ElapsedMilliseconds}; errorCode={errorCode}; errorMessage='{sdk.ErrorMessage(errorCode)}'");
                        throw new InvalidOperationException($"Everything SDK query failed: {sdk.ErrorMessage(errorCode)}");
                    }
                    sdkQueryStopwatch.Stop();

                    var resultCount = sdk.GetNumResults();
                    totalResults = sdk.GetTotResults();
                    logger.Info($"[{scanId}] SDK full-result query completed; returnedResults={resultCount}; totalMatches={totalResults}; elapsedMs={sdkQueryStopwatch.ElapsedMilliseconds}");

                    var buffer = new StringBuilder(1024);
                    var lastProgress = Stopwatch.StartNew();
                    var batchNumber = 0;

                    for (var batchOffset = 0u; batchOffset < resultCount; batchOffset += ResultProcessingBatchSize)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        batchNumber++;
                        var batchEnd = Math.Min(resultCount, batchOffset + ResultProcessingBatchSize);

                        for (var index = batchOffset; index < batchEnd; index++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var absoluteIndex = index;

                            if (!sdk.IsFileResult(index))
                            {
                                nonFileResultsSkipped++;
                                continue;
                            }

                            buffer.Clear();
                            var pathLength = sdk.GetResultFullPathName(index, buffer, (uint)buffer.Capacity);
                            if (pathLength == 0)
                            {
                                pathReadFailures++;
                                logger.Warning($"[{scanId}] SDK result skipped because Everything_GetResultFullPathName returned 0; batch={batchNumber}; batchIndex={index}; absoluteIndex={absoluteIndex}");
                                continue;
                            }

                            if (pathLength >= buffer.Capacity)
                            {
                                var previousCapacity = buffer.Capacity;
                                buffer.Capacity = checked((int)pathLength + 1);
                                buffer.Clear();
                                sdk.GetResultFullPathName(index, buffer, (uint)buffer.Capacity);
                                logger.Debug($"[{scanId}] Path buffer resized; batch={batchNumber}; batchIndex={index}; absoluteIndex={absoluteIndex}; previousCapacity={previousCapacity}; requestedLength={pathLength}; newCapacity={buffer.Capacity}");
                            }

                            var filePath = buffer.ToString();
                            if (!IsInsideRoot(filePath, normalizedRoot))
                            {
                                outsideRootSkipped++;
                                if (outsideRootSkipped <= 20)
                                {
                                    logger.Warning($"[{scanId}] SDK result skipped because it is outside normalized root; batch={batchNumber}; batchIndex={index}; absoluteIndex={absoluteIndex}; path='{filePath}'");
                                }
                                continue;
                            }

                            var hasSize = sdk.GetResultSize(index, out var sdkSizeBytes);
                            if (!hasSize)
                            {
                                missingSizeCount++;
                            }

                            var sizeBytes = hasSize ? Math.Max(0, sdkSizeBytes) : 0;
                            if (sizeBytes == 0)
                            {
                                zeroSizeCount++;
                            }

                            if (!TryAddFile(root, normalizedRoot, filePath, sizeBytes, out var fileItem, logger))
                            {
                                invalidDirectorySkipped++;
                                logger.Warning($"[{scanId}] File skipped because directory could not be resolved; batch={batchNumber}; batchIndex={index}; absoluteIndex={absoluteIndex}; path='{filePath}'");
                                continue;
                            }

                            files.Add(fileItem);
                            filesProcessed++;
                            bytesProcessed += sizeBytes;

                            if (ShouldLogFileResult(filesProcessed, logger))
                            {
                                logger.Trace($"[{scanId}] File accepted; batch={batchNumber}; batchIndex={index}; absoluteIndex={absoluteIndex}; filesProcessed={filesProcessed}; sizeBytes={sizeBytes}; path='{filePath}'");
                            }

                            if (filesProcessed % FileProgressLogInterval == 0 || lastProcessingLog.ElapsedMilliseconds >= 5000)
                            {
                                logger.Info($"[{scanId}] SDK result processing progress; batch={batchNumber}; absoluteIndex={absoluteIndex}; totalResults={totalResults}; filesProcessed={filesProcessed}; bytesProcessed={bytesProcessed}; elapsedMs={stopwatch.ElapsedMilliseconds}; skippedNonFiles={nonFileResultsSkipped}; skippedOutsideRoot={outsideRootSkipped}; pathReadFailures={pathReadFailures}");
                                lastProcessingLog.Restart();
                            }

                            if (filesProcessed % 2000 == 0 || lastProgress.ElapsedMilliseconds >= 250)
                            {
                                progress?.Report(new ScanProgress(filesProcessed, totalResults, bytesProcessed));
                                lastProgress.Restart();
                            }
                        }
                    }
                }
                logger.Info($"[{scanId}] SDK result loop completed; totalResults={totalResults}; filesProcessed={filesProcessed}; bytesProcessed={bytesProcessed}; skippedNonFiles={nonFileResultsSkipped}; pathReadFailures={pathReadFailures}; skippedOutsideRoot={outsideRootSkipped}; invalidDirectorySkipped={invalidDirectorySkipped}; missingSize={missingSizeCount}; zeroSize={zeroSizeCount}");
            }
            catch (OperationCanceledException)
            {
                logger.Warning($"[{scanId}] ScanCore cancellation observed; filesProcessed={filesProcessed}; bytesProcessed={bytesProcessed}; elapsedMs={stopwatch.ElapsedMilliseconds}");
                throw;
            }
            catch (DllNotFoundException ex)
            {
                logger.Error($"[{scanId}] Everything64.dll was not found while calling SDK", ex);
                throw new InvalidOperationException("Everything64.dll was not found beside the application executable.", ex);
            }
            catch (Exception ex)
            {
                logger.Error($"[{scanId}] ScanCore failed; filesProcessed={filesProcessed}; totalResults={totalResults}; bytesProcessed={bytesProcessed}; elapsedMs={stopwatch.ElapsedMilliseconds}", ex);
                throw;
            }
            finally
            {
                TryResetSdk(scanId, logger, sdk);
                logger.Debug($"[{scanId}] Everything SDK lock scope exiting");
            }
        }

        logger.Debug($"[{scanId}] Finalizing directory statistics starting; rootSizeBytes={root.SizeBytes}; childCount={root.Children.Count}");
        root.FinalizeStats(root.SizeBytes);
        stopwatch.Stop();
        logger.Debug($"[{scanId}] Finalizing directory statistics completed; folders={root.FolderCount}; rootFiles={root.FileCount}; elapsedMs={stopwatch.ElapsedMilliseconds}");
        progress?.Report(new ScanProgress(filesProcessed, totalResults, bytesProcessed));
        logger.Info($"[{scanId}] ScanCore completed; filesReturned={files.Count}; totalResults={totalResults}; bytesProcessed={bytesProcessed}; elapsedMs={stopwatch.ElapsedMilliseconds}");
        return new ScanResult(root, files, totalResults, stopwatch.Elapsed);
    }

    private static string BuildQuery(string normalizedRoot, IAppLogger logger)
    {
        var searchableRoot = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(searchableRoot))
        {
            searchableRoot = normalizedRoot;
        }

        var escapedRoot = searchableRoot.Replace("\"", "\"\"", StringComparison.Ordinal);
        var query = $"file: \"{escapedRoot}\"";
        logger.Debug($"BuildQuery; normalizedRoot='{normalizedRoot}', searchableRoot='{searchableRoot}', query='{query}'");
        return query;
    }

    private static bool TryAddFile(
        DirectoryUsageNode root,
        string normalizedRoot,
        string filePath,
        long sizeBytes,
        out FileUsageItem fileItem,
        IAppLogger logger)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (directory is null)
        {
            logger.Warning($"TryAddFile failed because Path.GetDirectoryName returned null; filePath='{filePath}'");
            fileItem = null!;
            return false;
        }

        root.AddAggregateFile(sizeBytes, lastModifiedUtc: null, lastAccessedUtc: null);
        var relativeDirectory = Path.GetRelativePath(normalizedRoot, directory);
        var current = root;

        if (!string.IsNullOrWhiteSpace(relativeDirectory) && relativeDirectory != ".")
        {
            foreach (var part in relativeDirectory.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                var childPath = Path.Combine(current.FullPath, part);
                current = current.GetOrAddChild(part, childPath);
                current.AddAggregateFile(sizeBytes, lastModifiedUtc: null, lastAccessedUtc: null);
            }
        }

        current.AddDirectFile(sizeBytes);
        fileItem = new FileUsageItem(
            Path.GetFileName(filePath),
            filePath,
            directory,
            sizeBytes,
            LastModifiedUtc: null,
            LastAccessedUtc: null);
        return true;
    }

    private static bool IsInsideRoot(string filePath, string normalizedRoot)
    {
        return filePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRoot(string rootPath, IAppLogger logger)
    {
        var fullPath = Path.GetFullPath(rootPath);
        var normalizedRoot = fullPath.EndsWith(Path.DirectorySeparatorChar) ? fullPath : fullPath + Path.DirectorySeparatorChar;
        logger.Debug($"NormalizeRoot; input='{rootPath}', fullPath='{fullPath}', normalizedRoot='{normalizedRoot}'");
        return normalizedRoot;
    }

    private static string GetRootDisplayName(string normalizedRoot, IAppLogger logger)
    {
        var trimmed = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.EndsWith(':'))
        {
            logger.Debug($"GetRootDisplayName resolved drive root; normalizedRoot='{normalizedRoot}'");
            return normalizedRoot;
        }

        var displayName = Path.GetFileName(trimmed);
        logger.Debug($"GetRootDisplayName resolved folder; normalizedRoot='{normalizedRoot}', displayName='{displayName}'");
        return displayName;
    }

    private static void TryResetSdk(string scanId, IAppLogger logger, IEverythingSdkOperations sdk)
    {
        try
        {
            logger.Debug($"[{scanId}] SDK cleanup: Everything_Reset starting");
            sdk.Reset();
            logger.Debug($"[{scanId}] SDK cleanup: Everything_Reset completed");
        }
        catch (Exception ex)
        {
            logger.Error($"[{scanId}] SDK cleanup: Everything_Reset failed", ex);
        }
    }

    private static bool ShouldLogFileResult(long filesProcessed, IAppLogger logger)
    {
        return logger.LogEachSdkFile || filesProcessed <= InitialFileSampleLogCount || filesProcessed % FileProgressLogInterval == 0;
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

}