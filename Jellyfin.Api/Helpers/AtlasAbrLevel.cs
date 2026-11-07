namespace Jellyfin.Api.Helpers;

/// <summary>
/// One quality level of an Atlas ABR ladder.
/// </summary>
internal sealed class AtlasAbrLevel
{
    /// <summary>
    /// Gets the level id, sent back as <c>AtlasLevel</c>.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets a value indicating whether this level is the original file (video stream copy).
    /// </summary>
    public bool IsOriginal { get; init; }

    /// <summary>
    /// Gets the output width.
    /// </summary>
    public int Width { get; init; }

    /// <summary>
    /// Gets the output height.
    /// </summary>
    public int Height { get; init; }

    /// <summary>
    /// Gets the target video bitrate of a transcoded level, in bits per second.
    /// </summary>
    public int VideoBitrate { get; init; }
}
