namespace Jellyfin.Api.Models.StreamingDtos;

/// <summary>
/// Atlas ABR: the answer to a warm-up request.
/// </summary>
public class AtlasWarmResult
{
    /// <summary>
    /// Gets or sets a value indicating whether the level is far enough ahead for the player to switch to it now.
    /// </summary>
    public bool Ready { get; set; }
}
