using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.MediaEncoding.Hls.Atlas;

/// <summary>
/// Analyzes original files for Atlas ABR in the background and caches the result on disk.
/// </summary>
/// <remarks>
/// The first playback of a file can't wait for a full packet scan (it reads the whole file), so the
/// first time a file is seen it is queued, and that playback gets an all-transcode ladder. Every
/// playback after the scan finishes can use the original as the top level. One scan runs at a time.
/// </remarks>
internal static class AtlasSourceAnalyzer
{
    private static readonly ConcurrentDictionary<string, AtlasSourceInfo> _memory = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim _scanGate = new(1, 1);
    private static readonly TimeSpan _scanStartDelay = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions _jsonOptions = new();
    private static readonly TimeSpan _scanTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Gets where analyses are stored: the data folder, not the cache, since redoing one means reading
    /// the whole file again and "clear cache" shouldn't throw that away.
    /// </summary>
    /// <param name="paths">The application paths.</param>
    /// <returns>The directory.</returns>
    public static string GetDirectory(IApplicationPaths paths) => Path.Combine(paths.DataPath, "atlas-abr");

    /// <summary>
    /// Analyzes a file now unless a current analysis exists, waiting for any scan already running.
    /// Used by the scheduled task, so playback never has to wait for one.
    /// </summary>
    /// <param name="mediaSourceId">The media source id.</param>
    /// <param name="mediaPath">The file path.</param>
    /// <param name="probePath">The ffprobe path.</param>
    /// <param name="cacheDirectory">Where analyses are stored.</param>
    /// <param name="gridSeconds">The grid to check keyframes against.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>true</c> if a scan ran, <c>false</c> if the file was already analyzed or can't be.</returns>
    public static async Task<bool> AnalyzeIfNeededAsync(
        string mediaSourceId,
        string mediaPath,
        string probePath,
        string cacheDirectory,
        int gridSeconds,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (TryGetCached(mediaSourceId, mediaPath, cacheDirectory, gridSeconds) is not null)
        {
            return false;
        }

        if (!_queued.TryAdd(mediaSourceId, 0))
        {
            // A playback already queued it.
            return false;
        }

        await ScanAndStoreAsync(mediaSourceId, mediaPath, probePath, cacheDirectory, gridSeconds, logger, TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Gets the cached analysis of a file, queueing a scan if there is none (or the file changed).
    /// </summary>
    /// <param name="mediaSourceId">The media source id.</param>
    /// <param name="mediaPath">The file path.</param>
    /// <param name="probePath">The ffprobe path.</param>
    /// <param name="cacheDirectory">Where analyses are stored.</param>
    /// <param name="gridSeconds">The grid to check keyframes against.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The analysis, or <c>null</c> if it isn't available yet.</returns>
    public static AtlasSourceInfo? GetOrQueue(string mediaSourceId, string mediaPath, string probePath, string cacheDirectory, int gridSeconds, ILogger logger)
    {
        var info = TryGetCached(mediaSourceId, mediaPath, cacheDirectory, gridSeconds);
        if (info is not null)
        {
            return info;
        }

        if (_queued.TryAdd(mediaSourceId, 0))
        {
            _ = Task.Run(() => ScanAndStoreAsync(mediaSourceId, mediaPath, probePath, cacheDirectory, gridSeconds, logger, _scanStartDelay, CancellationToken.None));
        }

        return null;
    }

    /// <summary>
    /// Gets the cached analysis of a file without queueing anything.
    /// </summary>
    /// <param name="mediaSourceId">The media source id.</param>
    /// <param name="mediaPath">The file path.</param>
    /// <param name="cacheDirectory">Where analyses are stored.</param>
    /// <param name="gridSeconds">The grid the analysis has to be for.</param>
    /// <returns>The analysis, or <c>null</c> if there is no current one.</returns>
    public static AtlasSourceInfo? TryGetCached(string mediaSourceId, string mediaPath, string cacheDirectory, int gridSeconds)
    {
        if (!TryGetFileStamp(mediaPath, out var size, out var lastWrite))
        {
            return null;
        }

        if (!_memory.TryGetValue(mediaSourceId, out var info))
        {
            info = ReadFromDisk(GetCachePath(cacheDirectory, mediaSourceId));
            if (info is not null)
            {
                _memory[mediaSourceId] = info;
            }
        }

        return info is not null
            && info.Version == AtlasSourceInfo.CurrentVersion
            && info.GridSeconds == gridSeconds
            && info.FileSize == size
            && info.LastWriteUtcTicks == lastWrite
            ? info
            : null;
    }

    private static async Task ScanAndStoreAsync(
        string mediaSourceId,
        string mediaPath,
        string probePath,
        string cacheDirectory,
        int gridSeconds,
        ILogger logger,
        TimeSpan startDelay,
        CancellationToken cancellationToken)
    {
        try
        {
            // A playback-triggered scan lets that play get going first: the scan reads the whole
            // file, and doing that while the first segments are being transcoded slows startup.
            if (startDelay > TimeSpan.Zero)
            {
                await Task.Delay(startDelay, cancellationToken).ConfigureAwait(false);
            }

            await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _queued.TryRemove(mediaSourceId, out _);
            throw;
        }

        try
        {
            if (!TryGetFileStamp(mediaPath, out var size, out var lastWrite))
            {
                return;
            }

            logger.LogInformation("Atlas ABR: analyzing {Path}", mediaPath);
            var stopwatch = Stopwatch.StartNew();
            var info = await ScanAsync(mediaPath, probePath, gridSeconds, size, lastWrite, cancellationToken).ConfigureAwait(false);
            if (info is null)
            {
                return;
            }

            _memory[mediaSourceId] = info;
            await WriteToDiskAsync(GetCachePath(cacheDirectory, mediaSourceId), info).ConfigureAwait(false);

            logger.LogInformation(
                "Atlas ABR: analyzed {Path} in {Seconds:0.0}s. Aligned: {Aligned} {Reason}. Average {Average} kbps, peak {Peak} kbps",
                mediaPath,
                stopwatch.Elapsed.TotalSeconds,
                info.Aligned,
                info.Reason ?? string.Empty,
                info.AverageVideoBitrate / 1000,
                info.PeakVideoBitrate / 1000);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(ex, "Atlas ABR: analyzing {Path} failed", mediaPath);
        }
        finally
        {
            _scanGate.Release();
            _queued.TryRemove(mediaSourceId, out _);
        }
    }

    private static async Task<AtlasSourceInfo?> ScanAsync(string mediaPath, string probePath, int gridSeconds, long size, long lastWrite, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_scanTimeout);
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = probePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        foreach (var arg in new[]
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "format=start_time:stream=r_frame_rate,avg_frame_rate:packet=pts_time,size,flags",
            "-of", "compact=p=1",
            mediaPath
        })
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        try
        {
            // Background work: never compete with a transcode for the CPU.
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            // Not allowed here or already exited; the scan still runs.
        }

        double formatStart = double.NaN;
        double fps = 0;
        double firstPts = double.MaxValue;
        double lastPts = 0;
        long totalBytes = 0;
        var keyframes = new List<double>();
        var packets = new List<(double Pts, int Size)>();

        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false)) is not null)
            {
                if (line.StartsWith("packet|", StringComparison.Ordinal))
                {
                    var pts = ReadDouble(line, "pts_time");
                    var packetSize = ReadDouble(line, "size");
                    if (double.IsNaN(pts) || double.IsNaN(packetSize))
                    {
                        continue;
                    }

                    var flags = ReadValue(line, "flags");
                    if (flags.Length > 0 && flags[0] == 'K')
                    {
                        keyframes.Add(pts);
                    }

                    packets.Add((pts, (int)packetSize));
                    totalBytes += (long)packetSize;
                    firstPts = Math.Min(firstPts, pts);
                    lastPts = Math.Max(lastPts, pts);
                }
                else if (line.StartsWith("stream|", StringComparison.Ordinal))
                {
                    fps = AtlasSegmentGrid.SnapFrameRate(ReadRational(line, "r_frame_rate"));
                    if (fps <= 0)
                    {
                        fps = AtlasSegmentGrid.SnapFrameRate(ReadRational(line, "avg_frame_rate"));
                    }
                }
                else if (line.StartsWith("format|", StringComparison.Ordinal))
                {
                    formatStart = ReadDouble(line, "start_time");
                }
            }

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            throw;
        }

        if (packets.Count == 0)
        {
            return null;
        }

        // Times are made relative to where -start_at_zero puts zero: the container start, else the first packet.
        var zero = double.IsNaN(formatStart) ? firstPts : formatStart;
        var duration = lastPts - zero;
        var averageBitrate = duration > 0 ? (long)(totalBytes * 8 / duration) : 0;
        var peakBitrate = GetPeakBitrate(packets, zero, gridSeconds);

        keyframes.Sort();
        for (var i = 0; i < keyframes.Count; i++)
        {
            keyframes[i] -= zero;
        }

        var reason = CheckAlignment(keyframes, gridSeconds, fps, duration);

        return new AtlasSourceInfo
        {
            Version = AtlasSourceInfo.CurrentVersion,
            FileSize = size,
            LastWriteUtcTicks = lastWrite,
            GridSeconds = gridSeconds,
            Aligned = reason is null,
            Reason = reason,
            FrameRate = fps,
            KeyframeSeconds = reason is null ? keyframes : Array.Empty<double>(),
            AverageVideoBitrate = averageBitrate,
            PeakVideoBitrate = peakBitrate
        };
    }

    // Checks that the file has exactly one keyframe per grid point, on the first frame at or after it.
    // Returns null if aligned, otherwise why not.
    private static string? CheckAlignment(List<double> keyframes, int gridSeconds, double fps, double duration)
    {
        if (fps <= 0)
        {
            return "unknown frame rate";
        }

        if (keyframes.Count < 2)
        {
            return "too few keyframes";
        }

        // Container timestamps are often rounded to the millisecond.
        const double RoundingSlack = 0.0006;
        var frameSlack = (0.5 / fps) + RoundingSlack;
        var expectedIndex = 0;
        foreach (var keyframe in keyframes)
        {
            var index = (int)Math.Round(keyframe / gridSeconds);
            if (index != expectedIndex)
            {
                return index < expectedIndex
                    ? string.Create(CultureInfo.InvariantCulture, $"extra keyframe at {keyframe:0.000}s")
                    : string.Create(CultureInfo.InvariantCulture, $"missing keyframe near {(double)expectedIndex * gridSeconds:0.000}s");
            }

            var expected = AtlasSegmentGrid.Boundary(index, gridSeconds, fps);
            if (Math.Abs(keyframe - expected) > frameSlack || keyframe < ((double)index * gridSeconds) - RoundingSlack)
            {
                return string.Create(CultureInfo.InvariantCulture, $"keyframe at {keyframe:0.000}s is off the grid");
            }

            expectedIndex++;
        }

        // The keyframes have to carry on to (near) the end, or the tail would be one long segment.
        var lastExpected = (int)Math.Floor((duration - 0.5) / gridSeconds);
        if (expectedIndex - 1 < lastExpected - 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"keyframes stop at {(double)(expectedIndex - 1) * gridSeconds:0.000}s");
        }

        return null;
    }

    private static long GetPeakBitrate(List<(double Pts, int Size)> packets, double zero, int gridSeconds)
    {
        var windows = new List<long>();
        foreach (var (pts, size) in packets)
        {
            var window = (int)Math.Max(0, Math.Floor((pts - zero) / gridSeconds));
            while (windows.Count <= window)
            {
                windows.Add(0);
            }

            windows[window] += size;
        }

        // The last window is usually partial; leave it out unless it's the only one.
        long peakBytes = 0;
        var count = windows.Count > 1 ? windows.Count - 1 : windows.Count;
        for (var i = 0; i < count; i++)
        {
            peakBytes = Math.Max(peakBytes, windows[i]);
        }

        return peakBytes * 8 / gridSeconds;
    }

    private static string ReadValue(string line, string key)
    {
        var search = "|" + key + "=";
        var start = line.IndexOf(search, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        start += search.Length;
        var end = line.IndexOf('|', start);
        return end < 0 ? line[start..] : line[start..end];
    }

    private static double ReadDouble(string line, string key)
        => double.TryParse(ReadValue(line, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;

    private static double ReadRational(string line, string key)
    {
        var value = ReadValue(line, key);
        var slash = value.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0
            && double.TryParse(value.AsSpan(0, slash), NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(value.AsSpan(slash + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && denominator > 0)
        {
            return numerator / denominator;
        }

        return 0;
    }

    private static bool TryGetFileStamp(string mediaPath, out long size, out long lastWriteUtcTicks)
    {
        try
        {
            var file = new FileInfo(mediaPath);
            if (file.Exists)
            {
                size = file.Length;
                lastWriteUtcTicks = file.LastWriteTimeUtc.Ticks;
                return true;
            }
        }
        catch (IOException)
        {
            // Treated as missing.
        }
        catch (UnauthorizedAccessException)
        {
            // Treated as missing.
        }

        size = 0;
        lastWriteUtcTicks = 0;
        return false;
    }

    private static string GetCachePath(string cacheDirectory, string mediaSourceId)
    {
        // Media source ids are GUID strings, but don't trust them with a path.
        var safeName = string.Concat(mediaSourceId.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(cacheDirectory, safeName + ".json");
    }

    private static AtlasSourceInfo? ReadFromDisk(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<AtlasSourceInfo>(stream, _jsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task WriteToDiskAsync(string path, AtlasSourceInfo info)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        var stream = File.Create(temp);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, info, _jsonOptions).ConfigureAwait(false);
        }

        File.Move(temp, path, true);
    }
}
