using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.MediaEncoding.Hls.Atlas;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.MediaEncoding.Hls.ScheduledTasks;

/// <summary>
/// Atlas ABR: analyzes the library's video files ahead of time (keyframe positions, frame rate and
/// bitrate peaks), so no playback has to wait for that work.
/// </summary>
/// <remarks>
/// A file's first adaptive play otherwise gets an all-transcode ladder while the analysis runs in
/// the background. Analyzed files can offer the original as the top level from the first play.
/// Results go in the data folder (<c>atlas-abr/</c>) and are redone only when a file changes.
/// Runs nightly next to the trickplay and chapter image tasks.
/// </remarks>
public class AtlasKeyframeAnalysisTask : IScheduledTask, IConfigurableScheduledTask
{
    private const int PageSize = 500;
    private static readonly BaseItemKind[] _itemTypes = [BaseItemKind.Episode, BaseItemKind.Movie, BaseItemKind.Video];

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<AtlasKeyframeAnalysisTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AtlasKeyframeAnalysisTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="mediaEncoder">The media encoder (for the ffprobe path).</param>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public AtlasKeyframeAnalysisTask(
        ILibraryManager libraryManager,
        IMediaEncoder mediaEncoder,
        IApplicationPaths applicationPaths,
        ILogger<AtlasKeyframeAnalysisTask> logger)
    {
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Atlas: analyze video for adaptive streaming";

    /// <inheritdoc />
    public string Key => "AtlasKeyframeAnalysis";

    /// <inheritdoc />
    public string Description => "Reads each video's keyframes and bitrate peaks once, so adaptive streaming can start instantly and offer the original file as the top quality.";

    /// <inheritdoc />
    public string Category => "Atlas";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => AtlasAbrSettings.Enabled;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
            MaxRuntimeTicks = TimeSpan.FromHours(6).Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!AtlasAbrSettings.Enabled)
        {
            progress.Report(100);
            return;
        }

        var query = new InternalItemsQuery
        {
            MediaTypes = [MediaType.Video],
            IsVirtualItem = false,
            IncludeItemTypes = _itemTypes,
            DtoOptions = new DtoOptions(false),
            SourceTypes = [SourceType.Library],
            Recursive = true,
            IncludeOwnedItems = true,
            Limit = PageSize
        };

        var total = _libraryManager.GetCount(query);
        var directory = AtlasSourceAnalyzer.GetDirectory(_applicationPaths);
        var grid = AtlasAbrSettings.GridSeconds;
        var done = 0;
        var analyzed = 0;

        for (var start = 0; start < total; start += PageSize)
        {
            query.StartIndex = start;
            foreach (var item in _libraryManager.GetItemList(query))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (item is IHasMediaSources hasSources)
                {
                    foreach (var source in hasSources.GetMediaSources(false))
                    {
                        // Local files only, and only the ones playback would analyze.
                        if (source.Protocol != MediaProtocol.File
                            || string.IsNullOrEmpty(source.Id)
                            || string.IsNullOrEmpty(source.Path)
                            || !File.Exists(source.Path))
                        {
                            continue;
                        }

                        if (await AtlasSourceAnalyzer.AnalyzeIfNeededAsync(
                                source.Id,
                                source.Path,
                                _mediaEncoder.ProbePath,
                                directory,
                                grid,
                                _logger,
                                cancellationToken).ConfigureAwait(false))
                        {
                            analyzed++;
                        }
                    }
                }

                done++;
                progress.Report(100.0 * done / Math.Max(total, 1));
            }
        }

        _logger.LogInformation("Atlas ABR: analysis task finished, {Analyzed} of {Total} videos analyzed this run", analyzed, total);
        progress.Report(100);
    }
}
