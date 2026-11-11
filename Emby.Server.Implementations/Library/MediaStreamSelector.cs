#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Model.Entities;

namespace Emby.Server.Implementations.Library
{
    public static class MediaStreamSelector
    {
        public static int? GetDefaultAudioStreamIndex(IReadOnlyList<MediaStream> streams, IReadOnlyList<string> preferredLanguages, bool preferDefaultTrack)
        {
            var sortedStreams = GetSortedStreams(streams, MediaStreamType.Audio, preferredLanguages).ToList();

            if (preferDefaultTrack)
            {
                var defaultStream = sortedStreams.FirstOrDefault(i => i.IsDefault);

                if (defaultStream is not null)
                {
                    return defaultStream.Index;
                }
            }

            return sortedStreams.FirstOrDefault()?.Index;
        }

        public static int? GetDefaultSubtitleStreamIndex(
            IEnumerable<MediaStream> streams,
            IReadOnlyList<string> preferredLanguages,
            SubtitlePlaybackMode mode,
            string audioTrackLanguage)
        {
            if (mode == SubtitlePlaybackMode.None)
            {
                return null;
            }

            // Sort in the following order: Default > No tag > Forced
            var sortedStreams = streams
                .Where(i => i.Type == MediaStreamType.Subtitle)
                .OrderByDescending(x => x.IsExternal)
                .ThenByDescending(x => x.IsDefault)
                .ThenByDescending(x => !x.IsForced && MatchesPreferredLanguage(x.Language, preferredLanguages))
                .ThenByDescending(x => x.IsForced && MatchesPreferredLanguage(x.Language, preferredLanguages))
                .ThenByDescending(x => x.IsForced && IsLanguageUndefined(x.Language))
                .ThenByDescending(x => x.IsForced)
                .ToList();

            MediaStream? stream = null;

            if (mode == SubtitlePlaybackMode.Default)
            {
                // Load subtitles according to default and forced flags.
                // Atlas: not merely for being external. Every text subtitle in the Atlas library is a
                // sidecar file (Tdarr extracts them), so "external" no longer means someone added it on
                // purpose, and stock Jellyfin would turn on whichever sidecar sorts first.
                stream = sortedStreams.FirstOrDefault(x => x.IsDefault || x.IsForced);
            }
            else if (mode == SubtitlePlaybackMode.Smart)
            {
                // Only attempt to load subtitles if the audio language is not one of the user's preferred subtitle languages.
                // If no subtitles of preferred language available, use none.
                // If the audio language is one of the user's preferred subtitle languages behave like OnlyForced.
                if (!preferredLanguages.Contains(audioTrackLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    stream = sortedStreams.FirstOrDefault(x => MatchesPreferredLanguage(x.Language, preferredLanguages));
                }
                else
                {
                    stream = BehaviorOnlyForced(sortedStreams, preferredLanguages).FirstOrDefault();
                }
            }
            else if (mode == SubtitlePlaybackMode.Always)
            {
                // Always load (full/non-forced) subtitles of the user's preferred subtitle language if possible, otherwise OnlyForced behaviour.
                stream = sortedStreams.FirstOrDefault(x => !x.IsForced && MatchesPreferredLanguage(x.Language, preferredLanguages)) ??
                    BehaviorOnlyForced(sortedStreams, preferredLanguages).FirstOrDefault();
            }
            else if (mode == SubtitlePlaybackMode.OnlyForced)
            {
                // Load subtitles that are flagged forced of the user's preferred subtitle language or with an undefined language
                stream = BehaviorOnlyForced(sortedStreams, preferredLanguages).FirstOrDefault();
            }

            return PreferSidecar(streams, stream?.Index);
        }

        /// <summary>
        /// Atlas: swaps an embedded subtitle for its sidecar copy (same language, same forced flag,
        /// not SDH) when the file has one. Reading an embedded subtitle means extracting it from
        /// the whole video, which competes with the transcode for the disk and stalls the start;
        /// the sidecar is a small file read.
        /// </summary>
        /// <param name="streams">The item's streams.</param>
        /// <param name="index">The chosen subtitle index, or null/-1 for none.</param>
        /// <returns>The index to use.</returns>
        public static int? PreferSidecar(IEnumerable<MediaStream> streams, int? index)
        {
            if (index is null or < 0)
            {
                return index;
            }

            var subtitles = streams.Where(i => i.Type == MediaStreamType.Subtitle).ToList();
            var chosen = subtitles.FirstOrDefault(i => i.Index == index);
            if (chosen is null || chosen.IsExternal || string.IsNullOrEmpty(chosen.Language))
            {
                return index;
            }

            var sidecar = subtitles
                .Where(i => i.IsExternal
                    && i.IsForced == chosen.IsForced
                    && SameLanguage(i.Language, chosen.Language))
                .OrderBy(i => i.IsHearingImpaired == chosen.IsHearingImpaired ? 0 : 1)
                .ThenBy(i => i.Index)
                .FirstOrDefault();
            return sidecar?.Index ?? index;
        }

        private static bool SameLanguage(string? a, string? b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            {
                return false;
            }

            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // eng/en, fre/fra/fr, ger/deu/de: the same language under its two- and three-letter codes.
            static string Key(string code)
            {
                var lower = code.Split('-')[0].ToLowerInvariant();
                return lower switch
                {
                    "eng" => "en",
                    "spa" => "es",
                    "fre" or "fra" => "fr",
                    "ger" or "deu" => "de",
                    "kor" => "ko",
                    "jpn" => "ja",
                    "ita" => "it",
                    "por" => "pt",
                    "chi" or "zho" => "zh",
                    "dut" or "nld" => "nl",
                    "rus" => "ru",
                    "ara" => "ar",
                    "baq" or "eus" => "eu",
                    _ => lower,
                };
            }

            return string.Equals(Key(a), Key(b), StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<MediaStream> GetSortedStreams(IEnumerable<MediaStream> streams, MediaStreamType type, IReadOnlyList<string> languagePreferences)
        {
            // Give some preference to external text subs for better performance
            return streams
                .Where(i => i.Type == type)
                .OrderByDescending(i => GetStreamScore(i, languagePreferences));
        }

        public static void SetSubtitleStreamScores(
            IReadOnlyList<MediaStream> streams,
            IReadOnlyList<string> preferredLanguages,
            SubtitlePlaybackMode mode,
            string audioTrackLanguage)
        {
            if (mode == SubtitlePlaybackMode.None)
            {
                return;
            }

            var sortedStreams = GetSortedStreams(streams, MediaStreamType.Subtitle, preferredLanguages).ToList();

            List<MediaStream>? filteredStreams = null;

            if (mode == SubtitlePlaybackMode.Default)
            {
                // Prefer embedded metadata over smart logic
                // Load subtitles according to default and forced flags (Atlas: not external alone, see above).
                filteredStreams = sortedStreams.Where(s => s.IsDefault || s.IsForced)
                    .ToList();
            }
            else if (mode == SubtitlePlaybackMode.Smart)
            {
                // Prefer smart logic over embedded metadata
                // Only attempt to load subtitles if the audio language is not one of the user's preferred subtitle languages, otherwise OnlyForced behavior.
                if (!preferredLanguages.Contains(audioTrackLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    filteredStreams = sortedStreams.Where(s => MatchesPreferredLanguage(s.Language, preferredLanguages))
                        .ToList();
                }
                else
                {
                    filteredStreams = BehaviorOnlyForced(sortedStreams, preferredLanguages);
                }
            }
            else if (mode == SubtitlePlaybackMode.Always)
            {
                // Always load (full/non-forced) subtitles of the user's preferred subtitle language if possible, otherwise OnlyForced behavior.
                filteredStreams = sortedStreams.Where(s => !s.IsForced && MatchesPreferredLanguage(s.Language, preferredLanguages))
                    .ToList() ?? BehaviorOnlyForced(sortedStreams, preferredLanguages);
            }
            else if (mode == SubtitlePlaybackMode.OnlyForced)
            {
                // Load subtitles that are flagged forced of the user's preferred subtitle language or with an undefined language
                filteredStreams = BehaviorOnlyForced(sortedStreams, preferredLanguages);
            }

            // If filteredStreams is null, initialize it as an empty list to avoid null reference errors
            filteredStreams ??= new List<MediaStream>();

            foreach (var stream in filteredStreams)
            {
                stream.Score = GetStreamScore(stream, preferredLanguages);
            }
        }

        private static bool MatchesPreferredLanguage(string language, IReadOnlyList<string> preferredLanguages)
        {
            // If preferredLanguages is empty, treat it as "any language" (wildcard)
            return preferredLanguages.Count == 0 ||
                preferredLanguages.Contains(language, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLanguageUndefined(string language)
        {
            // Check for null, empty, or known placeholders
            return string.IsNullOrEmpty(language) ||
                language.Equals("und", StringComparison.OrdinalIgnoreCase) ||
                language.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
                language.Equals("undetermined", StringComparison.OrdinalIgnoreCase) ||
                language.Equals("mul", StringComparison.OrdinalIgnoreCase) ||
                language.Equals("zxx", StringComparison.OrdinalIgnoreCase);
        }

        private static List<MediaStream> BehaviorOnlyForced(IEnumerable<MediaStream> sortedStreams, IReadOnlyList<string> preferredLanguages)
        {
            return sortedStreams
                .Where(s => s.IsForced && (MatchesPreferredLanguage(s.Language, preferredLanguages) || IsLanguageUndefined(s.Language)))
                .OrderByDescending(s => MatchesPreferredLanguage(s.Language, preferredLanguages))
                .ThenByDescending(s => IsLanguageUndefined(s.Language))
                .ToList();
        }

        internal static int GetStreamScore(MediaStream stream, IReadOnlyList<string> languagePreferences)
        {
            var index = languagePreferences.FindIndex(x => string.Equals(x, stream.Language, StringComparison.OrdinalIgnoreCase));
            var score = index == -1 ? 1 : 101 - index;
            score = (score * 10) + (stream.IsForced ? 2 : 1);
            score = (score * 10) + (stream.IsDefault ? 2 : 1);
            score = (score * 10) + (stream.SupportsExternalStream ? 2 : 1);
            score = (score * 10) + (stream.IsTextSubtitleStream ? 2 : 1);
            score = (score * 10) + (stream.IsExternal ? 2 : 1);
            return score;
        }
    }
}
