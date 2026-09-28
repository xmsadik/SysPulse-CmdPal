using SysPulse.Core.Monitoring;
using SysPulse.Core.Settings;

namespace SysPulse.Tests.Monitoring;

/// <summary>
/// Covers spec §8 test items 1–5 and 9 (item 9's options-clamp coverage lives in
/// <c>SysPulseOptionsTests</c>), plus RetryInterval-change-mid-Verifying.
/// Defaults per spec: Scan 10s, Retry 3s, RetryCount 3, CPU 85%, MEM 90%, Hysteresis 5%.
/// </summary>
public class HealthMonitorTests
{
    private static readonly SysPulseOptions Defaults = new();

    private static SystemSnapshot Snapshot(double cpu, double mem) => new(cpu, mem, 16_000_000_000, DateTimeOffset.UnixEpoch);

    // Spec test 1: single spike that recovers on retry 1 -> stays NORMAL, no UI change (BreachKind back to None).
    [Fact]
    public void SingleSpikeRecoveringOnFirstRetry_StaysNormal()
    {
        var monitor = new HealthMonitor(Defaults);

        HealthEvaluation e1 = monitor.Evaluate(Snapshot(90, 10)); // breach -> Verifying
        Assert.Equal(HealthState.Verifying, e1.State);
        Assert.Equal(TimeSpan.FromSeconds(Defaults.RetryIntervalSeconds), e1.NextDelay);

        HealthEvaluation e2 = monitor.Evaluate(Snapshot(50, 10)); // recovers on first retry -> Normal
        Assert.Equal(HealthState.Normal, e2.State);
        Assert.Equal(BreachKind.None, e2.BreachKind);
        Assert.False(e2.EnteredAlert);
        Assert.Equal(TimeSpan.FromSeconds(Defaults.ScanIntervalSeconds), e2.NextDelay);
        Assert.Equal(HealthState.Normal, monitor.State);
    }

    // Spec test 2: breach persisting through all 3 retries -> ALERT, EnteredAlert true exactly once.
    [Fact]
    public void BreachPersistingThroughAllRetries_EntersAlertExactlyOnce()
    {
        var monitor = new HealthMonitor(Defaults);
        var enteredAlertFlags = new List<bool>();

        enteredAlertFlags.Add(monitor.Evaluate(Snapshot(90, 10)).EnteredAlert); // trigger
        enteredAlertFlags.Add(monitor.Evaluate(Snapshot(90, 10)).EnteredAlert); // retry 1
        enteredAlertFlags.Add(monitor.Evaluate(Snapshot(90, 10)).EnteredAlert); // retry 2
        HealthEvaluation last = monitor.Evaluate(Snapshot(90, 10)); // retry 3 -> Alert
        enteredAlertFlags.Add(last.EnteredAlert);

        Assert.Equal([false, false, false, true], enteredAlertFlags);
        Assert.Equal(HealthState.Alert, last.State);
        Assert.Equal(BreachKind.Cpu, last.BreachKind);
        Assert.Equal(HealthState.Alert, monitor.State);
    }

    // Spec test 3: ALERT -> metric at threshold-2 (inside hysteresis) -> stays ALERT.
    [Fact]
    public void AlertMetricWithinHysteresis_StaysAlert()
    {
        var monitor = DriveToAlert();

        HealthEvaluation e = monitor.Evaluate(Snapshot(Defaults.CpuThreshold - 2, 10));

        Assert.Equal(HealthState.Alert, e.State);
        Assert.False(e.LeftAlert);
        Assert.Equal(HealthState.Alert, monitor.State);
    }

    // Spec test 4: ALERT -> metric at threshold-6 (beyond hysteresis) -> NORMAL.
    [Fact]
    public void AlertMetricBeyondHysteresis_ReturnsToNormal()
    {
        var monitor = DriveToAlert();

        HealthEvaluation e = monitor.Evaluate(Snapshot(Defaults.CpuThreshold - 6, 10));

        Assert.Equal(HealthState.Normal, e.State);
        Assert.True(e.LeftAlert);
        Assert.Equal(BreachKind.None, e.BreachKind);
        Assert.Equal(HealthState.Normal, monitor.State);
    }

    // Spec test 5: both metrics disabled -> never alerts, even under sustained extreme load.
    [Fact]
    public void BothMetricsDisabled_NeverAlerts()
    {
        var options = Defaults with { CpuMonitoringEnabled = false, MemoryMonitoringEnabled = false };
        var monitor = new HealthMonitor(options);

        for (int i = 0; i < 10; i++)
        {
            HealthEvaluation e = monitor.Evaluate(Snapshot(100, 100));
            Assert.Equal(HealthState.Normal, e.State);
            Assert.Equal(BreachKind.None, e.BreachKind);
        }
    }

    [Fact]
    public void RetryIntervalOrCountChange_MidVerifying_TakesEffectOnNextSample()
    {
        var monitor = new HealthMonitor(Defaults);

        HealthEvaluation trigger = monitor.Evaluate(Snapshot(90, 10)); // Verifying, attempt=0
        Assert.Equal(HealthState.Verifying, trigger.State);

        // Shrink RetryCount to 1 mid-verification; per documented UpdateOptions behavior the
        // attempt counter is kept, so the very next breaching sample should now be enough to alert.
        monitor.UpdateOptions(Defaults with { RetryCount = 1 });

        HealthEvaluation next = monitor.Evaluate(Snapshot(90, 10));

        Assert.Equal(HealthState.Alert, next.State);
        Assert.True(next.EnteredAlert);
    }

    [Fact]
    public void UpdateOptions_ChangesRetryIntervalUsedForNextDelay()
    {
        var monitor = new HealthMonitor(Defaults);
        monitor.Evaluate(Snapshot(90, 10)); // -> Verifying

        monitor.UpdateOptions(Defaults with { RetryIntervalSeconds = 7 });

        HealthEvaluation next = monitor.Evaluate(Snapshot(90, 10)); // still breaching -> stays Verifying
        Assert.Equal(HealthState.Verifying, next.State);
        Assert.Equal(TimeSpan.FromSeconds(7), next.NextDelay);
    }

    private static HealthMonitor DriveToAlert()
    {
        var monitor = new HealthMonitor(Defaults);
        monitor.Evaluate(Snapshot(90, 10));
        monitor.Evaluate(Snapshot(90, 10));
        monitor.Evaluate(Snapshot(90, 10));
        HealthEvaluation last = monitor.Evaluate(Snapshot(90, 10));
        Assert.Equal(HealthState.Alert, last.State);
        return monitor;
    }
}
