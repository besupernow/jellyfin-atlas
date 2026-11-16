using System;

namespace Jellyfin.Api.Models.AtlasDtos;

/// <summary>
/// Atlas: one user's music settings, as an administrator sees them.
/// </summary>
public class AtlasMusicUserDto
{
    /// <summary>
    /// Gets or sets the Jellyfin user id.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin user name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the user's ListenBrainz username, or null when none is set.
    /// </summary>
    public string? ListenBrainzUserName { get; set; }
}
