using System;
using System.Collections.Generic;

namespace Jellyfin.Api.Helpers;

/// <summary>
/// What Atlas ABR knows about an original file, from one ffprobe pass over its video packets.
/// </summary>
internal sealed class AtlasSourceInfo
{
    /// <summary>
    /// The format version. Bump it to make every file be analyzed again.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Gets the format version this was written with.
    /// </summary>
    public int Version { get; init; }

    /// <summary>
    /// Gets the file size when it was analyzed. A replaced file (Tdarr) is analyzed again.
    /// </summary>
    public long FileSize { get; init; }

    /// <summary>
    /// Gets the file's last write time when it was analyzed, in UTC ticks.
    /// </summary>
    public long LastWriteUtcTicks { get; init; }

    /// <summary>
    /// Gets the grid the file was checked against, in seconds.
    /// </summary>
    public int GridSeconds { get; init; }

    /// <summary>
    /// Gets a value indicating whether the file has a keyframe on every grid point and nowhere else,
    /// which is what lets the original be a level next to transcodes.
    /// </summary>
    public bool Aligned { get; init; }

    /// <summary>
    /// Gets the exact frame rate.
    /// </summary>
    public double FrameRate { get; init; }

    /// <summary>
    /// Gets the keyframe times in seconds from the start of the file. Only kept when <see cref="Aligned"/>.
    /// </summary>
    public IReadOnlyList<double> KeyframeSeconds { get; init; } = Array.Empty<double>();

    /// <summary>
    /// Gets the average video bitrate in bits per second, or 0 if unknown.
    /// </summary>
    public long AverageVideoBitrate { get; init; }

    /// <summary>
    /// Gets the highest video bitrate over any one grid-length window, in bits per second, or 0 if unknown.
    /// This is what a segment of the original can actually need.
    /// </summary>
    public long PeakVideoBitrate { get; init; }

    /// <summary>
    /// Gets a short reason when <see cref="Aligned"/> is <c>false</c>, for the log.
    /// </summary>
    public string? Reason { get; init; }
}
