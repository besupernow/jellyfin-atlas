using System;
using System.Collections.Generic;
using System.Globalization;
using MediaBrowser.Controller.Streaming;

namespace Jellyfin.Api.Helpers;

/// <summary>
/// Builds the Atlas ABR quality ladder for a source.
/// </summary>
internal static class AtlasAbrLadder
{
    /// <summary>
    /// The highest bitrate a full resolution transcode is given, in bits per second.
    /// </summary>
    private const int MaxTopBitrate = 20_000_000;

    /// <summary>
    /// Builds the ladder, lowest level first.
    /// </summary>
    /// <param name="sourceWidth">The source width.</param>
    /// <param name="sourceHeight">The source height.</param>
    /// <param name="sourceVideoBitrate">The source's average video bitrate, or 0 if unknown.</param>
    /// <param name="includeOriginal">Whether the top level is the original file (stream copy) instead of a full resolution transcode.</param>
    /// <returns>The levels, lowest first.</returns>
    public static IReadOnlyList<AtlasAbrLevel> Build(int sourceWidth, int sourceHeight, long sourceVideoBitrate, bool includeOriginal)
    {
        var levels = new List<AtlasAbrLevel>();
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return levels;
        }

        foreach (var (height, bitrate) in AtlasAbrSettings.Ladder)
        {
            // Only levels that are actually smaller than the source: a transcode at the source's own
            // resolution (or with more bits than the source has) is just a worse copy of the original.
            if (height > sourceHeight - 32)
            {
                continue;
            }

            if (sourceVideoBitrate > 0 && bitrate > sourceVideoBitrate * 0.9)
            {
                continue;
            }

            levels.Add(new AtlasAbrLevel
            {
                Id = "h" + height.ToString(CultureInfo.InvariantCulture),
                Width = ScaleWidth(sourceWidth, sourceHeight, height),
                Height = height,
                VideoBitrate = bitrate
            });
        }

        if (includeOriginal)
        {
            levels.Add(new AtlasAbrLevel
            {
                Id = AtlasAbrSettings.OriginalLevel,
                IsOriginal = true,
                Width = sourceWidth,
                Height = sourceHeight,
                VideoBitrate = (int)Math.Min(sourceVideoBitrate, int.MaxValue)
            });
        }
        else
        {
            levels.Add(new AtlasAbrLevel
            {
                Id = AtlasAbrSettings.TopLevel,
                Width = sourceWidth,
                Height = sourceHeight,
                VideoBitrate = GetTopBitrate(sourceHeight, sourceVideoBitrate)
            });
        }

        return levels;
    }

    private static int GetTopBitrate(int sourceHeight, long sourceVideoBitrate)
    {
        if (sourceVideoBitrate > 0)
        {
            return (int)Math.Min(sourceVideoBitrate, MaxTopBitrate);
        }

        // Unknown source bitrate: use the ladder entry for this height, or scale the tallest one by pixel count.
        var ladder = AtlasAbrSettings.Ladder;
        foreach (var (height, bitrate) in ladder)
        {
            if (height >= sourceHeight)
            {
                return bitrate;
            }
        }

        var (topHeight, topBitrate) = ladder[^1];
        var scale = (double)sourceHeight / topHeight;
        return (int)Math.Min(topBitrate * scale * scale, MaxTopBitrate);
    }

    private static int ScaleWidth(int sourceWidth, int sourceHeight, int height)
    {
        // Keep the aspect ratio, rounded to an even number for the encoder.
        var width = (double)sourceWidth * height / sourceHeight;
        return Math.Max(2, (int)Math.Round(width / 2) * 2);
    }
}
