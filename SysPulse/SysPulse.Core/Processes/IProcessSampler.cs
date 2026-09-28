namespace SysPulse.Core.Processes;

/// <summary>
/// Produces point-in-time samples of every running process's identity and resource usage.
/// </summary>
public interface IProcessSampler
{
    /// <summary>
    /// Takes a new sample of every currently running process.
    /// </summary>
    /// <returns>One <see cref="ProcessSample"/> per live process, including PID 0 ("Idle").</returns>
    IReadOnlyList<ProcessSample> Sample();
}
