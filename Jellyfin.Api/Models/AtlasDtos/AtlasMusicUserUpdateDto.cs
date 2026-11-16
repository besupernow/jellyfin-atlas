namespace Jellyfin.Api.Models.AtlasDtos;

/// <summary>
/// Atlas: an administrator's change to a user's music settings.
/// </summary>
public class AtlasMusicUserUpdateDto
{
    /// <summary>
    /// Gets or sets the ListenBrainz username. Null or empty clears it.
    /// </summary>
    public string? ListenBrainzUserName { get; set; }
}
