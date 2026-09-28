using SysPulse.Core.Processes;

namespace SysPulse.Tests.Processes;

/// <summary>
/// Integration smoke test against the real OS (no fakes) — exercises the actual
/// <c>NtQuerySystemInformation</c> P/Invoke layer, buffer growth, and struct-offset parsing.
/// </summary>
public class ProcessSamplerTests
{
    [Fact]
    public void Sample_CalledTwice_ReturnsRealProcessData()
    {
        using var sampler = new ProcessSampler();

        IReadOnlyList<ProcessSample> first = sampler.Sample();

        // Second call exercises the delta-based CPU% path against a live process set.
        Thread.Sleep(50);
        IReadOnlyList<ProcessSample> second = sampler.Sample();

        Assert.True(first.Count > 10, $"Expected more than 10 processes, got {first.Count}.");
        Assert.True(second.Count > 10, $"Expected more than 10 processes, got {second.Count}.");

        int currentPid = Environment.ProcessId;
        ProcessSample self = Assert.Single(second, p => p.Pid == currentPid);
        Assert.False(string.IsNullOrWhiteSpace(self.Name));

        foreach (ProcessSample sample in second)
        {
            Assert.True(sample.CpuPercent >= 0.0, $"PID {sample.Pid} ({sample.Name}) reported negative CpuPercent: {sample.CpuPercent}");
            Assert.True(sample.CpuPercent <= 100.0, $"PID {sample.Pid} ({sample.Name}) reported CpuPercent above 100: {sample.CpuPercent}");
            Assert.True(sample.CreateTime >= 0, $"PID {sample.Pid} ({sample.Name}) reported negative CreateTime.");
        }

        // Idle (PID 0) is always present and named "Idle" per spec §5.1.
        ProcessSample idle = Assert.Single(second, p => p.Pid == 0);
        Assert.Equal("Idle", idle.Name);
    }

    [Fact]
    public void Sample_RepeatedCalls_DoNotThrow()
    {
        using var sampler = new ProcessSampler();

        for (int i = 0; i < 5; i++)
        {
            IReadOnlyList<ProcessSample> result = sampler.Sample();
            Assert.NotEmpty(result);
        }
    }
}
