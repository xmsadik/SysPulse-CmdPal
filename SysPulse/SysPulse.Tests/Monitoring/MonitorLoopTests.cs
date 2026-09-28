using Microsoft.Extensions.Time.Testing;
using SysPulse.Core.Monitoring;
using SysPulse.Core.Settings;
using SysPulse.Tests.TestSupport;

namespace SysPulse.Tests.Monitoring;

public class MonitorLoopTests
{
    private static readonly SysPulseOptions Defaults = new();
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task BreachPersistingThroughThreeRetries_FiresExactlyOneAlertWithScanThenRetryCadence()
    {
        var fakeTime = new FakeTimeProvider();
        SystemSnapshot Breach() => new(90, 10, 16_000_000_000, fakeTime.GetUtcNow());
        var sampler = new ScriptedSampler([Breach(), Breach(), Breach(), Breach()]);
        var monitor = new HealthMonitor(Defaults);
        var notifier = new RecordingNotifier();
        using var sync = new SemaphoreSlim(0);
        var states = new List<HealthState>();

        await using var loop = new MonitorLoop(sampler, monitor, Defaults, fakeTime, notifier);
        loop.Sampled += (evaluation, _) =>
        {
            states.Add(evaluation.State);
            sync.Release();
        };

        loop.Start();

        // Tick 1: initial cadence must be the Scan interval (state starts Normal).
        fakeTime.Advance(TimeSpan.FromSeconds(Defaults.ScanIntervalSeconds));
        Assert.True(await sync.WaitAsync(WaitTimeout));
        Assert.Equal(HealthState.Verifying, states[^1]);
        Assert.Empty(notifier.Calls);

        // Ticks 2-3: retry cadence, still verifying.
        fakeTime.Advance(TimeSpan.FromSeconds(Defaults.RetryIntervalSeconds));
        Assert.True(await sync.WaitAsync(WaitTimeout));
        Assert.Equal(HealthState.Verifying, states[^1]);

        fakeTime.Advance(TimeSpan.FromSeconds(Defaults.RetryIntervalSeconds));
        Assert.True(await sync.WaitAsync(WaitTimeout));
        Assert.Equal(HealthState.Verifying, states[^1]);
        Assert.Empty(notifier.Calls);

        // Tick 4: 3rd retry -> Alert, exactly one notification.
        fakeTime.Advance(TimeSpan.FromSeconds(Defaults.RetryIntervalSeconds));
        Assert.True(await sync.WaitAsync(WaitTimeout));
        Assert.Equal(HealthState.Alert, states[^1]);

        Assert.Single(notifier.Calls);
        Assert.Equal(BreachKind.Cpu, notifier.Calls[0].BreachKind);
        Assert.Equal(0, sampler.RemainingCount);

        await loop.StopAsync();
    }

    [Fact]
    public async Task NoBreach_NeverNotifies_AndKeepsScanCadence()
    {
        var fakeTime = new FakeTimeProvider();
        SystemSnapshot Healthy() => new(20, 30, 16_000_000_000, fakeTime.GetUtcNow());
        var sampler = new ScriptedSampler([Healthy(), Healthy(), Healthy()]);
        var monitor = new HealthMonitor(Defaults);
        var notifier = new RecordingNotifier();
        using var sync = new SemaphoreSlim(0);

        await using var loop = new MonitorLoop(sampler, monitor, Defaults, fakeTime, notifier);
        loop.Sampled += (_, _) => sync.Release();
        loop.Start();

        for (int i = 0; i < 3; i++)
        {
            fakeTime.Advance(TimeSpan.FromSeconds(Defaults.ScanIntervalSeconds));
            Assert.True(await sync.WaitAsync(WaitTimeout));
        }

        Assert.Empty(notifier.Calls);
        Assert.Equal(HealthState.Normal, monitor.State);

        await loop.StopAsync();
    }

    [Fact]
    public async Task SamplerException_RaisesFaulted_AndLoopContinues()
    {
        var fakeTime = new FakeTimeProvider();
        var sampler = new ThrowThenSucceedSampler(fakeTime);
        var monitor = new HealthMonitor(Defaults);
        using var faultSync = new SemaphoreSlim(0);
        using var sampledSync = new SemaphoreSlim(0);
        Exception? observed = null;

        await using var loop = new MonitorLoop(sampler, monitor, Defaults, fakeTime);
        loop.Faulted += ex =>
        {
            observed = ex;
            faultSync.Release();
        };
        loop.Sampled += (_, _) => sampledSync.Release();
        loop.Start();

        fakeTime.Advance(TimeSpan.FromSeconds(Defaults.ScanIntervalSeconds));
        Assert.True(await faultSync.WaitAsync(WaitTimeout));
        Assert.NotNull(observed);

        fakeTime.Advance(TimeSpan.FromSeconds(Defaults.ScanIntervalSeconds));
        Assert.True(await sampledSync.WaitAsync(WaitTimeout));

        await loop.StopAsync();
    }

    private sealed class ThrowThenSucceedSampler(FakeTimeProvider timeProvider) : ISystemSampler
    {
        private bool _thrown;

        public SystemSnapshot Sample()
        {
            if (!_thrown)
            {
                _thrown = true;
                throw new InvalidOperationException("simulated sampler failure");
            }

            return new SystemSnapshot(10, 10, 16_000_000_000, timeProvider.GetUtcNow());
        }
    }
}
