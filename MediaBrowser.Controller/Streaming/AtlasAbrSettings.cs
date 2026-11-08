using System;
using System.Collections.Generic;
using System.Globalization;

namespace MediaBrowser.Controller.Streaming;

/// <summary>
/// Atlas adaptive bitrate (ABR) streaming settings.
/// </summary>
/// <remarks>
/// Atlas ABR is opt-in per request: only a master playlist requested with <c>AtlasAbr=true</c>
/// gets the quality ladder, so stock Jellyfin clients behave exactly as upstream.
/// Everything here is read once from environment variables, so it can be tuned
/// without a settings UI:
/// <list type="bullet">
/// <item><c>ATLAS_ABR_ENABLED</c>: <c>false</c> turns the ladder off server-wide (default <c>true</c>).</item>
/// <item><c>ATLAS_ABR_PREWARM</c>: <c>false</c> stops starting the first segment's transcode at playlist time (default <c>true</c>).</item>
/// <item><c>ATLAS_ABR_GRID_SECONDS</c>: the keyframe interval the library was conformed to, and the segment length (default 2).</item>
/// <item><c>ATLAS_ABR_IDLE_LEVEL_SECONDS</c>: how long a quality level may go without segment requests before it is stopped (default 20).</item>
/// <item><c>ATLAS_ABR_PEAK_FACTOR</c>: peak/average bitrate ratio assumed for the original file until it has been analyzed (default 1.8).</item>
/// <item><c>ATLAS_ABR_LADDER</c>: transcoded levels as <c>height:kbps</c> pairs (default <c>480:1500,720:2000,720:4000,1080:8000,1080:12000,1080:20000</c>, the app's quality presets).</item>
/// </list>
/// </remarks>
public static class AtlasAbrSettings
{
    /// <summary>
    /// The master playlist query parameter that asks for the Atlas ladder.
    /// </summary>
    public const string RequestParameter = "AtlasAbr";

    /// <summary>
    /// The variant playlist and segment query parameter naming the quality level.
    /// </summary>
    public const string LevelParameter = "AtlasLevel";

    /// <summary>
    /// The master playlist query parameter with the client's expected bandwidth, in bits per second.
    /// </summary>
    public const string StartBitrateParameter = "AtlasStartBitrate";

    /// <summary>
    /// The master playlist query parameter with where playback will start, in ticks, so the server can
    /// start transcoding that segment right away (see <see cref="Prewarm"/>).
    /// </summary>
    public const string StartTicksParameter = "AtlasStartTicks";

    /// <summary>
    /// The master playlist query parameter pinning one quality (a manual pick in the app), in bits per
    /// second. With <see cref="MaxHeightParameter"/> it replaces the ladder with a single level.
    /// </summary>
    public const string MaxBitrateParameter = "AtlasMaxBitrate";

    /// <summary>
    /// The master playlist query parameter with the pinned quality's height (see <see cref="MaxBitrateParameter"/>).
    /// </summary>
    public const string MaxHeightParameter = "AtlasMaxHeight";

    /// <summary>
    /// The segment query parameter marking the server's own warm-up request for the next level up, so
    /// that level is already transcoding when the player switches to it.
    /// </summary>
    public const string WarmParameter = "AtlasWarm";

    /// <summary>
    /// The variant playlist and segment query parameter saying where segment boundaries come from:
    /// <see cref="KeyframeSegments"/> or <see cref="GridSegments"/>.
    /// </summary>
    public const string SegmentsParameter = "AtlasSegments";

    /// <summary>
    /// Segment boundaries are the analyzed keyframes of the original file, which every level cuts at.
    /// Used when the ladder includes the original file.
    /// </summary>
    public const string KeyframeSegments = "kf";

    /// <summary>
    /// Segment boundaries are computed from the frame rate and the grid. Used for an all-transcode ladder.
    /// </summary>
    public const string GridSegments = "grid";

    /// <summary>
    /// The level id of the original file (video stream copy).
    /// </summary>
    public const string OriginalLevel = "src";

    /// <summary>
    /// The level id of a full resolution transcode, used when the original can't be stream copied.
    /// </summary>
    public const string TopLevel = "top";

    /// <summary>
    /// Gets a value indicating whether Atlas ABR is enabled on this server.
    /// </summary>
    public static bool Enabled { get; } = ReadBool("ATLAS_ABR_ENABLED", true);

    /// <summary>
    /// Gets a value indicating whether the start level's first segment starts transcoding as soon as
    /// the master playlist is handed out (<c>ATLAS_ABR_PREWARM</c>, default <c>true</c>).
    /// </summary>
    public static bool Prewarm { get; } = ReadBool("ATLAS_ABR_PREWARM", true);

    /// <summary>
    /// Gets the keyframe grid of the conformed library, in seconds. This is also the segment length.
    /// </summary>
    public static int GridSeconds { get; } = ReadInt("ATLAS_ABR_GRID_SECONDS", 2, 1, 10);

    /// <summary>
    /// Gets how long a quality level may go without segment requests before it is stopped, in seconds.
    /// </summary>
    public static int IdleLevelSeconds { get; } = ReadInt("ATLAS_ABR_IDLE_LEVEL_SECONDS", 20, 5, 600);

    /// <summary>
    /// Gets the peak/average bitrate ratio assumed for an original file that hasn't been analyzed yet.
    /// </summary>
    public static double FallbackPeakFactor { get; } = ReadDouble("ATLAS_ABR_PEAK_FACTOR", 1.8, 1.0, 4.0);

    /// <summary>
    /// Gets the transcoded levels, lowest first.
    /// </summary>
    public static IReadOnlyList<(int Height, int VideoBitrate)> Ladder { get; } = ReadLadder("ATLAS_ABR_LADDER", "480:1500,720:2000,720:4000,1080:8000,1080:12000,1080:20000");

    /// <summary>
    /// Gets a value indicating whether a quality level has gone idle.
    /// </summary>
    /// <param name="lastActivityUtc">When a segment of the level was last requested.</param>
    /// <param name="nowUtc">The current time.</param>
    /// <returns><c>true</c> if the level has been idle longer than <see cref="IdleLevelSeconds"/>.</returns>
    public static bool IsIdle(DateTime lastActivityUtc, DateTime nowUtc)
        => (nowUtc - lastActivityUtc).TotalSeconds > IdleLevelSeconds;

    /// <summary>
    /// Gets a value indicating whether a level id is well formed. Only short alphanumeric ids are accepted,
    /// because the id ends up in transcode file names.
    /// </summary>
    /// <param name="level">The level id.</param>
    /// <returns><c>true</c> if the id is valid.</returns>
    public static bool IsValidLevel(string? level)
    {
        if (string.IsNullOrEmpty(level) || level.Length > 16)
        {
            return false;
        }

        foreach (var c in level)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ReadBool(string name, bool fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return bool.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static int ReadInt(string name, int fallback, int min, int max)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, min, max)
            : fallback;
    }

    private static double ReadDouble(string name, double fallback, double min, double max)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, min, max)
            : fallback;
    }

    private static IReadOnlyList<(int Height, int VideoBitrate)> ReadLadder(string name, string fallback)
    {
        var parsed = ParseLadder(Environment.GetEnvironmentVariable(name));
        return parsed.Count > 0 ? parsed : ParseLadder(fallback);
    }

    private static List<(int Height, int VideoBitrate)> ParseLadder(string? value)
    {
        var levels = new List<(int Height, int VideoBitrate)>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return levels;
        }

        foreach (var entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kbps)
                && height >= 144
                && kbps >= 100)
            {
                levels.Add((height, kbps * 1000));
            }
        }

        levels.Sort((a, b) => a.Height != b.Height ? a.Height.CompareTo(b.Height) : a.VideoBitrate.CompareTo(b.VideoBitrate));
        return levels;
    }
}
