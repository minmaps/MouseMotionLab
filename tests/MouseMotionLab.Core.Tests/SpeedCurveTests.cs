using MouseMotionLab.Core.Analysis;

namespace MouseMotionLab.Core.Tests;

public sealed class SpeedCurveTests
{
    [Fact]
    public void CombinesDifferentDurationsDpiAndQpcFrequenciesWithEqualTrialWeights()
    {
        var accumulator = new SpeedCurveAccumulator();
        accumulator.AddTrial(0, 20, 1_000, 254,
        [
            new(0, 1, 0), new(10, 10, 0), new(20, 10, 0)
        ]);
        accumulator.AddTrial(100, 180, 2_000, 508,
        [
            new(100, 1, 0), new(120, 60, 0), new(140, 60, 0), new(160, 60, 0), new(180, 60, 0)
        ]);

        var summary = accumulator.GetSummary();

        Assert.Equal(2, summary.TrialCount);
        Assert.Equal(0, summary.SkippedTrialCount);
        Assert.Equal(101, summary.Points.Count);
        Assert.Equal(0d, summary.Points[0].ProgressPercent);
        Assert.Equal(100d, summary.Points[^1].ProgressPercent);
        Assert.All(summary.Points, point =>
        {
            Assert.Equal(100d, point.MinimumMillimetersPerSecond, 8);
            Assert.Equal(300d, point.MaximumMillimetersPerSecond, 8);
            Assert.Equal(200d, point.MeanMillimetersPerSecond, 8);
        });
    }

    [Fact]
    public void MinimumAndMaximumAreComputedAtEachTimePoint()
    {
        var accumulator = new SpeedCurveAccumulator(5);
        accumulator.AddTrial(0, 20, 1_000, 254,
        [
            new(0, 1, 0), new(10, 10, 0), new(20, 30, 0)
        ]);
        accumulator.AddTrial(0, 20, 1_000, 254,
        [
            new(0, 1, 0), new(10, 30, 0), new(20, 10, 0)
        ]);

        var points = accumulator.GetSummary().Points;

        Assert.Equal(100d, points[0].MinimumMillimetersPerSecond, 8);
        Assert.Equal(300d, points[0].MaximumMillimetersPerSecond, 8);
        Assert.Equal(200d, points[2].MinimumMillimetersPerSecond, 8);
        Assert.Equal(200d, points[2].MaximumMillimetersPerSecond, 8);
        Assert.Equal(100d, points[^1].MinimumMillimetersPerSecond, 8);
        Assert.Equal(300d, points[^1].MaximumMillimetersPerSecond, 8);
        Assert.All(points, point => Assert.Equal(200d, point.MeanMillimetersPerSecond, 8));
    }

    [Fact]
    public void InterpolatesUsingActualNonUniformIntervalsAndKeepsTerminalStationaryPacket()
    {
        var accumulator = new SpeedCurveAccumulator(7);
        accumulator.AddTrial(0, 30, 1_000, 254,
        [
            new(0, 1, 0), new(10, 10, 0), new(30, 0, 0)
        ]);

        var points = accumulator.GetSummary().Points;

        Assert.Equal(100d, points[0].MeanMillimetersPerSecond, 8);
        Assert.Equal(100d, points[1].MeanMillimetersPerSecond, 8);
        Assert.Equal(200d / 3d, points[2].MeanMillimetersPerSecond, 8);
        Assert.Equal(100d / 3d, points[3].MeanMillimetersPerSecond, 8);
        Assert.Equal(0d, points[4].MeanMillimetersPerSecond, 8);
        Assert.Equal(0d, points[^1].MeanMillimetersPerSecond, 8);
    }

    [Fact]
    public void CombinesPacketsWithIdenticalTimestampsWithoutDividingByZero()
    {
        var accumulator = new SpeedCurveAccumulator();
        accumulator.AddTrial(0, 10, 1_000, 254,
        [
            new(0, 99, 0), new(10, 3, 4), new(10, -3, -4)
        ]);

        Assert.All(accumulator.GetSummary().Points,
            point => Assert.Equal(100d, point.MeanMillimetersPerSecond, 8));
    }

    [Fact]
    public void UnusableTrialsDoNotContaminateTheSummary()
    {
        var accumulator = new SpeedCurveAccumulator();
        Assert.Empty(accumulator.GetSummary().Points);
        accumulator.AddTrial(0, 0, 1_000, 254, [new(0, 1, 0)]);
        accumulator.AddTrial(0, 10, 1_000, 254, [new(0, 1, 0), new(0, 1, 0)]);
        accumulator.AddTrial(0, 10, 1_000, 254, [new(0, 1, 0), new(8, 4, 0), new(7, 4, 0)]);
        accumulator.AddTrial(0, 10, 1_000, 0, [new(0, 1, 0), new(10, 1, 0)]);
        accumulator.AddTrial(0, 10, 1_000, 254, [new(0, 1, 0), new(10, 10, 0)]);

        var summary = accumulator.GetSummary();

        Assert.Equal(1, summary.TrialCount);
        Assert.Equal(4, summary.SkippedTrialCount);
        Assert.All(summary.Points, point => Assert.Equal(100d, point.MeanMillimetersPerSecond, 8));
    }
}
