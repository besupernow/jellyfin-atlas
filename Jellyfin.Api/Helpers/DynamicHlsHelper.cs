using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Controllers;
using Jellyfin.Api.Extensions;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using Jellyfin.MediaEncoding.Hls.Atlas;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Api.Helpers;

/// <summary>
/// Dynamic hls helper.
/// </summary>
public class DynamicHlsHelper
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ITranscodeManager _transcodeManager;
    private readonly INetworkManager _networkManager;
    private readonly ILogger<DynamicHlsHelper> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly EncodingHelper _encodingHelper;
    private readonly ITrickplayManager _trickplayManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServerApplicationHost _serverApplicationHost;

    /// <summary>
    /// Initializes a new instance of the <see cref="DynamicHlsHelper"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="mediaSourceManager">Instance of the <see cref="IMediaSourceManager"/> interface.</param>
    /// <param name="serverConfigurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    /// <param name="mediaEncoder">Instance of the <see cref="IMediaEncoder"/> interface.</param>
    /// <param name="transcodeManager">Instance of <see cref="ITranscodeManager"/>.</param>
    /// <param name="networkManager">Instance of the <see cref="INetworkManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{DynamicHlsHelper}"/> interface.</param>
    /// <param name="httpContextAccessor">Instance of the <see cref="IHttpContextAccessor"/> interface.</param>
    /// <param name="encodingHelper">Instance of <see cref="EncodingHelper"/>.</param>
    /// <param name="trickplayManager">Instance of <see cref="ITrickplayManager"/>.</param>
    /// <param name="httpClientFactory">Instance of <see cref="IHttpClientFactory"/>, for the Atlas start segment prewarm.</param>
    /// <param name="serverApplicationHost">Instance of <see cref="IServerApplicationHost"/>, for this server's local address.</param>
    public DynamicHlsHelper(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IMediaSourceManager mediaSourceManager,
        IServerConfigurationManager serverConfigurationManager,
        IMediaEncoder mediaEncoder,
        ITranscodeManager transcodeManager,
        INetworkManager networkManager,
        ILogger<DynamicHlsHelper> logger,
        IHttpContextAccessor httpContextAccessor,
        EncodingHelper encodingHelper,
        ITrickplayManager trickplayManager,
        IHttpClientFactory httpClientFactory,
        IServerApplicationHost serverApplicationHost)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _mediaSourceManager = mediaSourceManager;
        _serverConfigurationManager = serverConfigurationManager;
        _mediaEncoder = mediaEncoder;
        _transcodeManager = transcodeManager;
        _networkManager = networkManager;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
        _encodingHelper = encodingHelper;
        _trickplayManager = trickplayManager;
        _httpClientFactory = httpClientFactory;
        _serverApplicationHost = serverApplicationHost;
    }

    /// <summary>
    /// Get master hls playlist.
    /// </summary>
    /// <param name="transcodingJobType">Transcoding job type.</param>
    /// <param name="streamingRequest">Streaming request dto.</param>
    /// <param name="enableAdaptiveBitrateStreaming">Enable adaptive bitrate streaming.</param>
    /// <returns>A <see cref="Task"/> containing the resulting <see cref="ActionResult"/>.</returns>
    public async Task<ActionResult> GetMasterHlsPlaylist(
        TranscodingJobType transcodingJobType,
        StreamingRequestDto streamingRequest,
        bool enableAdaptiveBitrateStreaming)
    {
        var isHeadRequest = _httpContextAccessor.HttpContext?.Request.Method == WebRequestMethods.Http.Head;
        // CTS lifecycle is managed internally.
        var cancellationTokenSource = new CancellationTokenSource();
        return await GetMasterPlaylistInternal(
            streamingRequest,
            isHeadRequest,
            enableAdaptiveBitrateStreaming,
            transcodingJobType,
            cancellationTokenSource).ConfigureAwait(false);
    }

    private async Task<ActionResult> GetMasterPlaylistInternal(
        StreamingRequestDto streamingRequest,
        bool isHeadRequest,
        bool enableAdaptiveBitrateStreaming,
        TranscodingJobType transcodingJobType,
        CancellationTokenSource cancellationTokenSource)
    {
        if (_httpContextAccessor.HttpContext is null)
        {
            throw new ResourceNotFoundException(nameof(_httpContextAccessor.HttpContext));
        }

        using var state = await StreamingHelpers.GetStreamingState(
                streamingRequest,
                _httpContextAccessor.HttpContext,
                _mediaSourceManager,
                _userManager,
                _libraryManager,
                _serverConfigurationManager,
                _mediaEncoder,
                _encodingHelper,
                _transcodeManager,
                transcodingJobType,
                cancellationTokenSource.Token)
            .ConfigureAwait(false);

        _httpContextAccessor.HttpContext.Response.Headers.Append(HeaderNames.Expires, "0");
        if (isHeadRequest)
        {
            return new FileContentResult(Array.Empty<byte>(), MimeTypes.GetMimeType("playlist.m3u8"));
        }

        var totalBitrate = (state.OutputAudioBitrate ?? 0) + (state.OutputVideoBitrate ?? 0);

        var builder = new StringBuilder();

        builder.AppendLine("#EXTM3U");

        var isLiveStream = state.IsSegmentedLiveStream;

        var queryString = _httpContextAccessor.HttpContext.Request.QueryString.ToString();
        var playlistQuery = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(queryString);

        // From universal audio service: timeline is determined by segment index and runtime ticks, not StartTimeTicks
        playlistQuery.Remove("StartTimeTicks");

        // From universal audio service: override AudioCodec when the actual request differs from original query
        if (!string.Equals(state.OutputAudioCodec, _httpContextAccessor.HttpContext.Request.Query["AudioCodec"].ToString(), StringComparison.OrdinalIgnoreCase))
        {
            playlistQuery["AudioCodec"] = state.OutputAudioCodec;
        }

        // From universal audio service
        if (!string.IsNullOrWhiteSpace(state.Request.SegmentContainer))
        {
            playlistQuery.TryAdd("SegmentContainer", state.Request.SegmentContainer);
        }

        // From universal audio service
        if (!string.IsNullOrWhiteSpace(state.Request.TranscodeReasons))
        {
            playlistQuery.TryAdd("TranscodeReasons", state.Request.TranscodeReasons);
        }

        // Video rotation metadata is only supported in fMP4 remuxing
        if (state.VideoStream is not null
            && state.VideoRequest is not null
            && (state.VideoStream?.Rotation ?? 0) != 0
            && EncodingHelper.IsCopyCodec(state.OutputVideoCodec)
            && !string.IsNullOrWhiteSpace(state.Request.SegmentContainer)
            && !string.Equals(state.Request.SegmentContainer, "mp4", StringComparison.OrdinalIgnoreCase))
        {
            playlistQuery["AllowVideoStreamCopy"] = "false";
        }

        // Main stream
        var baseUrl = isLiveStream ? "live.m3u8" : "main.m3u8";
        var playlistUrl = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(baseUrl, playlistQuery);

        var subtitleStreams = state.MediaSource
            .MediaStreams
            .Where(i => i.IsTextSubtitleStream)
            .ToList();

        var subtitleGroup = subtitleStreams.Count > 0 && (state.SubtitleDeliveryMethod == SubtitleDeliveryMethod.Hls || state.VideoRequest!.EnableSubtitlesInManifest)
            ? "subs"
            : null;

        // If we're burning in subtitles then don't add additional subs to the manifest
        if (state.SubtitleStream is not null && state.SubtitleDeliveryMethod == SubtitleDeliveryMethod.Encode)
        {
            subtitleGroup = null;
        }

        if (!string.IsNullOrWhiteSpace(subtitleGroup))
        {
            AddSubtitles(state, subtitleStreams, builder, _httpContextAccessor.HttpContext.User);
        }

        // Atlas ABR: a real quality ladder for clients that ask for it. Stock clients never get here.
        if (IsAtlasAbrRequest(state, isLiveStream))
        {
            AppendAtlasLadder(builder, state, playlistQuery, baseUrl, subtitleGroup);

            if (state.VideoRequest?.EnableTrickplay ?? false)
            {
                var atlasSourceId = Guid.Parse(state.Request.MediaSourceId);
                var atlasTrickplayResolutions = await _trickplayManager.GetTrickplayResolutions(atlasSourceId).ConfigureAwait(false);
                AddTrickplay(state, atlasTrickplayResolutions, builder, _httpContextAccessor.HttpContext.User);
            }

            return new FileContentResult(Encoding.UTF8.GetBytes(builder.ToString()), MimeTypes.GetMimeType("playlist.m3u8"));
        }

        // For DoVi profiles without a compatible base layer (P5 HEVC, P10/bl0 AV1),
        // add a spec-compliant dvh1/dav1 variant before the hvc1 hack variant.
        // SUPPLEMENTAL-CODECS cannot be used for these profiles (no compatible BL to supplement).
        // The DoVi variant is listed first so spec-compliant clients (Apple TV, webOS 24+)
        // select it over the fallback when both have identical BANDWIDTH.
        // Only emit for clients that explicitly declared DOVI support to avoid breaking
        // non-compliant players that don't recognize dvh1/dav1 CODECS strings.
        if (state.VideoStream is not null
            && state.VideoRequest is not null
            && EncodingHelper.IsCopyCodec(state.OutputVideoCodec)
            && state.VideoStream.VideoRangeType == VideoRangeType.DOVI
            && state.VideoStream.DvProfile.HasValue
            && state.VideoStream.DvLevel.HasValue
            && state.GetRequestedRangeTypes(state.VideoStream.Codec)
                .Contains(VideoRangeType.DOVI.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            AppendDoviPlaylist(builder, state, playlistUrl, totalBitrate, subtitleGroup);
        }

        var basicPlaylist = AppendPlaylist(builder, state, playlistUrl, totalBitrate, subtitleGroup);

        if (state.VideoStream is not null && state.VideoRequest is not null)
        {
            var encodingOptions = _serverConfigurationManager.GetEncodingOptions();

            // Provide AV1 and HEVC SDR entrances for backward compatibility.
            foreach (var sdrVideoCodec in new[] { "av1", "hevc" })
            {
                var isAv1EncodingAllowed = encodingOptions.AllowAv1Encoding
                    && string.Equals(sdrVideoCodec, "av1", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(state.ActualOutputVideoCodec, "av1", StringComparison.OrdinalIgnoreCase);
                var isHevcEncodingAllowed = encodingOptions.AllowHevcEncoding
                    && string.Equals(sdrVideoCodec, "hevc", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(state.ActualOutputVideoCodec, "hevc", StringComparison.OrdinalIgnoreCase);
                var isEncodingAllowed = isAv1EncodingAllowed || isHevcEncodingAllowed;

                if (isEncodingAllowed
                    && EncodingHelper.IsCopyCodec(state.OutputVideoCodec)
                    && state.VideoStream.VideoRange == VideoRange.HDR)
                {
                    // Force AV1 and HEVC Main Profile and disable video stream copy.
                    state.OutputVideoCodec = sdrVideoCodec;

                    var sdrPlaylistQuery = playlistQuery;
                    sdrPlaylistQuery["VideoCodec"] = sdrVideoCodec;
                    sdrPlaylistQuery[sdrVideoCodec + "-profile"] = "main";
                    sdrPlaylistQuery["AllowVideoStreamCopy"] = "false";

                    var sdrVideoUrl = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(baseUrl, sdrPlaylistQuery);

                    // HACK: Use the same bitrate so that the client can choose by other attributes, such as color range.
                    AppendPlaylist(builder, state, sdrVideoUrl, totalBitrate, subtitleGroup);

                    // Restore the video codec
                    state.OutputVideoCodec = "copy";
                }
            }

            // Provide H.264 SDR entrance for backward compatibility.
            if (EncodingHelper.IsCopyCodec(state.OutputVideoCodec)
                && state.VideoStream.VideoRange == VideoRange.HDR)
            {
                // Force H.264 and disable video stream copy.
                state.OutputVideoCodec = "h264";

                var sdrPlaylistQuery = playlistQuery;
                sdrPlaylistQuery["VideoCodec"] = "h264";
                sdrPlaylistQuery["AllowVideoStreamCopy"] = "false";

                var sdrVideoUrl = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(baseUrl, sdrPlaylistQuery);

                // HACK: Use the same bitrate so that the client can choose by other attributes, such as color range.
                AppendPlaylist(builder, state, sdrVideoUrl, totalBitrate, subtitleGroup);

                // Restore the video codec
                state.OutputVideoCodec = "copy";
            }

            // Provide Level 5.0 entrance for backward compatibility.
            // e.g. Apple A10 chips refuse the master playlist containing SDR HEVC Main Level 5.1 video,
            // but in fact it is capable of playing videos up to Level 6.1.
            if (EncodingHelper.IsCopyCodec(state.OutputVideoCodec)
                && state.VideoStream.Level.HasValue
                && state.VideoStream.Level > 150
                && state.VideoStream.VideoRange == VideoRange.SDR
                && string.Equals(state.ActualOutputVideoCodec, "hevc", StringComparison.OrdinalIgnoreCase))
            {
                var playlistCodecsField = new StringBuilder();
                AppendPlaylistCodecsField(playlistCodecsField, state);

                // Force the video level to 5.0.
                var originalLevel = state.VideoStream.Level;
                state.VideoStream.Level = 150;
                var newPlaylistCodecsField = new StringBuilder();
                AppendPlaylistCodecsField(newPlaylistCodecsField, state);

                // Restore the video level.
                state.VideoStream.Level = originalLevel;
                var newPlaylist = ReplacePlaylistCodecsField(basicPlaylist, playlistCodecsField, newPlaylistCodecsField);
                builder.Append(newPlaylist);
            }
        }

        if (EnableAdaptiveBitrateStreaming(state, isLiveStream, enableAdaptiveBitrateStreaming, _httpContextAccessor.HttpContext.GetNormalizedRemoteIP()))
        {
            var requestedVideoBitrate = state.VideoRequest?.VideoBitRate ?? 0;

            // By default, vary by just 200k
            var variation = GetBitrateVariation(totalBitrate);

            var newBitrate = totalBitrate - variation;
            var variantQuery = playlistQuery;
            variantQuery["VideoBitrate"] = (requestedVideoBitrate - variation).ToString(CultureInfo.InvariantCulture);
            var variantUrl = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(baseUrl, variantQuery);
            AppendPlaylist(builder, state, variantUrl, newBitrate, subtitleGroup);

            variation *= 2;
            newBitrate = totalBitrate - variation;
            variantQuery["VideoBitrate"] = (requestedVideoBitrate - variation).ToString(CultureInfo.InvariantCulture);
            variantUrl = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(baseUrl, variantQuery);
            AppendPlaylist(builder, state, variantUrl, newBitrate, subtitleGroup);
        }

        if (!isLiveStream && (state.VideoRequest?.EnableTrickplay ?? false))
        {
            var sourceId = Guid.Parse(state.Request.MediaSourceId);
            var trickplayResolutions = await _trickplayManager.GetTrickplayResolutions(sourceId).ConfigureAwait(false);
            AddTrickplay(state, trickplayResolutions, builder, _httpContextAccessor.HttpContext.User);
        }

        return new FileContentResult(Encoding.UTF8.GetBytes(builder.ToString()), MimeTypes.GetMimeType("playlist.m3u8"));
    }

    private StringBuilder AppendPlaylist(StringBuilder builder, StreamState state, string url, int bitrate, string? subtitleGroup)
    {
        var playlistBuilder = new StringBuilder();
        playlistBuilder.Append("#EXT-X-STREAM-INF:BANDWIDTH=")
            .Append(bitrate.ToString(CultureInfo.InvariantCulture))
            .Append(",AVERAGE-BANDWIDTH=")
            .Append(bitrate.ToString(CultureInfo.InvariantCulture));

        AppendPlaylistVideoRangeField(playlistBuilder, state);

        AppendPlaylistCodecsField(playlistBuilder, state);

        AppendPlaylistSupplementalCodecsField(playlistBuilder, state);

        AppendPlaylistResolutionField(playlistBuilder, state);

        AppendPlaylistFramerateField(playlistBuilder, state);

        if (!string.IsNullOrWhiteSpace(subtitleGroup))
        {
            playlistBuilder.Append(",SUBTITLES=\"")
                .Append(subtitleGroup)
                .Append('"');
        }

        playlistBuilder.Append(Environment.NewLine);
        playlistBuilder.AppendLine(url);
        builder.Append(playlistBuilder);

        return playlistBuilder;
    }

    /// <summary>
    /// Appends a Dolby Vision variant with dvh1/dav1 CODECS for profiles without a compatible
    /// base layer (P5 HEVC, P10/bl0 AV1). This enables spec-compliant HLS clients to detect
    /// DoVi from the manifest rather than relying on init segment inspection.
    /// </summary>
    /// <param name="builder">StringBuilder for the master playlist.</param>
    /// <param name="state">StreamState of the current stream.</param>
    /// <param name="url">Playlist URL for this variant.</param>
    /// <param name="bitrate">Bitrate for the BANDWIDTH field.</param>
    /// <param name="subtitleGroup">Subtitle group identifier, or null.</param>
    private void AppendDoviPlaylist(StringBuilder builder, StreamState state, string url, int bitrate, string? subtitleGroup)
    {
        var dvProfile = state.VideoStream.DvProfile;
        var dvLevel = state.VideoStream.DvLevel;
        if (dvProfile is null || dvLevel is null)
        {
            return;
        }

        var playlistBuilder = new StringBuilder();
        playlistBuilder.Append("#EXT-X-STREAM-INF:BANDWIDTH=")
            .Append(bitrate.ToString(CultureInfo.InvariantCulture))
            .Append(",AVERAGE-BANDWIDTH=")
            .Append(bitrate.ToString(CultureInfo.InvariantCulture));

        playlistBuilder.Append(",VIDEO-RANGE=PQ");

        var dvCodec = HlsCodecStringHelpers.GetDoviString(dvProfile.Value, dvLevel.Value, state.ActualOutputVideoCodec);

        string audioCodecs = string.Empty;
        if (!string.IsNullOrEmpty(state.ActualOutputAudioCodec))
        {
            audioCodecs = GetPlaylistAudioCodecs(state);
        }

        playlistBuilder.Append(",CODECS=\"")
            .Append(dvCodec);
        if (!string.IsNullOrEmpty(audioCodecs))
        {
            playlistBuilder.Append(',').Append(audioCodecs);
        }

        playlistBuilder.Append('"');

        AppendPlaylistResolutionField(playlistBuilder, state);
        AppendPlaylistFramerateField(playlistBuilder, state);

        if (!string.IsNullOrWhiteSpace(subtitleGroup))
        {
            playlistBuilder.Append(",SUBTITLES=\"")
                .Append(subtitleGroup)
                .Append('"');
        }

        playlistBuilder.AppendLine();
        playlistBuilder.AppendLine(url);
        builder.Append(playlistBuilder);
    }

    /// <summary>
    /// Appends a VIDEO-RANGE field containing the range of the output video stream.
    /// </summary>
    /// <seealso cref="AppendPlaylist(StringBuilder, StreamState, string, int, string)"/>
    /// <param name="builder">StringBuilder to append the field to.</param>
    /// <param name="state">StreamState of the current stream.</param>
    private void AppendPlaylistVideoRangeField(StringBuilder builder, StreamState state)
    {
        if (state.VideoStream is not null && state.VideoStream.VideoRange != VideoRange.Unknown)
        {
            var videoRange = state.VideoStream.VideoRange;
            var videoRangeType = state.VideoStream.VideoRangeType;
            if (EncodingHelper.IsCopyCodec(state.OutputVideoCodec))
            {
                if (videoRange == VideoRange.SDR)
                {
                    builder.Append(",VIDEO-RANGE=SDR");
                }

                if (videoRange == VideoRange.HDR)
                {
                    switch (videoRangeType)
                    {
                        case VideoRangeType.HLG:
                        case VideoRangeType.DOVIWithHLG:
                        case VideoRangeType.DOVIInvalid when string.Equals(state.VideoStream.ColorTransfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase):
                            builder.Append(",VIDEO-RANGE=HLG");
                            break;
                        default:
                            builder.Append(",VIDEO-RANGE=PQ");
                            break;
                    }
                }
            }
            else
            {
                // Currently we only encode to SDR.
                builder.Append(",VIDEO-RANGE=SDR");
            }
        }
    }

    /// <summary>
    /// Appends a CODECS field containing formatted strings of
    /// the active streams output video and audio codecs.
    /// </summary>
    /// <seealso cref="AppendPlaylist(StringBuilder, StreamState, string, int, string)"/>
    /// <seealso cref="GetPlaylistVideoCodecs(StreamState, string, int)"/>
    /// <seealso cref="GetPlaylistAudioCodecs(StreamState)"/>
    /// <param name="builder">StringBuilder to append the field to.</param>
    /// <param name="state">StreamState of the current stream.</param>
    private void AppendPlaylistCodecsField(StringBuilder builder, StreamState state)
    {
        // Video
        string videoCodecs = string.Empty;
        int? videoCodecLevel = GetOutputVideoCodecLevel(state);
        if (!string.IsNullOrEmpty(state.ActualOutputVideoCodec) && videoCodecLevel.HasValue)
        {
            videoCodecs = GetPlaylistVideoCodecs(state, state.ActualOutputVideoCodec, videoCodecLevel.Value);
        }

        // Audio
        string audioCodecs = string.Empty;
        if (!string.IsNullOrEmpty(state.ActualOutputAudioCodec))
        {
            audioCodecs = GetPlaylistAudioCodecs(state);
        }

        StringBuilder codecs = new StringBuilder();

        codecs.Append(videoCodecs);

        if (!string.IsNullOrEmpty(videoCodecs) && !string.IsNullOrEmpty(audioCodecs))
        {
            codecs.Append(',');
        }

        codecs.Append(audioCodecs);

        if (codecs.Length > 1)
        {
            builder.Append(",CODECS=\"")
                .Append(codecs)
                .Append('"');
        }
    }

    /// <summary>
    /// Appends a SUPPLEMENTAL-CODECS field containing formatted strings of
    /// the active streams output Dolby Vision Videos.
    /// </summary>
    /// <seealso cref="AppendPlaylist(StringBuilder, StreamState, string, int, string)"/>
    /// <seealso cref="GetPlaylistVideoCodecs(StreamState, string, int)"/>
    /// <param name="builder">StringBuilder to append the field to.</param>
    /// <param name="state">StreamState of the current stream.</param>
    private void AppendPlaylistSupplementalCodecsField(StringBuilder builder, StreamState state)
    {
        // HDR dynamic metadata currently cannot exist when transcoding
        if (!EncodingHelper.IsCopyCodec(state.OutputVideoCodec))
        {
            return;
        }

        if (EncodingHelper.IsDovi(state.VideoStream) && !_encodingHelper.IsDoviRemoved(state))
        {
            AppendDvString();
        }
        else if (EncodingHelper.IsHdr10Plus(state.VideoStream) && !_encodingHelper.IsHdr10PlusRemoved(state))
        {
            AppendHdr10PlusString();
        }

        return;

        void AppendDvString()
        {
            var dvProfile = state.VideoStream.DvProfile;
            var dvLevel = state.VideoStream.DvLevel;
            var dvRangeString = state.VideoStream.VideoRangeType switch
            {
                VideoRangeType.DOVIWithHDR10 => "db1p",
                VideoRangeType.DOVIWithHLG => "db4h",
                VideoRangeType.DOVIWithHDR10Plus => "db1p", // The HDR10+ metadata would be removed if Dovi metadata is not removed
                _ => string.Empty // Don't label Dovi with EL and SDR due to compatability issues, ignore invalid configurations
            };

            if (dvProfile is null || dvLevel is null || string.IsNullOrEmpty(dvRangeString))
            {
                return;
            }

            var dvFourCc = string.Equals(state.ActualOutputVideoCodec, "av1", StringComparison.OrdinalIgnoreCase) ? "dav1" : "dvh1";
            builder.Append(",SUPPLEMENTAL-CODECS=\"")
                .Append(dvFourCc)
                .Append('.')
                .Append(dvProfile.Value.ToString("D2", CultureInfo.InvariantCulture))
                .Append('.')
                .Append(dvLevel.Value.ToString("D2", CultureInfo.InvariantCulture))
                .Append('/')
                .Append(dvRangeString)
                .Append('"');
        }

        void AppendHdr10PlusString()
        {
            var videoCodecLevel = GetOutputVideoCodecLevel(state);
            if (string.IsNullOrEmpty(state.ActualOutputVideoCodec) || videoCodecLevel is null)
            {
                return;
            }

            var videoCodecString = GetPlaylistVideoCodecs(state, state.ActualOutputVideoCodec, videoCodecLevel.Value);
            builder.Append(",SUPPLEMENTAL-CODECS=\"")
                .Append(videoCodecString)
                .Append('/')
                .Append("cdm4")
                .Append('"');
        }
    }

    /// <summary>
    /// Appends a RESOLUTION field containing the resolution of the output stream.
    /// </summary>
    /// <seealso cref="AppendPlaylist(StringBuilder, StreamState, string, int, string)"/>
    /// <param name="builder">StringBuilder to append the field to.</param>
    /// <param name="state">StreamState of the current stream.</param>
    private void AppendPlaylistResolutionField(StringBuilder builder, StreamState state)
    {
        if (state.OutputWidth.HasValue && state.OutputHeight.HasValue)
        {
            builder.Append(",RESOLUTION=")
                .Append(state.OutputWidth.GetValueOrDefault())
                .Append('x')
                .Append(state.OutputHeight.GetValueOrDefault());
        }
    }

    /// <summary>
    /// Appends a FRAME-RATE field containing the framerate of the output stream.
    /// </summary>
    /// <seealso cref="AppendPlaylist(StringBuilder, StreamState, string, int, string)"/>
    /// <param name="builder">StringBuilder to append the field to.</param>
    /// <param name="state">StreamState of the current stream.</param>
    private void AppendPlaylistFramerateField(StringBuilder builder, StreamState state)
    {
        double? framerate = null;
        if (state.TargetFramerate.HasValue)
        {
            framerate = Math.Round(state.TargetFramerate.GetValueOrDefault(), 3);
        }
        else if (state.VideoStream?.RealFrameRate is not null)
        {
            framerate = Math.Round(state.VideoStream.RealFrameRate.GetValueOrDefault(), 3);
        }

        if (framerate.HasValue)
        {
            builder.Append(",FRAME-RATE=")
                .Append(framerate.Value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private bool IsAtlasAbrRequest(StreamState state, bool isLiveStream)
    {
        if (!AtlasAbrSettings.Enabled || isLiveStream)
        {
            return false;
        }

        var requested = _httpContextAccessor.HttpContext?.Request.Query[AtlasAbrSettings.RequestParameter].ToString();
        return bool.TryParse(requested, out var isRequested)
            && isRequested
            && state.IsOutputVideo
            && state.VideoRequest is not null
            && state.VideoStream is not null
            && (state.VideoStream.Width ?? 0) > 0
            && (state.VideoStream.Height ?? 0) > 0
            && (state.RunTimeTicks ?? 0) > 0
            && !string.IsNullOrEmpty(state.MediaPath)
            && !string.IsNullOrEmpty(state.Request.MediaSourceId);
    }

    /// <summary>
    /// Appends the Atlas ABR ladder: transcoded levels below the source, and the original file on top
    /// (stream copy) when it has been analyzed and is keyframe aligned, otherwise a full resolution transcode.
    /// </summary>
    /// <param name="builder">StringBuilder for the master playlist.</param>
    /// <param name="state">StreamState of the master request.</param>
    /// <param name="playlistQuery">The query the variant URLs are built from.</param>
    /// <param name="baseUrl">The variant playlist URL.</param>
    /// <param name="subtitleGroup">Subtitle group identifier, or null.</param>
    private void AppendAtlasLadder(StringBuilder builder, StreamState state, Dictionary<string, StringValues> playlistQuery, string baseUrl, string? subtitleGroup)
    {
        var videoStream = state.VideoStream!;
        var grid = AtlasAbrSettings.GridSeconds;
        var info = AtlasSourceAnalyzer.GetOrQueue(
            state.Request.MediaSourceId!,
            state.MediaPath,
            _mediaEncoder.ProbePath,
            GetAtlasCacheDirectory(),
            grid,
            _logger);

        // The original can only sit next to transcodes if its keyframes are on the same grid, and only
        // if the client can play it as is (the master request resolved to a video stream copy).
        var includeOriginal = EncodingHelper.IsCopyCodec(state.OutputVideoCodec) && info is { Aligned: true };
        var segments = includeOriginal ? AtlasAbrSettings.KeyframeSegments : AtlasAbrSettings.GridSegments;
        long sourceBitrate = info is { AverageVideoBitrate: > 0 } ? info.AverageVideoBitrate : videoStream.BitRate ?? 0;
        var levels = AtlasAbrLadder.Build(videoStream.Width ?? 0, videoStream.Height ?? 0, sourceBitrate, includeOriginal);

        // A quality picked by hand in the app: just that one level, still on the Atlas grid and with
        // sidecar subtitles (nothing burned in), instead of a separate classic transcode.
        var request = _httpContextAccessor.HttpContext?.Request;
        if (request is not null
            && int.TryParse(request.Query[AtlasAbrSettings.MaxBitrateParameter], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pinBitrate)
            && int.TryParse(request.Query[AtlasAbrSettings.MaxHeightParameter], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pinHeight)
            && pinBitrate > 0
            && pinHeight > 0)
        {
            levels = AtlasAbrLadder.Pin(videoStream.Width ?? 0, videoStream.Height ?? 0, sourceBitrate, includeOriginal, pinHeight, pinBitrate);
        }

        if (levels.Count == 0)
        {
            return;
        }

        var audioBitrate = string.IsNullOrEmpty(state.ActualOutputAudioCodec)
            ? 0
            : state.OutputAudioBitrate ?? state.AudioStream?.BitRate ?? 192_000;

        // Small allowance for container and segment overhead on top of the elementary streams.
        const double Overhead = 1.05;

        var variants = new List<(long Peak, string Text)>(levels.Count);
        var variantQueries = new List<string>(levels.Count);
        foreach (var level in levels)
        {
            var query = new Dictionary<string, StringValues>(playlistQuery, StringComparer.OrdinalIgnoreCase);
            query.Remove(AtlasAbrSettings.RequestParameter);
            query.Remove(AtlasAbrSettings.StartBitrateParameter);
            query.Remove(AtlasAbrSettings.StartTicksParameter);
            query.Remove(AtlasAbrSettings.MaxBitrateParameter);
            query.Remove(AtlasAbrSettings.MaxHeightParameter);
            query[AtlasAbrSettings.LevelParameter] = level.Id;
            query[AtlasAbrSettings.SegmentsParameter] = segments;
            query["SegmentLength"] = grid.ToString(CultureInfo.InvariantCulture);

            long peak;
            long average;
            var line = new StringBuilder();
            if (level.IsOriginal)
            {
                var peakVideo = info is { PeakVideoBitrate: > 0 }
                    ? info.PeakVideoBitrate
                    : (long)(sourceBitrate * AtlasAbrSettings.FallbackPeakFactor);
                peak = (long)((peakVideo + audioBitrate) * Overhead);
                average = (long)((sourceBitrate + audioBitrate) * Overhead);

                line.Append("#EXT-X-STREAM-INF:BANDWIDTH=").Append(peak.ToString(CultureInfo.InvariantCulture))
                    .Append(",AVERAGE-BANDWIDTH=").Append(average.ToString(CultureInfo.InvariantCulture));
                AppendPlaylistVideoRangeField(line, state);
                AppendPlaylistCodecsField(line, state);
                AppendPlaylistResolutionField(line, state);
            }
            else
            {
                query["VideoCodec"] = "h264";
                query["AllowVideoStreamCopy"] = "false";
                query["VideoBitrate"] = level.VideoBitrate.ToString(CultureInfo.InvariantCulture);
                query["MaxWidth"] = level.Width.ToString(CultureInfo.InvariantCulture);
                query["MaxHeight"] = level.Height.ToString(CultureInfo.InvariantCulture);
                query["h264-profile"] = "high";
                query["h264-level"] = "41";
                query.Remove("Width");
                query.Remove("Height");

                // A capped transcode can still burst a little above its target within one short
                // segment (VideoToolbox stays close to it). Overstating this makes players refuse
                // to step up, so keep it tight.
                peak = (long)(((level.VideoBitrate * 1.15) + audioBitrate) * Overhead);
                average = (long)((level.VideoBitrate + audioBitrate) * Overhead);

                line.Append("#EXT-X-STREAM-INF:BANDWIDTH=").Append(peak.ToString(CultureInfo.InvariantCulture))
                    .Append(",AVERAGE-BANDWIDTH=").Append(average.ToString(CultureInfo.InvariantCulture))
                    .Append(",VIDEO-RANGE=SDR")
                    .Append(",CODECS=\"avc1.640029");
                if (!string.IsNullOrEmpty(state.ActualOutputAudioCodec))
                {
                    var audioCodecs = GetPlaylistAudioCodecs(state);
                    if (!string.IsNullOrEmpty(audioCodecs))
                    {
                        line.Append(',').Append(audioCodecs);
                    }
                }

                line.Append('"')
                    .Append(",RESOLUTION=").Append(level.Width.ToString(CultureInfo.InvariantCulture))
                    .Append('x').Append(level.Height.ToString(CultureInfo.InvariantCulture));
            }

            AppendPlaylistFramerateField(line, state);
            if (!string.IsNullOrWhiteSpace(subtitleGroup))
            {
                line.Append(",SUBTITLES=\"").Append(subtitleGroup).Append('"');
            }

            line.AppendLine();
            line.AppendLine(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(baseUrl, query));
            variants.Add((peak, line.ToString()));
            variantQueries.Add(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(string.Empty, query).TrimStart('?'));
        }

        // The start level goes first (see GetAtlasStartIndex).
        var startIndex = GetAtlasStartIndex(levels);
        builder.Insert(GetFirstLineEnd(builder), "#EXT-X-INDEPENDENT-SEGMENTS" + Environment.NewLine);
        builder.Append(variants[startIndex].Text);
        for (var i = 0; i < variants.Count; i++)
        {
            if (i != startIndex)
            {
                builder.Append(variants[i].Text);
            }
        }

        var session = StoreAtlasSession(state, levels, variantQueries, segments, info);
        if (session is not null && AtlasAbrSettings.Prewarm)
        {
            // Start the start level's first segment now (see FetchAtlasSegment).
            _ = long.TryParse(request?.Query[AtlasAbrSettings.StartTicksParameter].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var startTicks);
            FetchAtlasSegment(session, startIndex, session.SegmentAt(startTicks), warm: false);
        }
    }

    /// <summary>
    /// Remembers a session's ladder (level ids, variant queries, segment boundaries, the player's
    /// credentials) so segment requests can get the next level up transcoding ahead of a switch.
    /// </summary>
    /// <param name="state">StreamState of the master request.</param>
    /// <param name="levels">The ladder, lowest first.</param>
    /// <param name="variantQueries">Each level's variant query string.</param>
    /// <param name="segments">The segment boundary mode (keyframes or grid).</param>
    /// <param name="info">The source analysis, if any.</param>
    /// <returns>The stored session, or <c>null</c> if it can't be warmed (no session id, no runtime).</returns>
    private AtlasSessionLadder? StoreAtlasSession(StreamState state, IReadOnlyList<AtlasAbrLevel> levels, List<string> variantQueries, string segments, AtlasSourceInfo? info)
    {
        var request = _httpContextAccessor.HttpContext?.Request;
        var runtimeTicks = state.RunTimeTicks ?? 0;
        var playSessionId = state.Request.PlaySessionId;
        if (request is null || runtimeTicks <= 0 || string.IsNullOrEmpty(playSessionId))
        {
            return null;
        }

        var lengths = string.Equals(segments, AtlasAbrSettings.KeyframeSegments, StringComparison.Ordinal) && info is { Aligned: true, KeyframeSeconds.Count: > 0 }
            ? AtlasSegmentGrid.FromKeyframes(info.KeyframeSeconds, runtimeTicks)
            : AtlasSegmentGrid.FromGrid(runtimeTicks, AtlasAbrSettings.GridSeconds, AtlasSegmentGrid.GetFrameRate(state.VideoStream));
        if (lengths.Count == 0)
        {
            return null;
        }

        // Same credentials and user agent as the player: the user agent is part of a transcode job's
        // identity, so anything else would start a second job instead of filling the player's.
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var name in new[] { HeaderNames.Authorization, "X-Emby-Authorization", "X-Emby-Token", "X-MediaBrowser-Token", HeaderNames.UserAgent })
        {
            var value = request.Headers[name].ToString();
            if (!string.IsNullOrEmpty(value))
            {
                headers.Add(new KeyValuePair<string, string>(name, value));
            }
        }

        var session = new AtlasSessionLadder(
            state.Request.Id.ToString("N", CultureInfo.InvariantCulture),
            string.IsNullOrEmpty(state.Request.SegmentContainer) ? "ts" : state.Request.SegmentContainer,
            levels.Select(l => l.Id).ToArray(),
            levels.Select(l => l.IsOriginal).ToArray(),
            variantQueries.ToArray(),
            lengths,
            headers);
        AtlasSessionLadder.Store(playSessionId, session);
        return session;
    }

    /// <summary>
    /// Atlas ABR: the player wants to switch up to <paramref name="level"/> and would fetch
    /// <paramref name="segmentIndex"/> next (or play from <paramref name="positionTicks"/>). Makes sure that level is transcoding from there (starting a
    /// warm-up when it isn't) and says whether it is far enough ahead to switch to now.
    /// </summary>
    /// <remarks>
    /// The player asks about once a second until the answer is yes, then switches; it only asks while
    /// it wants to go up, so nothing is transcoded ahead for a level nobody is about to watch. A
    /// warm-up the player stops asking about times out on its own (see TranscodeManager).
    /// </remarks>
    /// <param name="itemId">The item id (N format).</param>
    /// <param name="playSessionId">The play session id.</param>
    /// <param name="level">The level to switch to.</param>
    /// <param name="segmentIndex">The next segment the player would fetch.</param>
    /// <param name="positionTicks">Or the position to warm from, when the player only knows times.</param>
    /// <returns><c>true</c> if ready, <c>false</c> if warming, <c>null</c> if the session or level is unknown.</returns>
    public bool? WarmAtlasLevel(string itemId, string playSessionId, string level, int? segmentIndex, long? positionTicks)
    {
        if (!AtlasAbrSettings.Enabled
            || !AtlasSessionLadder.TryGet(playSessionId, out var session)
            || !string.Equals(session.ItemId, itemId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var index = Array.IndexOf(session.LevelIds, level);
        if (index < 0)
        {
            return null;
        }

        var segment = segmentIndex ?? session.SegmentAt(Math.Max(0, positionTicks ?? 0));
        if (segment < 0 || segment >= session.SegmentCount)
        {
            return false;
        }

        // Jellyfin serves a segment once the one after it exists, so "ready" means a few past it.
        var readyAt = Math.Min(segment + AtlasAbrSettings.WarmReadySegments, session.SegmentCount - 1);
        var segmentSeconds = Math.Max(1.0, (double)session.SegmentLengthTicks[segment] / TimeSpan.TicksPerSecond);
        var restartGap = Math.Max(1, (int)(24 / segmentSeconds));

        var job = _transcodeManager.GetAtlasLevelJob(playSessionId, level);
        if (job?.Path is not null)
        {
            // Asked for: keep it alive (a level the player left a moment ago counts too).
            _transcodeManager.TouchAtlasWarm(job);
            if (File.Exists(GetAtlasSegmentPath(job.Path, readyAt, session.Container)))
            {
                return true;
            }

            if (File.Exists(GetAtlasSegmentPath(job.Path, segment, session.Container)))
            {
                return false;
            }

            // Not there yet. Fine if this warm-up started at or before it and isn't too far behind;
            // otherwise the switch would restart it anyway, so restart it from here now.
            if (session.TryGetWarmStart(level, out var start)
                && segment >= start
                && (segment - restartGap < start || File.Exists(GetAtlasSegmentPath(job.Path, segment - restartGap, session.Container))))
            {
                return false;
            }
        }

        if (session.TryStartWarm(level, segment))
        {
            // One warm-up per session: a different level asked for earlier isn't wanted any more.
            _ = _transcodeManager.KillAtlasWarmJobs(playSessionId, level);
            _logger.LogDebug("Atlas ABR: player wants {Level}, warming it from segment {Segment}", level, segment);
            FetchAtlasSegment(session, index, segment, warm: true);
        }

        return false;
    }

    private static string GetAtlasSegmentPath(string playlistPath, int index, string container)
        => Path.Combine(
            Path.GetDirectoryName(playlistPath) ?? string.Empty,
            Path.GetFileNameWithoutExtension(playlistPath) + index.ToString(CultureInfo.InvariantCulture) + "." + container);

    /// <summary>
    /// Requests a segment of a level from this server over its local address, exactly as the player
    /// would (same query, user agent and credentials), so it goes through the normal path and lands in
    /// the same transcode job the player will use. Fire and forget: a failure only loses the head start.
    /// </summary>
    /// <param name="session">The session's ladder.</param>
    /// <param name="levelIndex">The level.</param>
    /// <param name="segmentIndex">The segment.</param>
    /// <param name="warm">Whether this warms a level the player isn't on yet.</param>
    private void FetchAtlasSegment(AtlasSessionLadder session, int levelIndex, int segmentIndex, bool warm)
    {
        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"{_serverApplicationHost.GetApiUrlForLocalAccess(null, false).TrimEnd('/')}/Videos/{session.ItemId}/hls1/main/{segmentIndex}.{session.Container}?{session.Queries[levelIndex]}&runtimeTicks={session.SegmentStartTicks[segmentIndex]}&actualSegmentLengthTicks={session.SegmentLengthTicks[segmentIndex]}{(warm ? "&" + AtlasAbrSettings.WarmParameter + "=1" : string.Empty)}");
        var headers = session.Headers;
        var levelId = session.LevelIds[levelIndex];

        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var message = new HttpRequestMessage(HttpMethod.Get, url);
                foreach (var header in headers)
                {
                    message.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                var client = _httpClientFactory.CreateClient(NamedClient.Default);
                using var response = await client.SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
                _logger.LogDebug("Atlas ABR: {Kind} {Level} segment {Index} ({Status})", warm ? "warmed" : "prewarmed", levelId, segmentIndex, (int)response.StatusCode);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
            {
                _logger.LogDebug(ex, "Atlas ABR: fetching {Level} segment {Index} ahead failed", levelId, segmentIndex);
            }
        });
    }

    private static int GetAtlasStartIndex(IReadOnlyList<AtlasAbrLevel> levels)
    {
        // The first listed variant is where AVPlayer and hls.js start (the app gives ExoPlayer a
        // matching first estimate), so this decides time to first frame. Always the lowest rung
        // (480p at 1.5 Mbps by default): its first segment encodes and downloads fastest, and the
        // player climbs, or jumps straight up, as soon as it has measured the connection.
        for (var i = 0; i < levels.Count; i++)
        {
            if (!levels[i].IsOriginal)
            {
                return i;
            }
        }

        return 0;
    }

    private static int GetFirstLineEnd(StringBuilder builder)
    {
        for (var i = 0; i < builder.Length; i++)
        {
            if (builder[i] == '\n')
            {
                return i + 1;
            }
        }

        return builder.Length;
    }

    private string GetAtlasCacheDirectory()
        => AtlasSourceAnalyzer.GetDirectory(_serverConfigurationManager.ApplicationPaths);

    private bool EnableAdaptiveBitrateStreaming(StreamState state, bool isLiveStream, bool enableAdaptiveBitrateStreaming, IPAddress ipAddress)
    {
        // Within the local network this will likely do more harm than good.
        if (_networkManager.IsInLocalNetwork(ipAddress))
        {
            return false;
        }

        if (!enableAdaptiveBitrateStreaming)
        {
            return false;
        }

        if (isLiveStream || string.IsNullOrWhiteSpace(state.MediaPath))
        {
            // Opening live streams is so slow it's not even worth it
            return false;
        }

        if (EncodingHelper.IsCopyCodec(state.OutputVideoCodec))
        {
            return false;
        }

        if (EncodingHelper.IsCopyCodec(state.OutputAudioCodec))
        {
            return false;
        }

        if (!state.IsOutputVideo)
        {
            return false;
        }

        return state.VideoRequest?.VideoBitRate.HasValue ?? false;
    }

    private void AddSubtitles(StreamState state, IEnumerable<MediaStream> subtitles, StringBuilder builder, ClaimsPrincipal user)
    {
        if (state.SubtitleDeliveryMethod == SubtitleDeliveryMethod.Drop)
        {
            return;
        }

        var selectedIndex = state.SubtitleStream is null || state.SubtitleDeliveryMethod != SubtitleDeliveryMethod.Hls ? (int?)null : state.SubtitleStream.Index;
        const string Format = "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"{0}\",DEFAULT={1},FORCED={2},AUTOSELECT=YES,URI=\"{3}\",LANGUAGE=\"{4}\"";

        foreach (var stream in subtitles)
        {
            var name = stream.DisplayTitle;

            var isDefault = selectedIndex.HasValue && selectedIndex.Value == stream.Index;
            var isForced = stream.IsForced;

            var url = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/Subtitles/{1}/subtitles.m3u8?SegmentLength={2}&ApiKey={3}&VttTimestampMapMpegts={4}",
                state.Request.MediaSourceId,
                stream.Index.ToString(CultureInfo.InvariantCulture),
                30.ToString(CultureInfo.InvariantCulture),
                user.GetToken(),
                GetVttTimestampMapMpegts(state.Request.SegmentContainer).ToString(CultureInfo.InvariantCulture));

            var line = string.Format(
                CultureInfo.InvariantCulture,
                Format,
                name,
                isDefault ? "YES" : "NO",
                isForced ? "YES" : "NO",
                url,
                stream.Language ?? "Unknown");

            builder.AppendLine(line);
        }
    }

    /// <summary>
    /// Gets the WebVTT X-TIMESTAMP-MAP MPEGTS offset appropriate for the given HLS segment container.
    /// fMP4 segments start at PTS 0, unlike legacy MPEG-TS segments which start at the historical 10 second offset.
    /// </summary>
    /// <param name="segmentContainer">The HLS segment container, e.g. "ts" or "mp4".</param>
    /// <returns>The MPEGTS offset to embed in the WebVTT X-TIMESTAMP-MAP header.</returns>
    internal static long GetVttTimestampMapMpegts(string? segmentContainer)
    {
        var segmentExtension = EncodingHelper.GetSegmentFileExtension(segmentContainer);
        return string.Equals(segmentExtension, ".mp4", StringComparison.OrdinalIgnoreCase)
            ? 0
            : SubtitleController.DefaultVttTimestampMapMpegts;
    }

    /// <summary>
    /// Appends EXT-X-IMAGE-STREAM-INF playlists for each available trickplay resolution.
    /// </summary>
    /// <param name="state">StreamState of the current stream.</param>
    /// <param name="trickplayResolutions">Dictionary of widths to corresponding tiles info.</param>
    /// <param name="builder">StringBuilder to append the field to.</param>
    /// <param name="user">Http user context.</param>
    private void AddTrickplay(StreamState state, Dictionary<int, TrickplayInfo> trickplayResolutions, StringBuilder builder, ClaimsPrincipal user)
    {
        const string playlistFormat = "#EXT-X-IMAGE-STREAM-INF:BANDWIDTH={0},RESOLUTION={1}x{2},CODECS=\"jpeg\",URI=\"{3}\"";

        foreach (var resolution in trickplayResolutions)
        {
            var width = resolution.Key;
            var trickplayInfo = resolution.Value;

            var url = string.Format(
                CultureInfo.InvariantCulture,
                "Trickplay/{0}/tiles.m3u8?MediaSourceId={1}&ApiKey={2}",
                width.ToString(CultureInfo.InvariantCulture),
                state.Request.MediaSourceId,
                user.GetToken());

            builder.AppendFormat(
                CultureInfo.InvariantCulture,
                playlistFormat,
                trickplayInfo.Bandwidth.ToString(CultureInfo.InvariantCulture),
                trickplayInfo.Width.ToString(CultureInfo.InvariantCulture),
                trickplayInfo.Height.ToString(CultureInfo.InvariantCulture),
                url);

            builder.AppendLine();
        }
    }

    /// <summary>
    /// Get the H.26X level of the output video stream.
    /// </summary>
    /// <param name="state">StreamState of the current stream.</param>
    /// <returns>H.26X level of the output video stream.</returns>
    private int? GetOutputVideoCodecLevel(StreamState state)
    {
        string levelString = string.Empty;
        if (EncodingHelper.IsCopyCodec(state.OutputVideoCodec)
            && state.VideoStream is not null
            && state.VideoStream.Level.HasValue)
        {
            levelString = state.VideoStream.Level.Value.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            if (string.Equals(state.ActualOutputVideoCodec, "h264", StringComparison.OrdinalIgnoreCase))
            {
                levelString = state.GetRequestedLevel(state.ActualOutputVideoCodec) ?? "41";
                levelString = EncodingHelper.NormalizeTranscodingLevel(state, levelString);
            }

            if (string.Equals(state.ActualOutputVideoCodec, "h265", StringComparison.OrdinalIgnoreCase)
                || string.Equals(state.ActualOutputVideoCodec, "hevc", StringComparison.OrdinalIgnoreCase))
            {
                levelString = state.GetRequestedLevel("h265") ?? state.GetRequestedLevel("hevc") ?? "120";
                levelString = EncodingHelper.NormalizeTranscodingLevel(state, levelString);
            }

            if (string.Equals(state.ActualOutputVideoCodec, "av1", StringComparison.OrdinalIgnoreCase))
            {
                levelString = state.GetRequestedLevel("av1") ?? "19";
                levelString = EncodingHelper.NormalizeTranscodingLevel(state, levelString);
            }
        }

        if (int.TryParse(levelString, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLevel))
        {
            return parsedLevel;
        }

        return null;
    }

    /// <summary>
    /// Get the profile of the output video stream.
    /// </summary>
    /// <param name="state">StreamState of the current stream.</param>
    /// <param name="codec">Video codec.</param>
    /// <returns>Profile of the output video stream.</returns>
    private string GetOutputVideoCodecProfile(StreamState state, string codec)
    {
        string profileString = string.Empty;
        if (EncodingHelper.IsCopyCodec(state.OutputVideoCodec)
            && !string.IsNullOrEmpty(state.VideoStream.Profile))
        {
            profileString = state.VideoStream.Profile;
        }
        else if (!string.IsNullOrEmpty(codec))
        {
            profileString = state.GetRequestedProfiles(codec).FirstOrDefault() ?? string.Empty;
            if (string.Equals(state.ActualOutputVideoCodec, "h264", StringComparison.OrdinalIgnoreCase))
            {
                profileString ??= "high";
            }

            if (string.Equals(state.ActualOutputVideoCodec, "h265", StringComparison.OrdinalIgnoreCase)
                || string.Equals(state.ActualOutputVideoCodec, "hevc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(state.ActualOutputVideoCodec, "av1", StringComparison.OrdinalIgnoreCase))
            {
                profileString ??= "main";
            }
        }

        return profileString;
    }

    /// <summary>
    /// Gets a formatted string of the output audio codec, for use in the CODECS field.
    /// </summary>
    /// <seealso cref="AppendPlaylistCodecsField(StringBuilder, StreamState)"/>
    /// <seealso cref="GetPlaylistVideoCodecs(StreamState, string, int)"/>
    /// <param name="state">StreamState of the current stream.</param>
    /// <returns>Formatted audio codec string.</returns>
    private string GetPlaylistAudioCodecs(StreamState state)
    {
        if (string.Equals(state.ActualOutputAudioCodec, "aac", StringComparison.OrdinalIgnoreCase))
        {
            string? profile = EncodingHelper.IsCopyCodec(state.OutputAudioCodec)
                ? state.AudioStream?.Profile : state.GetRequestedProfiles("aac").FirstOrDefault();

            return HlsCodecStringHelpers.GetAACString(profile);
        }

        if (string.Equals(state.ActualOutputAudioCodec, "mp3", StringComparison.OrdinalIgnoreCase))
        {
            return HlsCodecStringHelpers.GetMP3String();
        }

        if (string.Equals(state.ActualOutputAudioCodec, "ac3", StringComparison.OrdinalIgnoreCase))
        {
            return HlsCodecStringHelpers.GetAC3String();
        }

        if (string.Equals(state.ActualOutputAudioCodec, "eac3", StringComparison.OrdinalIgnoreCase))
        {
            return HlsCodecStringHelpers.GetEAC3String();
        }

        if (string.Equals(state.ActualOutputAudioCodec, "flac", StringComparison.OrdinalIgnoreCase))
        {
            return HlsCodecStringHelpers.GetFLACString();
        }

        if (string.Equals(state.ActualOutputAudioCodec, "alac", StringComparison.OrdinalIgnoreCase))
        {
            return HlsCodecStringHelpers.GetALACString();
        }

        if (string.Equals(state.ActualOutputAudioCodec, "opus", StringComparison.OrdinalIgnoreCase))
        {
            return HlsCodecStringHelpers.GetOPUSString();
        }

        if (string.Equals(state.ActualOutputAudioCodec, "truehd", StringComparison.OrdinalIgnoreCase))
        {
            return HlsCodecStringHelpers.GetTRUEHDString();
        }

        if (string.Equals(state.ActualOutputAudioCodec, "dts", StringComparison.OrdinalIgnoreCase))
        {
            // lavc only support encoding DTS core profile
            string? profile = EncodingHelper.IsCopyCodec(state.OutputAudioCodec) ? state.AudioStream?.Profile : "DTS";

            return HlsCodecStringHelpers.GetDTSString(profile);
        }

        return string.Empty;
    }

    /// <summary>
    /// Gets a formatted string of the output video codec, for use in the CODECS field.
    /// </summary>
    /// <seealso cref="AppendPlaylistCodecsField(StringBuilder, StreamState)"/>
    /// <seealso cref="GetPlaylistAudioCodecs(StreamState)"/>
    /// <param name="state">StreamState of the current stream.</param>
    /// <param name="codec">Video codec.</param>
    /// <param name="level">Video level.</param>
    /// <returns>Formatted video codec string.</returns>
    private string GetPlaylistVideoCodecs(StreamState state, string codec, int level)
    {
        if (level == 0)
        {
            // This is 0 when there's no requested level in the device profile
            // and the source is not encoded in H.26X or AV1
            _logger.LogError("Got invalid level when building CODECS field for HLS master playlist");
            return string.Empty;
        }

        if (string.Equals(codec, "h264", StringComparison.OrdinalIgnoreCase))
        {
            string profile = GetOutputVideoCodecProfile(state, "h264");
            return HlsCodecStringHelpers.GetH264String(profile, level);
        }

        if (string.Equals(codec, "h265", StringComparison.OrdinalIgnoreCase)
            || string.Equals(codec, "hevc", StringComparison.OrdinalIgnoreCase))
        {
            string profile = GetOutputVideoCodecProfile(state, "hevc");
            return HlsCodecStringHelpers.GetH265String(profile, level);
        }

        if (string.Equals(codec, "av1", StringComparison.OrdinalIgnoreCase))
        {
            string profile = GetOutputVideoCodecProfile(state, "av1");

            // Currently we only transcode to 8 bits AV1
            int bitDepth = 8;
            if (EncodingHelper.IsCopyCodec(state.OutputVideoCodec)
                && state.VideoStream is not null
                && state.VideoStream.BitDepth.HasValue)
            {
                bitDepth = state.VideoStream.BitDepth.Value;
            }

            return HlsCodecStringHelpers.GetAv1String(profile, level, false, bitDepth);
        }

        // VP9 HLS is for video remuxing only, everything is probed from the original video
        if (string.Equals(codec, "vp9", StringComparison.OrdinalIgnoreCase))
        {
            var width = state.VideoStream.Width ?? 0;
            var height = state.VideoStream.Height ?? 0;
            var framerate = state.VideoStream.ReferenceFrameRate ?? 30;
            var bitDepth = state.VideoStream.BitDepth ?? 8;
            return HlsCodecStringHelpers.GetVp9String(
                width,
                height,
                state.VideoStream.PixelFormat,
                framerate,
                bitDepth);
        }

        return string.Empty;
    }

    private int GetBitrateVariation(int bitrate)
    {
        // By default, vary by just 50k
        var variation = 50000;

        if (bitrate >= 10000000)
        {
            variation = 2000000;
        }
        else if (bitrate >= 5000000)
        {
            variation = 1500000;
        }
        else if (bitrate >= 3000000)
        {
            variation = 1000000;
        }
        else if (bitrate >= 2000000)
        {
            variation = 500000;
        }
        else if (bitrate >= 1000000)
        {
            variation = 300000;
        }
        else if (bitrate >= 600000)
        {
            variation = 200000;
        }
        else if (bitrate >= 400000)
        {
            variation = 100000;
        }

        return variation;
    }

    private string ReplacePlaylistCodecsField(StringBuilder playlist, StringBuilder oldValue, StringBuilder newValue)
    {
        var oldPlaylist = playlist.ToString();
        return oldPlaylist.Replace(
            oldValue.ToString(),
            newValue.ToString(),
            StringComparison.Ordinal);
    }
}
