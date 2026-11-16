using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Api.Extensions;
using Jellyfin.Api.Models.AtlasDtos;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Atlas: music mode's per-user settings. Each user's ListenBrainz username lives with their
/// Jellyfin account (in the user preferences table), so the Atlas app never has to ask for it:
/// an administrator sets it once and every device signed in as that user picks it up.
/// </summary>
[Route("Atlas/Music")]
[Tags("Atlas")]
public class AtlasMusicController : BaseJellyfinApiController
{
    /// <summary>
    /// ListenBrainz (MusicBrainz) usernames are at most 64 characters.
    /// </summary>
    private const int MaxUserNameLength = 64;

    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="AtlasMusicController"/> class.
    /// </summary>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    public AtlasMusicController(IUserManager userManager)
    {
        _userManager = userManager;
    }

    /// <summary>
    /// Gets the signed-in user's music settings.
    /// </summary>
    /// <response code="200">Settings returned.</response>
    /// <response code="404">User not found.</response>
    /// <returns>The user's <see cref="AtlasMusicProfileDto"/>.</returns>
    [HttpGet("Me")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<AtlasMusicProfileDto> GetMyProfile()
    {
        var user = _userManager.GetUserById(User.GetUserId());
        if (user is null)
        {
            return NotFound();
        }

        return new AtlasMusicProfileDto { ListenBrainzUserName = ListenBrainzUserName(user) };
    }

    /// <summary>
    /// Gets every user's music settings.
    /// </summary>
    /// <response code="200">Users returned.</response>
    /// <returns>An <see cref="AtlasMusicUserDto"/> per user, by name.</returns>
    [HttpGet("Users")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<AtlasMusicUserDto>> GetUsers()
    {
        var users = _userManager.GetUsers()
            .OrderBy(user => user.Username, StringComparer.OrdinalIgnoreCase)
            .Select(user => new AtlasMusicUserDto
            {
                UserId = user.Id,
                Name = user.Username,
                ListenBrainzUserName = ListenBrainzUserName(user)
            })
            .ToList();
        return Ok(users);
    }

    /// <summary>
    /// Sets a user's music settings.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="update">The new settings.</param>
    /// <response code="204">Settings saved.</response>
    /// <response code="400">The ListenBrainz username isn't valid.</response>
    /// <response code="404">User not found.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Users/{userId}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> UpdateUser([FromRoute, Required] Guid userId, [FromBody, Required] AtlasMusicUserUpdateDto update)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return NotFound();
        }

        var name = update.ListenBrainzUserName?.Trim() ?? string.Empty;
        if (name.Length > MaxUserNameLength || name.Any(c => char.IsControl(c) || c == '|' || c == '/'))
        {
            return BadRequest("That isn't a ListenBrainz username.");
        }

        string[] values = name.Length == 0 ? [] : [name];
        user.SetPreference(PreferenceKind.AtlasListenBrainzUserName, values);
        await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
        return NoContent();
    }

    private static string? ListenBrainzUserName(User user)
    {
        var values = user.GetPreference(PreferenceKind.AtlasListenBrainzUserName);
        return values.Length == 0 || string.IsNullOrWhiteSpace(values[0]) ? null : values[0];
    }
}
