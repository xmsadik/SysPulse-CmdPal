namespace SysPulse.Core.Monitoring;

/// <summary>
/// A single point-in-time sample of system-wide resource usage.
/// </summary>
/// <param name="CpuPercent">System-wide CPU utilization, 0..100.</param>
/// <param name="MemoryPercent">System-wide memory utilization, 0..100.</param>
/// <param name="TotalPhysBytes">Total physical memory in bytes, as reported by the OS at sample time.</param>
/// <param name="Timestamp">The time the sample was taken.</param>
public readonly record struct SystemSnapshot(double CpuPercent, double MemoryPercent, ulong TotalPhysBytes, DateTimeOffset Timestamp);

/// <summary>
/// The health state of the monitored system, as tracked by <see cref="HealthMonitor"/>.
/// </summary>
public enum HealthState
{
    /// <summary>No breach detected; scanning at the normal cadence.</summary>
    Normal,

    /// <summary>A breach was detected and is being re-checked at the retry cadence before raising an alert.</summary>
    Verifying,

    /// <summary>A breach persisted through verification; the alert is active.</summary>
    Alert,
}

/// <summary>
/// Identifies which monitored metric(s) are responsible for a breach or alert.
/// </summary>
public enum BreachKind
{
    /// <summary>No metric is breaching.</summary>
    None,

    /// <summary>Only CPU usage is breaching.</summary>
    Cpu,

    /// <summary>Only memory usage is breaching.</summary>
    Memory,

    /// <summary>Both CPU and memory usage are breaching.</summary>
    Both,
}
