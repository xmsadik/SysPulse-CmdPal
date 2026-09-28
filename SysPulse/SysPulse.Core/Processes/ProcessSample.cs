namespace SysPulse.Core.Processes;

/// <summary>
/// A single point-in-time sample of one process's identity and resource usage, as read from
/// <c>NtQuerySystemInformation</c> (see <see cref="ProcessSampler"/>).
/// </summary>
/// <param name="Pid">The process id. <c>0</c> is the System Idle Process.</param>
/// <param name="Name">
/// The image (executable) name as reported by the OS, e.g. <c>chrome.exe</c>. <c>"Idle"</c> for
/// PID 0, whose raw image name is empty.
/// </param>
/// <param name="CreateTime">The process's creation time (FILETIME, 100ns ticks since 1601-01-01 UTC).</param>
/// <param name="CpuPercent">CPU utilization since the previous sample, 0..100 (see <see cref="ProcessCpuTracker"/>).</param>
/// <param name="PrivateBytes">Private (non-shared) committed memory, in bytes.</param>
/// <param name="WorkingSetBytes">Working set size, in bytes.</param>
public readonly record struct ProcessSample(int Pid, string Name, long CreateTime, double CpuPercent, ulong PrivateBytes, ulong WorkingSetBytes);
