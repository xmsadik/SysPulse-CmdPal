using SysPulse.Core.Processes;

namespace SysPulse.Tests.Processes;

/// <summary>Covers spec §8 test item 6: CPU% delta calculation, including PID reuse.</summary>
public class ProcessCpuTrackerTests
{
    [Fact]
    public void FirstObservation_ReturnsZero()
    {
        var tracker = new ProcessCpuTracker();

        IReadOnlyDictionary<int, double> result = tracker.ComputeCpuPercents(
            [new ProcessCpuSample(100, 1000, 5_000_000)], DateTimeOffset.UnixEpoch, processorCount: 1);

        Assert.Equal(0.0, result[100]);
    }

    [Fact]
    public void SecondObservation_ComputesDeltaOverWallClockAndProcessorCount()
    {
        var tracker = new ProcessCpuTracker();
        DateTimeOffset t0 = DateTimeOffset.UnixEpoch;
        tracker.ComputeCpuPercents([new ProcessCpuSample(100, 1000, 0)], t0, processorCount: 1);

        // 1s wall clock (10,000,000 * 100ns ticks); cpu ticks advance by 5,000,000 (0.5s busy) -> 50% on 1 core.
        DateTimeOffset t1 = t0.AddSeconds(1);
        IReadOnlyDictionary<int, double> result = tracker.ComputeCpuPercents([new ProcessCpuSample(100, 1000, 5_000_000)], t1, processorCount: 1);

        Assert.Equal(50.0, result[100], precision: 3);
    }

    [Fact]
    public void SamePid_DifferentCreateTime_TreatedAsNewProcess_ReturnsZero()
    {
        var tracker = new ProcessCpuTracker();
        DateTimeOffset t0 = DateTimeOffset.UnixEpoch;
        tracker.ComputeCpuPercents([new ProcessCpuSample(100, 1000, 5_000_000)], t0, processorCount: 1);

        // Same PID reused by a *different* process (different CreateTime): must not diff against
        // the old baseline, which would otherwise report a large delta.
        DateTimeOffset t1 = t0.AddSeconds(1);
        IReadOnlyDictionary<int, double> result = tracker.ComputeCpuPercents([new ProcessCpuSample(100, 2000, 5_500_000)], t1, processorCount: 1);

        Assert.Equal(0.0, result[100]);
    }

    [Fact]
    public void SamePidSameCreateTime_AccumulatesAcrossThreeSamples()
    {
        var tracker = new ProcessCpuTracker();
        DateTimeOffset t0 = DateTimeOffset.UnixEpoch;
        tracker.ComputeCpuPercents([new ProcessCpuSample(100, 1000, 0)], t0, processorCount: 1);

        DateTimeOffset t1 = t0.AddSeconds(1);
        IReadOnlyDictionary<int, double> r1 = tracker.ComputeCpuPercents([new ProcessCpuSample(100, 1000, 5_000_000)], t1, processorCount: 1);
        Assert.Equal(50.0, r1[100], precision: 3);

        DateTimeOffset t2 = t1.AddSeconds(1);
        IReadOnlyDictionary<int, double> r2 = tracker.ComputeCpuPercents([new ProcessCpuSample(100, 1000, 15_000_000)], t2, processorCount: 1);
        Assert.Equal(100.0, r2[100], precision: 3);
    }

    [Fact]
    public void MultiCore_DividesByProcessorCount()
    {
        var tracker = new ProcessCpuTracker();
        DateTimeOffset t0 = DateTimeOffset.UnixEpoch;
        tracker.ComputeCpuPercents([new ProcessCpuSample(100, 1000, 0)], t0, processorCount: 4);

        // 1 full core-second of CPU time used out of 4 available cores over 1 wall-second -> 25%.
        DateTimeOffset t1 = t0.AddSeconds(1);
        IReadOnlyDictionary<int, double> result = tracker.ComputeCpuPercents([new ProcessCpuSample(100, 1000, 10_000_000)], t1, processorCount: 4);

        Assert.Equal(25.0, result[100], precision: 3);
    }

    [Fact]
    public void ClampsAboveHundred()
    {
        var tracker = new ProcessCpuTracker();
        DateTimeOffset t0 = DateTimeOffset.UnixEpoch;
        tracker.ComputeCpuPercents([new ProcessCpuSample(100, 1000, 0)], t0, processorCount: 1);

        // More CPU ticks than wall-clock elapsed could allow on 1 core (defensive clamp).
        DateTimeOffset t1 = t0.AddSeconds(1);
        IReadOnlyDictionary<int, double> result = tracker.ComputeCpuPercents([new ProcessCpuSample(100, 1000, 50_000_000)], t1, processorCount: 1);

        Assert.Equal(100.0, result[100]);
    }

    [Fact]
    public void PrunedEntry_ReappearingLater_IsTreatedAsUnobserved()
    {
        var tracker = new ProcessCpuTracker();
        DateTimeOffset t0 = DateTimeOffset.UnixEpoch;
        tracker.ComputeCpuPercents(
            [new ProcessCpuSample(100, 1000, 0), new ProcessCpuSample(200, 1000, 0)], t0, processorCount: 1);

        // PID 200 is absent from this sample -> pruned from the cache.
        DateTimeOffset t1 = t0.AddSeconds(1);
        tracker.ComputeCpuPercents([new ProcessCpuSample(100, 1000, 5_000_000)], t1, processorCount: 1);

        // PID 200 reappears with the *same* CreateTime as before, but since its baseline was
        // pruned it must be reported as a fresh (0%) observation, not diffed against stale data.
        DateTimeOffset t2 = t1.AddSeconds(1);
        IReadOnlyDictionary<int, double> result = tracker.ComputeCpuPercents([new ProcessCpuSample(200, 1000, 5_000_000)], t2, processorCount: 1);

        Assert.Equal(0.0, result[200]);
    }

    [Fact]
    public void ZeroOrNegativeWallDelta_ReturnsZero()
    {
        double result = ProcessCpuTracker.ComputePercent(prevTicks: 100, ticks: 200, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, processorCount: 1);

        Assert.Equal(0.0, result);
    }
}
