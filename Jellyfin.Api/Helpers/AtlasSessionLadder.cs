using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Jellyfin.Api.Helpers;

/// <summary>
/// Atlas ABR: what a playback session's master playlist handed out (level ids, each level's variant
/// query, the segment boundaries and the player's credentials), kept so segment requests can fetch
/// ahead on the player's behalf: the start segment at playlist time, and the next level up while the
/// player is still on the one below.
/// </summary>
internal sealed class AtlasSessionLadder
{
    private static readonly ConcurrentDictionary<string, AtlasSessionLadder> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan _lifetime = TimeSpan.FromHours(12);
    private static readonly TimeSpan _warmRetry = TimeSpan.FromSeconds(30);

    private readonly Lock _warmLock = new();
    private string? _warmLevel;
    private DateTime _warmAtUtc;

    /// <summary>
    /// Initializes a new instance of the <see cref="AtlasSessionLadder"/> class.
    /// </summary>
    /// <param name="itemId">The item id (N format).</param>
    /// <param name="container">The segment container extension.</param>
    /// <param name="levelIds">The level ids, lowest first.</param>
    /// <param name="isOriginal">Whether each level is the original.</param>
    /// <param name="queries">Each level's variant query string.</param>
    /// <param name="lengths">The segment lengths, in seconds.</param>
    /// <param name="headers">The player's credential and user agent headers.</param>
    public AtlasSessionLadder(
        string itemId,
        string container,
        string[] levelIds,
        bool[] isOriginal,
        string[] queries,
        IReadOnlyList<double> lengths,
        IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        ItemId = itemId;
        Container = container;
        LevelIds = levelIds;
        IsOriginal = isOriginal;
        Queries = queries;
        Headers = headers;
        CreatedUtc = DateTime.UtcNow;

        // Ticks summed exactly as the playlist generator does, so runtimeTicks / actualSegmentLengthTicks
        // match what the player sends for the same segment.
        SegmentStartTicks = new long[lengths.Count];
        SegmentLengthTicks = new long[lengths.Count];
        long position = 0;
        for (var i = 0; i < lengths.Count; i++)
        {
            var length = Convert.ToInt64(lengths[i] * TimeSpan.TicksPerSecond);
            SegmentStartTicks[i] = position;
            SegmentLengthTicks[i] = length;
            position += length;
        }
    }

    /// <summary>Gets the item id (N format).</summary>
    public string ItemId { get; }

    /// <summary>Gets the segment container extension.</summary>
    public string Container { get; }

    /// <summary>Gets the level ids, lowest first.</summary>
    public string[] LevelIds { get; }

    /// <summary>Gets whether each level is the original.</summary>
    public bool[] IsOriginal { get; }

    /// <summary>Gets each level's variant query string.</summary>
    public string[] Queries { get; }

    /// <summary>Gets the player's credential and user agent headers.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; }

    /// <summary>Gets each segment's start, in ticks.</summary>
    public long[] SegmentStartTicks { get; }

    /// <summary>Gets each segment's length, in ticks.</summary>
    public long[] SegmentLengthTicks { get; }

    /// <summary>Gets the number of segments.</summary>
    public int SegmentCount => SegmentStartTicks.Length;

    /// <summary>Gets when this was stored.</summary>
    public DateTime CreatedUtc { get; }

    /// <summary>
    /// Stores a session's ladder, replacing an earlier one (a reload asks for a new master) and
    /// dropping ones old enough that the session must be over.
    /// </summary>
    /// <param name="playSessionId">The play session id.</param>
    /// <param name="ladder">The ladder.</param>
    public static void Store(string playSessionId, AtlasSessionLadder ladder)
    {
        _sessions[playSessionId] = ladder;
        var cutoff = DateTime.UtcNow - _lifetime;
        foreach (var entry in _sessions)
        {
            if (entry.Value.CreatedUtc < cutoff)
            {
                _sessions.TryRemove(entry.Key, out _);
            }
        }
    }

    /// <summary>
    /// Gets a session's ladder.
    /// </summary>
    /// <param name="playSessionId">The play session id.</param>
    /// <param name="ladder">The ladder, if stored.</param>
    /// <returns><c>true</c> if found.</returns>
    public static bool TryGet(string playSessionId, [NotNullWhen(true)] out AtlasSessionLadder? ladder)
        => _sessions.TryGetValue(playSessionId, out ladder);

    /// <summary>
    /// Gets the segment holding <paramref name="ticks"/>.
    /// </summary>
    /// <param name="ticks">A position, in ticks.</param>
    /// <returns>The segment index.</returns>
    public int SegmentAt(long ticks)
    {
        for (var i = SegmentCount - 1; i > 0; i--)
        {
            if (SegmentStartTicks[i] <= ticks)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>
    /// Claims the right to warm <paramref name="level"/>. Each segment the player fetches would ask
    /// again; this keeps it to one warm-up per level at a time (retried after a while if the job died).
    /// </summary>
    /// <param name="level">The level to warm.</param>
    /// <returns><c>true</c> if the caller should start it.</returns>
    public bool TryClaimWarm(string level)
    {
        lock (_warmLock)
        {
            var now = DateTime.UtcNow;
            if (string.Equals(_warmLevel, level, StringComparison.Ordinal) && now - _warmAtUtc < _warmRetry)
            {
                return false;
            }

            _warmLevel = level;
            _warmAtUtc = now;
            return true;
        }
    }
}
