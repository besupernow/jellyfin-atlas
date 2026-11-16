namespace Jellyfin.Api.Models.AtlasDtos;

/// <summary>
/// Atlas: what music mode needs to know about the signed-in user.
/// </summary>
public class AtlasMusicProfileDto
{
    /// <summary>
    /// Gets or sets the user's ListenBrainz username, or null when none is set.
    /// </summary>
    public string? ListenBrainzUserName { get; set; }
}
