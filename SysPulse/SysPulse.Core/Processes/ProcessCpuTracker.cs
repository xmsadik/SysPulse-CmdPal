namespace SysPulse.Core.Processes;

/// <summary>
/// One process's cumulative CPU time at a point in time, as read from the OS (raw ticks, not a
/// percentage). Input to <see cref="ProcessCpuTracker"/>.
/// </summary>
/// <param name="Pid">The process id.</param>
/// <param name="CreateTime">
/// The process's creation time (FILETIME, 100ns ticks). Paired with <paramref name="Pid"/> as the
/// cache key so a reused PID (same PID, different <see cref="CreateTime"/>) is never mistaken for
/// the process previously observed under that PID.
/// </param>
/// <param name="CpuTicks">Cumulative user+kernel CPU time used by the process, in 100ns ticks.</param>
public readonly record struct ProcessCpuSample(int Pid, long CreateTime, ulong CpuTicks);

/// <summary>
/// Pure CPU%-from-deltas math, kept separately testable from the <c>NtQuerySystemInformation</c>
/// P/Invoke layer used by <see cref="ProcessSampler"/> (spec §8 test item 6).
/// </summary>
/// <remarks>
/// Thread-safety: this type is not thread-safe; callers must serialize calls to
/// <see cref="ComputeCpuPercents"/> (mirrors <see cref="Monitoring.HealthMonitor"/>'s contract).
/// </remarks>
public sealed class ProcessCpuTracker
{
    private readonly Dictionary<CacheKey, CacheEntry> _cache = [];

    /// <summary>
    /// Computes each process's CPU percent since the previous call.
    /// </summary>
    /// <remarks>
    /// Processes are matched to their previous observation by (PID, CreateTime), so a PID reused
    /// by a different process (same PID, different CreateTime) is treated as unobserved rather
    /// than diffed against a stale baseline. A process observed for the first time reports 0%.
    /// Cache entries for processes absent from <paramref name="samples"/> are dropped (pruned) on
    /// every call, so the cache never outlives the process it was measuring.
    /// </remarks>
    /// <param name="samples">The current raw CPU-tick samples, one per live process (PIDs are unique within a call).</param>
    /// <param name="timestamp">The wall-clock time the samples were taken (from an injected <see cref="TimeProvider"/>).</param>
    /// <param name="processorCount">The number of logical processors, used to normalize CPU time into a 0..100 percent.</param>
    /// <returns>CPU percent per PID, clamped to <c>[0, 100]</c>.</returns>
    public IReadOnlyDictionary<int, double> ComputeCpuPercents(IReadOnlyList<ProcessCpuSample> samples, DateTimeOffset timestamp, int processorCount)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var results = new Dictionary<int, double>(samples.Count);
        var nextCache = new Dictionary<CacheKey, CacheEntry>(samples.Count);

        foreach (ProcessCpuSample sample in samples)
        {
            var key = new CacheKey(sample.Pid, sample.CreateTime);
            double percent = _cache.TryGetValue(key, out CacheEntry previous)
                ? ComputePercent(previous.CpuTicks, sample.CpuTicks, previous.Timestamp, timestamp, processorCount)
                : 0.0;

            results[sample.Pid] = percent;
            nextCache[key] = new CacheEntry(sample.CpuTicks, timestamp);
        }

        _cache.Clear();
        foreach (KeyValuePair<CacheKey, CacheEntry> entry in nextCache)
        {
            _cache[entry.Key] = entry.Value;
        }

        return results;
    }

    /// <summary>Pure delta math, kept separately testable from the cache/pruning logic.</summary>
    /// <param name="prevTicks">Cumulative CPU ticks (100ns) at the previous observation.</param>
    /// <param name="ticks">Cumulative CPU ticks (100ns) at the current observation.</param>
    /// <param name="prevTimestamp">Wall-clock time of the previous observation.</param>
    /// <param name="timestamp">Wall-clock time of the current observation.</param>
    /// <param name="processorCount">The number of logical processors.</param>
    /// <returns>CPU utilization percent, clamped to <c>[0, 100]</c>.</returns>
    internal static double ComputePercent(ulong prevTicks, ulong ticks, DateTimeOffset prevTimestamp, DateTimeOffset timestamp, int processorCount)
    {
        if (processorCount <= 0 || ticks < prevTicks)
        {
            return 0.0;
        }

        long wallTicks = (timestamp - prevTimestamp).Ticks;
        if (wallTicks <= 0)
        {
            return 0.0;
        }

        ulong cpuDelta = ticks - prevTicks;
        double denominator = wallTicks * (double)processorCount;
        double percent = cpuDelta / denominator * 100.0;
        return Math.Clamp(percent, 0.0, 100.0);
    }

    private readonly record struct CacheKey(int Pid, long CreateTime);

    private readonly record struct CacheEntry(ulong CpuTicks, DateTimeOffset Timestamp);
}
