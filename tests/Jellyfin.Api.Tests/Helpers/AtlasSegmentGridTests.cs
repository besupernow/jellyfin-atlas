using System;
using System.Linq;
using Jellyfin.MediaEncoding.Hls.Atlas;
using Xunit;

namespace Jellyfin.Api.Tests.Helpers
{
    public static class AtlasSegmentGridTests
    {
        private const double Ntsc24 = 24000.0 / 1001;

        [Theory]
        [InlineData(23.976, Ntsc24)]
        [InlineData(23.9760246, Ntsc24)]
        [InlineData(25.0, 25.0)]
        [InlineData(29.97, 30000.0 / 1001)]
        [InlineData(0, 0)]
        public static void SnapFrameRate_SnapsToExactRate(double measured, double expected)
        {
            Assert.Equal(expected, AtlasSegmentGrid.SnapFrameRate(measured), 9);
        }

        [Fact]
        public static void Boundary_IsFirstFrameAtOrAfterGridPoint()
        {
            // 2s at 23.976 fps is frame 47.952, so the boundary is frame 48.
            Assert.Equal(48 / Ntsc24, AtlasSegmentGrid.Boundary(1, 2, Ntsc24), 9);

            // Whole frame rates land exactly on the grid.
            Assert.Equal(6.0, AtlasSegmentGrid.Boundary(3, 2, 25), 9);
        }

        [Fact]
        public static void FromGrid_AddsUpToRuntime()
        {
            var runtime = TimeSpan.FromMinutes(42).Ticks + 1234567;
            var lengths = AtlasSegmentGrid.FromGrid(runtime, 2, Ntsc24);

            // The generator turns lengths back into ticks and sums them; that sum has to be the runtime.
            var ticks = lengths.Sum(l => Convert.ToInt64(l * TimeSpan.TicksPerSecond));
            Assert.Equal(runtime, ticks);
            Assert.All(lengths, l => Assert.InRange(l, 0.0001, 2.1));
        }

        [Fact]
        public static void FromGrid_BoundariesSitJustBeforeTheirFrame()
        {
            var lengths = AtlasSegmentGrid.FromGrid(TimeSpan.FromHours(2).Ticks, 2, Ntsc24);
            double position = 0;
            for (var i = 0; i < lengths.Count - 1; i++)
            {
                position += lengths[i];
                var frame = AtlasSegmentGrid.Boundary(i + 1, 2, Ntsc24);
                Assert.InRange(frame - position, 0.2 / Ntsc24, 0.3 / Ntsc24);
            }
        }

        [Fact]
        public static void FromKeyframes_CutsAtKeyframes()
        {
            var lengths = AtlasSegmentGrid.FromKeyframes(new[] { 0.0, 2.002, 4.004, 6.006 }, TimeSpan.FromSeconds(7).Ticks);
            Assert.Equal(new[] { 2.002, 2.002, 2.002, 0.994 }, lengths.Select(l => Math.Round(l, 6)));
        }

        [Fact]
        public static void KeyframeOffset_PutsRestartedKeyframesBackOnGrid()
        {
            // Restarting at segment 10 of a 23.976 fps grid: the first frame is boundary 10.
            var startTicks = (long)Math.Round((AtlasSegmentGrid.Boundary(10, 2, Ntsc24) - (0.25 / Ntsc24)) * TimeSpan.TicksPerSecond);
            var offset = AtlasSegmentGrid.KeyframeOffset(startTicks, 10, 2, Ntsc24, false);
            Assert.Equal(AtlasSegmentGrid.Boundary(10, 2, Ntsc24) - 20, offset, 9);
            Assert.InRange(offset, 0, 1 / Ntsc24);

            Assert.Equal(0.0, AtlasSegmentGrid.KeyframeOffset(0, 0, 2, Ntsc24, false));
        }
    }
}
