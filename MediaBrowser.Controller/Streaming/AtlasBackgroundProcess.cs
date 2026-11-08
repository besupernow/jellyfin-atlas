using System;
using System.Diagnostics;
using System.IO;

namespace MediaBrowser.Controller.Streaming;

/// <summary>
/// Atlas: runs a process in macOS's background band (low CPU priority and throttled disk I/O).
/// </summary>
/// <remarks>
/// Analyzing a file's keyframes reads all of it as fast as the drive allows. On a spinning external
/// drive that starves a transcode reading the same disk (the same thing held a movie on "loading"
/// for over a minute while Jellyfin extracted its subtitles). The first play of a file that hasn't
/// been analyzed yet does exactly that: transcode and analysis at once. In the background band
/// macOS serves the analysis's reads only when nothing more important wants the disk, so the
/// transcode goes first. Elsewhere (or with <c>ATLAS_BACKGROUND_READS=false</c>) nothing changes.
/// </remarks>
public static class AtlasBackgroundProcess
{
    private const string TaskPolicyPath = "/usr/sbin/taskpolicy";

    /// <summary>
    /// Rewrites <paramref name="startInfo"/> to start its program through <c>taskpolicy -b</c>, which
    /// sets the background policy and then becomes the program (same process, so stopping it stops
    /// the program). Call before starting the process.
    /// </summary>
    /// <param name="startInfo">The start info.</param>
    public static void Apply(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (!AtlasAbrSettings.BackgroundReads
            || !OperatingSystem.IsMacOS()
            || string.IsNullOrEmpty(startInfo.FileName)
            || !File.Exists(TaskPolicyPath))
        {
            return;
        }

        var program = startInfo.FileName;
        if (startInfo.ArgumentList.Count > 0)
        {
            startInfo.ArgumentList.Insert(0, program);
            startInfo.ArgumentList.Insert(0, "-b");
        }
        else
        {
            startInfo.Arguments = "-b \"" + program + "\" " + startInfo.Arguments;
        }

        startInfo.FileName = TaskPolicyPath;
    }
}
