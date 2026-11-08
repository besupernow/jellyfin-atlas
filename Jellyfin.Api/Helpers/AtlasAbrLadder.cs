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

        // Rungs are named by the 16:9 frame they fit (720 = 1280 wide), so a scope film's
        // 1920x800 counts as 1080 and its 720 rung is 1280x534, not 1720x720.
        var equivalentHeight = Math.Max(sourceHeight, (int)Math.Round(sourceWidth * 9.0 / 16.0));

        foreach (var (height, bitrate) in AtlasAbrSettings.Ladder)
        {
            // No rung bigger than the source.
            if (height > equivalentHeight + 8)
            {
                continue;
            }

            // A rung needs clearly fewer bits than the original, or it's a worse copy of it.
            // This keeps a same-resolution rung (1080 at 7 Mbps under a 15 Mbps 1080p original),
            // which is the step players need between 720 and the original.
            if (sourceVideoBitrate > 0 && bitrate > sourceVideoBitrate * 0.65)
            {
                continue;
            }

            var scale = Math.Min(1.0, (double)height / equivalentHeight);
            levels.Add(new AtlasAbrLevel
            {
                // Height and bitrate: the ladder can hold two rungs of one height (720 at 2 and 4 Mbps).
                Id = "h" + height.ToString(CultureInfo.InvariantCulture) + "b" + (bitrate / 1000).ToString(CultureInfo.InvariantCulture),
                Width = Even(sourceWidth * scale),
                Height = Even(sourceHeight * scale),
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
            // A full resolution rung that is nearly the top transcode only doubles the work.
            var topBitrate = GetTopBitrate(sourceHeight, sourceVideoBitrate);
            levels.RemoveAll(level => level.Height >= sourceHeight - 8 && level.VideoBitrate >= topBitrate * 0.8);
            levels.Add(new AtlasAbrLevel
            {
                Id = AtlasAbrSettings.TopLevel,
                Width = sourceWidth,
                Height = sourceHeight,
                VideoBitrate = topBitrate
            });
        }

        return levels;
    }

    /// <summary>
    /// Builds a single-level "ladder" for a quality picked by hand: the original when it already fits
    /// the pick (and can be stream copied), otherwise one transcode at the picked height and bitrate.
    /// </summary>
    /// <param name="sourceWidth">The source width.</param>
    /// <param name="sourceHeight">The source height.</param>
    /// <param name="sourceVideoBitrate">The source's average video bitrate, or 0 if unknown.</param>
    /// <param name="includeOriginal">Whether the original can be a level (stream copy, keyframes aligned).</param>
    /// <param name="maxHeight">The picked height (16:9 frame height: 720, 1080).</param>
    /// <param name="maxBitrate">The picked video bitrate, in bits per second.</param>
    /// <returns>The one level, or none if the source size is unknown.</returns>
    public static IReadOnlyList<AtlasAbrLevel> Pin(int sourceWidth, int sourceHeight, long sourceVideoBitrate, bool includeOriginal, int maxHeight, int maxBitrate)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return [];
        }

        var equivalentHeight = Math.Max(sourceHeight, (int)Math.Round(sourceWidth * 9.0 / 16.0));
        if (includeOriginal && sourceVideoBitrate > 0 && sourceVideoBitrate <= maxBitrate && equivalentHeight <= maxHeight + 8)
        {
            return
            [
                new AtlasAbrLevel
                {
                    Id = AtlasAbrSettings.OriginalLevel,
                    IsOriginal = true,
                    Width = sourceWidth,
                    Height = sourceHeight,
                    VideoBitrate = (int)Math.Min(sourceVideoBitrate, int.MaxValue)
                }
            ];
        }

        var height = Math.Min(maxHeight, equivalentHeight);
        var scale = Math.Min(1.0, (double)height / equivalentHeight);

        // Never more bits than the original has: that only makes a bigger copy of it.
        var bitrate = sourceVideoBitrate > 0 ? (int)Math.Min(maxBitrate, sourceVideoBitrate) : maxBitrate;
        return
        [
            new AtlasAbrLevel
            {
                Id = "p" + height.ToString(CultureInfo.InvariantCulture) + "b" + (bitrate / 1000).ToString(CultureInfo.InvariantCulture),
                Width = Even(sourceWidth * scale),
                Height = Even(sourceHeight * scale),
                VideoBitrate = bitrate
            }
        ];
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

    private static int Even(double value)
    {
        // The encoder wants even dimensions.
        return Math.Max(2, (int)Math.Round(value / 2) * 2);
    }
}
