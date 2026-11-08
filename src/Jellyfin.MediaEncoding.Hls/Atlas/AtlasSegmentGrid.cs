using System;
using System.Collections.Generic;
using MediaBrowser.Model.Entities;

namespace Jellyfin.MediaEncoding.Hls.Atlas;

/// <summary>
/// Segment boundary math for Atlas ABR.
/// </summary>
/// <remarks>
/// Every quality level of a playback must cut its segments at the same frames, or switching levels
/// shows a jump. Boundary <c>k</c> is the first frame at or after <c>k × grid</c> seconds. The library
/// is conformed (Tdarr) to have a keyframe at exactly that frame, transcoded levels force one there,
/// and ffmpeg is told to cut at every keyframe, so all levels line up.
/// </remarks>
internal static class AtlasSegmentGrid
{
    private static readonly double[] _ntscRates = [24000.0 / 1001, 30000.0 / 1001, 48000.0 / 1001, 60000.0 / 1001, 120000.0 / 1001];

    /// <summary>
    /// Gets the exact frame rate of a video stream: NTSC rates are snapped to their rational value
    /// (23.976 becomes 24000/1001), whole rates are rounded.
    /// </summary>
    /// <param name="stream">The video stream.</param>
    /// <returns>The frame rate, or 0 if unknown.</returns>
    public static double GetFrameRate(MediaStream? stream)
        => SnapFrameRate(stream?.ReferenceFrameRate ?? 0);

    /// <summary>
    /// Snaps a measured frame rate to the exact value it stands for.
    /// </summary>
    /// <param name="fps">The measured frame rate.</param>
    /// <returns>The exact frame rate, or 0 if invalid.</returns>
    public static double SnapFrameRate(double fps)
    {
        if (double.IsNaN(fps) || double.IsInfinity(fps) || fps <= 0 || fps > 240)
        {
            return 0;
        }

        foreach (var rate in _ntscRates)
        {
            if (Math.Abs(fps - rate) < 0.005)
            {
                return rate;
            }
        }

        var rounded = Math.Round(fps);
        return Math.Abs(fps - rounded) < 0.005 ? rounded : fps;
    }

    /// <summary>
    /// Gets the time of boundary <paramref name="index"/>: the first frame at or after <c>index × grid</c>.
    /// </summary>
    /// <param name="index">The boundary (segment) index.</param>
    /// <param name="gridSeconds">The grid, in seconds.</param>
    /// <param name="fps">The exact frame rate, or 0 if unknown.</param>
    /// <returns>The boundary time in seconds.</returns>
    public static double Boundary(int index, int gridSeconds, double fps)
    {
        var target = (double)index * gridSeconds;
        return fps > 0 ? Math.Ceiling((target * fps) - 1e-6) / fps : target;
    }

    /// <summary>
    /// Gets segment lengths for an all-transcode ladder, from the frame rate.
    /// </summary>
    /// <remarks>
    /// Each boundary is placed a quarter frame before the frame it stands for. A segment request seeks
    /// to its boundary, and an accurate seek starts at the first frame at or after it, so the quarter
    /// frame of slack keeps millisecond-rounded container timestamps from skipping that frame.
    /// </remarks>
    /// <param name="runtimeTicks">The runtime of the media.</param>
    /// <param name="gridSeconds">The grid, in seconds.</param>
    /// <param name="fps">The exact frame rate, or 0 if unknown.</param>
    /// <returns>The segment lengths in seconds.</returns>
    public static IReadOnlyList<double> FromGrid(long runtimeTicks, int gridSeconds, double fps)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gridSeconds);

        var slack = fps > 0 ? 0.25 / fps : 0;
        var boundaries = new List<double>();
        var total = (double)runtimeTicks / TimeSpan.TicksPerSecond;
        for (var index = 1; ; index++)
        {
            var boundary = Boundary(index, gridSeconds, fps) - slack;
            if (boundary >= total - 0.001)
            {
                break;
            }

            boundaries.Add(boundary);
        }

        return ToLengths(boundaries, runtimeTicks);
    }

    /// <summary>
    /// Gets segment lengths that cut at the original file's keyframes.
    /// </summary>
    /// <param name="keyframeSeconds">The keyframe times, the first one being 0.</param>
    /// <param name="runtimeTicks">The runtime of the media.</param>
    /// <returns>The segment lengths in seconds.</returns>
    public static IReadOnlyList<double> FromKeyframes(IReadOnlyList<double> keyframeSeconds, long runtimeTicks)
    {
        var total = (double)runtimeTicks / TimeSpan.TicksPerSecond;
        var boundaries = new List<double>(keyframeSeconds.Count);
        foreach (var keyframe in keyframeSeconds)
        {
            if (keyframe > 0.001 && keyframe < total - 0.001)
            {
                boundaries.Add(keyframe);
            }
        }

        return ToLengths(boundaries, runtimeTicks);
    }

    /// <summary>
    /// Gets how far the first frame of a restarted transcode is past its grid point.
    /// </summary>
    /// <remarks>
    /// ffmpeg evaluates <c>-force_key_frames expr:</c> with <c>t</c> counted from the first frame it encodes,
    /// not from the start of the file. When a transcode restarts at segment <c>n</c>, its first frame is
    /// boundary <c>n</c>, which sits up to a frame past <c>n × grid</c>. Subtracting that offset in the
    /// expression puts every following forced keyframe back on the global grid.
    /// </remarks>
    /// <param name="startTicks">Where the transcode starts (the segment's runtimeTicks).</param>
    /// <param name="segmentIndex">The segment the transcode starts at.</param>
    /// <param name="gridSeconds">The grid, in seconds.</param>
    /// <param name="fps">The exact frame rate, or 0 if unknown.</param>
    /// <param name="keyframeSegments">Whether boundaries are keyframe times (exact) or grid times (a quarter frame early).</param>
    /// <returns>The offset in seconds, between 0 and one frame.</returns>
    public static double KeyframeOffset(long startTicks, int segmentIndex, int gridSeconds, double fps, bool keyframeSegments)
    {
        if (segmentIndex <= 0 || fps <= 0)
        {
            return 0;
        }

        var start = (double)startTicks / TimeSpan.TicksPerSecond;
        var firstFrame = keyframeSegments
            ? start
            : Boundary(segmentIndex, gridSeconds, fps);

        return Math.Clamp(firstFrame - ((double)segmentIndex * gridSeconds), 0, 1.0 / fps);
    }

    private static List<double> ToLengths(List<double> boundaries, long runtimeTicks)
    {
        // Work in whole ticks: the playlist generator turns each length back into ticks and adds them up
        // for the segment's runtimeTicks, which is where a restarted transcode seeks to. Integer ticks
        // keep that sum exact instead of drifting over thousands of segments.
        var lengths = new List<double>(boundaries.Count + 1);
        long previous = 0;
        foreach (var boundary in boundaries)
        {
            var ticks = (long)Math.Round(boundary * TimeSpan.TicksPerSecond);
            if (ticks > previous)
            {
                lengths.Add((double)(ticks - previous) / TimeSpan.TicksPerSecond);
                previous = ticks;
            }
        }

        if (runtimeTicks > previous)
        {
            lengths.Add((double)(runtimeTicks - previous) / TimeSpan.TicksPerSecond);
        }

        return lengths;
    }
}
