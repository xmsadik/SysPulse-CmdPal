using SysPulse.Core.Monitoring;
using SysPulse.Core.Settings;

namespace SysPulse.Core.Processes;

/// <summary>
/// Pure ranking of processes for the Top-5 flyout (spec §5.5). Contains no I/O or sampling;
/// callers feed it a set of <see cref="ProcessSample"/>s already taken.
/// </summary>
public static class ProcessRanker
{
    /// <summary>
    /// Returns the top <paramref name="count"/> processes, ranked by the rule appropriate to the
    /// current alert state/breach kind.
    /// </summary>
    /// <remarks>
    /// Rules (spec §5.5):
    /// <list type="bullet">
    /// <item><see cref="HealthState.Alert"/> + <see cref="BreachKind.Cpu"/> ranks by CPU% descending.</item>
    /// <item><see cref="HealthState.Alert"/> + <see cref="BreachKind.Memory"/> ranks by private bytes descending.</item>
    /// <item>
    /// Every other combination (<see cref="BreachKind.Both"/>, or any non-<see cref="HealthState.Alert"/>
    /// state) ranks by the composite score <c>cpuPct / CpuThreshold + memShare / MemoryThreshold</c>,
    /// where <c>memShare = privateBytes / totalPhysBytes * 100</c>.
    /// </item>
    /// </list>
    /// PID 0 ("Idle") and any pseudo-total entry named <c>_Total</c> are always excluded. Ties are
    /// broken deterministically: CPU ranking falls back to private bytes then PID; memory ranking
    /// falls back to CPU% then PID; composite ranking falls back to private bytes then PID (not
    /// CPU%: two processes with equal composite score and 0% CPU each -- the common case while the
    /// system is idle -- would otherwise always tie on the CPU fallback too and fall through to
    /// PID order, ignoring memory entirely). All tiebreaks end in PID so the result is fully
    /// deterministic.
    /// </remarks>
    /// <param name="samples">The candidate processes.</param>
    /// <param name="kind">The current breach kind.</param>
    /// <param name="state">The current health state.</param>
    /// <param name="opts">Supplies <see cref="SysPulseOptions.CpuThreshold"/> / <see cref="SysPulseOptions.MemoryThreshold"/> for the composite score.</param>
    /// <param name="totalPhysBytes">Total physical memory, in bytes, for the composite score's memory share.</param>
    /// <param name="count">The maximum number of processes to return. Defaults to 5.</param>
    /// <returns>Up to <paramref name="count"/> processes, ranked highest-impact first.</returns>
    public static IReadOnlyList<ProcessSample> Top(
        IEnumerable<ProcessSample> samples,
        BreachKind kind,
        HealthState state,
        SysPulseOptions opts,
        ulong totalPhysBytes,
        int count = 5)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(opts);

        if (count <= 0)
        {
            return [];
        }

        var candidates = new List<ProcessSample>();
        foreach (ProcessSample sample in samples)
        {
            if (sample.Pid == 0 || string.Equals(sample.Name, "_Total", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            candidates.Add(sample);
        }

        Comparison<ProcessSample> comparison = SelectComparison(kind, state, opts, totalPhysBytes);
        candidates.Sort(comparison);

        return candidates.Count <= count ? candidates : candidates.GetRange(0, count);
    }

    private static Comparison<ProcessSample> SelectComparison(BreachKind kind, HealthState state, SysPulseOptions opts, ulong totalPhysBytes)
    {
        if (state == HealthState.Alert)
        {
            if (kind == BreachKind.Cpu)
            {
                return CompareByCpu;
            }

            if (kind == BreachKind.Memory)
            {
                return CompareByMemory;
            }
        }

        return (a, b) => CompareByComposite(a, b, opts, totalPhysBytes);
    }

    private static int CompareByCpu(ProcessSample a, ProcessSample b)
    {
        int byCpu = b.CpuPercent.CompareTo(a.CpuPercent);
        if (byCpu != 0)
        {
            return byCpu;
        }

        int byMem = b.PrivateBytes.CompareTo(a.PrivateBytes);
        return byMem != 0 ? byMem : a.Pid.CompareTo(b.Pid);
    }

    private static int CompareByMemory(ProcessSample a, ProcessSample b)
    {
        int byMem = b.PrivateBytes.CompareTo(a.PrivateBytes);
        if (byMem != 0)
        {
            return byMem;
        }

        int byCpu = b.CpuPercent.CompareTo(a.CpuPercent);
        return byCpu != 0 ? byCpu : a.Pid.CompareTo(b.Pid);
    }

    private static int CompareByComposite(ProcessSample a, ProcessSample b, SysPulseOptions opts, ulong totalPhysBytes)
    {
        double scoreA = CompositeScore(a, opts, totalPhysBytes);
        double scoreB = CompositeScore(b, opts, totalPhysBytes);

        int byScore = scoreB.CompareTo(scoreA);
        if (byScore != 0)
        {
            return byScore;
        }

        int byMem = b.PrivateBytes.CompareTo(a.PrivateBytes);
        return byMem != 0 ? byMem : a.Pid.CompareTo(b.Pid);
    }

    /// <summary>Pure composite-score math, kept separately testable.</summary>
    /// <param name="sample">The process to score.</param>
    /// <param name="opts">Supplies the CPU/memory thresholds the score is normalized against.</param>
    /// <param name="totalPhysBytes">Total physical memory, in bytes.</param>
    /// <returns><c>cpuPct / CpuThreshold + memShare / MemoryThreshold</c>.</returns>
    internal static double CompositeScore(ProcessSample sample, SysPulseOptions opts, ulong totalPhysBytes)
    {
        double cpuTerm = opts.CpuThreshold > 0 ? sample.CpuPercent / opts.CpuThreshold : 0.0;
        double memShare = totalPhysBytes > 0 ? (double)sample.PrivateBytes / totalPhysBytes * 100.0 : 0.0;
        double memTerm = opts.MemoryThreshold > 0 ? memShare / opts.MemoryThreshold : 0.0;
        return cpuTerm + memTerm;
    }
}
