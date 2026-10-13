namespace MouseMotionLab.Core.Analysis;

public readonly record struct SpeedCurveEvent(long QpcTicks, int Dx, int Dy);

public sealed record SpeedCurvePoint(
    double ProgressPercent,
    double MinimumMillimetersPerSecond,
    double MaximumMillimetersPerSecond,
    double MeanMillimetersPerSecond);

public sealed record SpeedCurveSummary(
    int TrialCount,
    int SkippedTrialCount,
    IReadOnlyList<SpeedCurvePoint> Points);

/// <summary>Aggregates equally weighted trials on a shared, normalized time axis.</summary>
public sealed class SpeedCurveAccumulator
{
    private readonly double[] _minimum;
    private readonly double[] _maximum;
    private readonly double[] _sum;
    private int _trialCount;
    private int _skippedTrialCount;

    public SpeedCurveAccumulator(int sampleCount = 101)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleCount, 2);
        _minimum = Enumerable.Repeat(double.PositiveInfinity, sampleCount).ToArray();
        _maximum = new double[sampleCount];
        _sum = new double[sampleCount];
    }

    public void AddTrial(
        long firstMotionQpc,
        long terminalQpc,
        long qpcFrequency,
        int dpi,
        IReadOnlyList<SpeedCurveEvent> events)
    {
        var durationTicks = (double)terminalQpc - firstMotionQpc;
        if (durationTicks <= 0 || qpcFrequency <= 0 || dpi <= 0 || events.Count < 2)
        {
            _skippedTrialCount++;
            return;
        }

        var samples = new List<(double Progress, double Speed)>(events.Count);
        var previousTicks = events[0].QpcTicks;
        if (previousTicks < firstMotionQpc || previousTicks > terminalQpc)
        {
            _skippedTrialCount++;
            return;
        }

        // There is no preceding timestamp for the first packet. Only measured
        // intervals are used; packets sharing a timestamp form one group.
        var eventIndex = 0;
        while (eventIndex < events.Count)
        {
            var ticks = events[eventIndex].QpcTicks;
            if (ticks < previousTicks || ticks > terminalQpc)
            {
                _skippedTrialCount++;
                return;
            }

            var distanceCounts = 0d;
            do
            {
                var item = events[eventIndex++];
                distanceCounts += Math.Sqrt((double)item.Dx * item.Dx + (double)item.Dy * item.Dy);
            }
            while (eventIndex < events.Count && events[eventIndex].QpcTicks == ticks);

            if (ticks > previousTicks)
            {
                var intervalTicks = (double)ticks - previousTicks;
                var progress = (((double)previousTicks - firstMotionQpc) + intervalTicks / 2d) / durationTicks;
                var speed = distanceCounts * 25.4d / dpi * qpcFrequency / intervalTicks;
                samples.Add((progress, speed));
            }

            previousTicks = ticks;
        }

        if (samples.Count == 0)
        {
            _skippedTrialCount++;
            return;
        }

        var rightIndex = 0;
        for (var index = 0; index < _sum.Length; index++)
        {
            var progress = index / (double)(_sum.Length - 1);
            while (rightIndex < samples.Count - 1 && samples[rightIndex].Progress < progress)
            {
                rightIndex++;
            }

            var right = samples[rightIndex];
            var speed = right.Speed;
            if (rightIndex > 0 && progress < right.Progress)
            {
                var left = samples[rightIndex - 1];
                var fraction = (progress - left.Progress) / (right.Progress - left.Progress);
                speed = left.Speed + (right.Speed - left.Speed) * fraction;
            }

            _minimum[index] = Math.Min(_minimum[index], speed);
            _maximum[index] = Math.Max(_maximum[index], speed);
            _sum[index] += speed;
        }

        _trialCount++;
    }

    public SpeedCurveSummary GetSummary()
    {
        var points = _trialCount == 0
            ? Array.Empty<SpeedCurvePoint>()
            : Enumerable.Range(0, _sum.Length)
                .Select(index => new SpeedCurvePoint(
                    index * 100d / (_sum.Length - 1),
                    _minimum[index],
                    _maximum[index],
                    _sum[index] / _trialCount))
                .ToArray();
        return new SpeedCurveSummary(_trialCount, _skippedTrialCount, points);
    }
}
