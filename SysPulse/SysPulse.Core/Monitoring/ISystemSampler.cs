namespace SysPulse.Core.Monitoring;

/// <summary>
/// Produces point-in-time samples of system-wide CPU and memory utilization.
/// </summary>
public interface ISystemSampler
{
    /// <summary>
    /// Takes a new sample of system resource usage.
    /// </summary>
    /// <remarks>
    /// CPU utilization is computed from the delta against the previous baseline. Implementations
    /// should establish that baseline at construction so the first call already reports a real
    /// value; if they cannot, the first call returns <c>0</c> for <see cref="SystemSnapshot.CpuPercent"/>.
    /// </remarks>
    /// <returns>The current system snapshot.</returns>
    SystemSnapshot Sample();
}
