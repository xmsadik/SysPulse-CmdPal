using SysPulse.Core.Monitoring;
using SysPulse.Core.Processes;
using SysPulse.Core.Settings;

namespace SysPulse.Tests.Processes;

/// <summary>Covers spec §8 test item 7: ranking CPU-only, memory-only, and composite.</summary>
public class ProcessRankerTests
{
    private static readonly SysPulseOptions Options = new() { CpuThreshold = 80, MemoryThreshold = 90 };

    private static ProcessSample P(int pid, double cpu, ulong privateBytes) => new(pid, $"proc{pid}.exe", pid, cpu, privateBytes, privateBytes);

    [Fact]
    public void AlertCpu_SortsByCpuDescending()
    {
        ProcessSample[] samples =
        [
            P(1, cpu: 10, privateBytes: 900),
            P(2, cpu: 90, privateBytes: 100),
            P(3, cpu: 50, privateBytes: 500),
        ];

        IReadOnlyList<ProcessSample> top = ProcessRanker.Top(samples, BreachKind.Cpu, HealthState.Alert, Options, totalPhysBytes: 1_000);

        Assert.Equal([2, 3, 1], top.Select(p => p.Pid));
    }

    [Fact]
    public void AlertMemory_SortsByPrivateBytesDescending()
    {
        ProcessSample[] samples =
        [
            P(1, cpu: 10, privateBytes: 900),
            P(2, cpu: 90, privateBytes: 100),
            P(3, cpu: 50, privateBytes: 500),
        ];

        IReadOnlyList<ProcessSample> top = ProcessRanker.Top(samples, BreachKind.Memory, HealthState.Alert, Options, totalPhysBytes: 1_000);

        Assert.Equal([1, 3, 2], top.Select(p => p.Pid));
    }

    [Fact]
    public void AlertBoth_UsesCompositeScore()
    {
        // totalPhys = 1000. Composite = cpu/80 + (privateBytes/1000*100)/90.
        // pid1: 40/80=0.5 + (200/1000*100)/90=0.222 -> 0.722
        // pid2: 10/80=0.125 + (800/1000*100)/90=0.889 -> 1.014
        ProcessSample[] samples = [P(1, cpu: 40, privateBytes: 200), P(2, cpu: 10, privateBytes: 800)];

        IReadOnlyList<ProcessSample> top = ProcessRanker.Top(samples, BreachKind.Both, HealthState.Alert, Options, totalPhysBytes: 1_000);

        Assert.Equal([2, 1], top.Select(p => p.Pid));
    }

    [Fact]
    public void NormalState_UsesCompositeScoreRegardlessOfBreachKind()
    {
        ProcessSample[] samples = [P(1, cpu: 40, privateBytes: 200), P(2, cpu: 10, privateBytes: 800)];

        IReadOnlyList<ProcessSample> top = ProcessRanker.Top(samples, BreachKind.Cpu, HealthState.Normal, Options, totalPhysBytes: 1_000);

        Assert.Equal([2, 1], top.Select(p => p.Pid));
    }

    [Fact]
    public void VerifyingState_UsesCompositeScoreRegardlessOfBreachKind()
    {
        ProcessSample[] samples = [P(1, cpu: 40, privateBytes: 200), P(2, cpu: 10, privateBytes: 800)];

        IReadOnlyList<ProcessSample> top = ProcessRanker.Top(samples, BreachKind.Memory, HealthState.Verifying, Options, totalPhysBytes: 1_000);

        Assert.Equal([2, 1], top.Select(p => p.Pid));
    }

    [Fact]
    public void IdleProcess_IsExcluded()
    {
        ProcessSample[] samples = [P(0, cpu: 99, privateBytes: 999) with { Name = "Idle" }, P(1, cpu: 5, privateBytes: 5)];

        IReadOnlyList<ProcessSample> top = ProcessRanker.Top(samples, BreachKind.Cpu, HealthState.Alert, Options, totalPhysBytes: 1_000);

        Assert.DoesNotContain(top, p => p.Pid == 0);
        Assert.Single(top);
    }

    [Fact]
    public void PseudoTotalEntry_IsExcluded()
    {
        ProcessSample[] samples = [P(1, cpu: 5, privateBytes: 5) with { Name = "_Total" }, P(2, cpu: 5, privateBytes: 5)];

        IReadOnlyList<ProcessSample> top = ProcessRanker.Top(samples, BreachKind.Cpu, HealthState.Alert, Options, totalPhysBytes: 1_000);

        Assert.DoesNotContain(top, p => p.Name == "_Total");
    }

    [Fact]
    public void ResultIsCappedAtCount()
    {
        ProcessSample[] samples = Enumerable.Range(1, 20).Select(i => P(i, cpu: i, privateBytes: (ulong)i)).ToArray();

        IReadOnlyList<ProcessSample> top = ProcessRanker.Top(samples, BreachKind.Cpu, HealthState.Alert, Options, totalPhysBytes: 1_000);

        Assert.Equal(5, top.Count);
        Assert.Equal([20, 19, 18, 17, 16], top.Select(p => p.Pid));
    }

    [Fact]
    public void ResultRespectsExplicitCountBelowFive()
    {
        ProcessSample[] samples = Enumerable.Range(1, 10).Select(i => P(i, cpu: i, privateBytes: (ulong)i)).ToArray();

        IReadOnlyList<ProcessSample> top = ProcessRanker.Top(samples, BreachKind.Cpu, HealthState.Alert, Options, totalPhysBytes: 1_000, count: 2);

        Assert.Equal(2, top.Count);
    }

    [Fact]
    public void FewerCandidatesThanCount_ReturnsAllOfThem()
    {
        ProcessSample[] samples = [P(1, cpu: 5, privateBytes: 5), P(2, cpu: 10, privateBytes: 10)];

        IReadOnlyList<ProcessSample> top = ProcessRanker.Top(samples, BreachKind.Cpu, HealthState.Alert, Options, totalPhysBytes: 1_000);

        Assert.Equal(2, top.Count);
    }

    [Fact]
    public void CpuTie_BreaksByPrivateBytesThenPid()
    {
        ProcessSample[] samples =
        [
            new ProcessSample(3, "a.exe", 3, 50, PrivateBytes: 100, WorkingSetBytes: 100),
            new ProcessSample(2, "b.exe", 2, 50, PrivateBytes: 200, WorkingSetBytes: 200),
            new ProcessSample(1, "c.exe", 1, 50, PrivateBytes: 200, WorkingSetBytes: 200),
        ];

        IReadOnlyList<ProcessSample> top = ProcessRanker.Top(samples, BreachKind.Cpu, HealthState.Alert, Options, totalPhysBytes: 1_000);

        // Pid 2 and 1 tie on cpu(50) and privateBytes(200); pid ascending breaks the tie. Pid 3 is last (lower privateBytes).
        Assert.Equal([1, 2, 3], top.Select(p => p.Pid));
    }

    /// <summary>
    /// Code-review finding 6: the composite ranking's tiebreak must fall back to private bytes
    /// (not CPU%) before PID, so that when every candidate has 0% CPU -- the common case while
    /// the system is idle -- ties are still broken by memory instead of falling straight through
    /// to PID order.
    /// </summary>
    [Fact]
    public void CompositeTie_AllZeroCpu_StillBreaksByPrivateBytesThenPid()
    {
        ProcessSample[] samples =
        [
            new ProcessSample(3, "a.exe", 3, CpuPercent: 0, PrivateBytes: 100, WorkingSetBytes: 100),
            new ProcessSample(2, "b.exe", 2, CpuPercent: 0, PrivateBytes: 300, WorkingSetBytes: 300),
            new ProcessSample(1, "c.exe", 1, CpuPercent: 0, PrivateBytes: 300, WorkingSetBytes: 300),
        ];

        IReadOnlyList<ProcessSample> top = ProcessRanker.Top(samples, BreachKind.Both, HealthState.Normal, Options, totalPhysBytes: 1_000);

        // Pid 1 and 2 tie on composite score (both 0 cpu, 300 privateBytes) and on privateBytes;
        // ascending pid breaks that tie. Pid 3 (lower privateBytes) ranks last.
        Assert.Equal([1, 2, 3], top.Select(p => p.Pid));
    }

    [Fact]
    public void CountZeroOrNegative_ReturnsEmpty()
    {
        ProcessSample[] samples = [P(1, cpu: 5, privateBytes: 5)];

        Assert.Empty(ProcessRanker.Top(samples, BreachKind.Cpu, HealthState.Alert, Options, totalPhysBytes: 1_000, count: 0));
        Assert.Empty(ProcessRanker.Top(samples, BreachKind.Cpu, HealthState.Alert, Options, totalPhysBytes: 1_000, count: -1));
    }
}
