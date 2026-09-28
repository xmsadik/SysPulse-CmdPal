using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SysPulse.Core.Monitoring;

/// <summary>
/// Samples system-wide CPU and memory utilization via <c>GetSystemTimes</c> and
/// <c>GlobalMemoryStatusEx</c> (kernel32). Intentionally avoids <c>PerformanceCounter</c>,
/// which is slow to initialize and depends on locale-specific counter names.
/// </summary>
public sealed partial class SystemSampler : ISystemSampler
{
    private readonly TimeProvider _timeProvider;
    private ulong _lastIdle;
    private ulong _lastKernel;
    private ulong _lastUser;
    private bool _primed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemSampler"/> class.
    /// </summary>
    /// <param name="timeProvider">
    /// The time source used to stamp snapshots. Defaults to <see cref="TimeProvider.System"/>.
    /// </param>
    public SystemSampler(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;

        // Prime the CPU baseline now so the first Sample() already reports a real delta.
        if (GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user))
        {
            _lastIdle = ToTicks(idle);
            _lastKernel = ToTicks(kernel);
            _lastUser = ToTicks(user);
            _primed = true;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The constructor primes the CPU delta baseline, so the first call reports the average
    /// since construction. Only if priming failed does the first call return <c>0</c> for
    /// <see cref="SystemSnapshot.CpuPercent"/>.
    /// </remarks>
    public SystemSnapshot Sample()
    {
        if (!GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user))
        {
            throw new InvalidOperationException($"GetSystemTimes failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        ulong idleTicks = ToTicks(idle);
        ulong kernelTicks = ToTicks(kernel);
        ulong userTicks = ToTicks(user);

        double cpuPercent;
        if (!_primed)
        {
            cpuPercent = 0.0;
            _primed = true;
        }
        else
        {
            cpuPercent = ComputeCpuPercent(_lastIdle, _lastKernel, _lastUser, idleTicks, kernelTicks, userTicks);
        }

        _lastIdle = idleTicks;
        _lastKernel = kernelTicks;
        _lastUser = userTicks;

        var memoryStatus = default(MEMORYSTATUSEX);
        memoryStatus.dwLength = (uint)Unsafe.SizeOf<MEMORYSTATUSEX>();
        if (!GlobalMemoryStatusEx(ref memoryStatus))
        {
            throw new InvalidOperationException($"GlobalMemoryStatusEx failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        double memoryPercent = ComputeMemoryPercent(memoryStatus.ullTotalPhys, memoryStatus.ullAvailPhys);

        return new SystemSnapshot(cpuPercent, memoryPercent, memoryStatus.ullTotalPhys, _timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Pure CPU-percent delta math, kept separately testable from the P/Invoke layer.
    /// Kernel time reported by Windows includes idle time, so busy time is
    /// <c>(kernelDelta + userDelta) - idleDelta</c>.
    /// </summary>
    /// <param name="prevIdle">Idle time (100ns ticks) from the previous sample.</param>
    /// <param name="prevKernel">Kernel time (100ns ticks) from the previous sample.</param>
    /// <param name="prevUser">User time (100ns ticks) from the previous sample.</param>
    /// <param name="idle">Idle time (100ns ticks) from the current sample.</param>
    /// <param name="kernel">Kernel time (100ns ticks) from the current sample.</param>
    /// <param name="user">User time (100ns ticks) from the current sample.</param>
    /// <returns>CPU utilization percent, clamped to <c>[0, 100]</c>.</returns>
    internal static double ComputeCpuPercent(ulong prevIdle, ulong prevKernel, ulong prevUser, ulong idle, ulong kernel, ulong user)
    {
        ulong idleDelta = idle - prevIdle;
        ulong kernelDelta = kernel - prevKernel;
        ulong userDelta = user - prevUser;
        ulong totalDelta = kernelDelta + userDelta;

        if (totalDelta == 0)
        {
            return 0.0;
        }

        double busyFraction = 1.0 - ((double)idleDelta / totalDelta);
        return Math.Clamp(busyFraction * 100.0, 0.0, 100.0);
    }

    /// <summary>
    /// Pure memory-percent math from total/available physical bytes, kept separately testable.
    /// </summary>
    /// <param name="totalPhysBytes">Total physical memory, in bytes.</param>
    /// <param name="availPhysBytes">Available physical memory, in bytes.</param>
    /// <returns>Memory utilization percent, clamped to <c>[0, 100]</c>.</returns>
    internal static double ComputeMemoryPercent(ulong totalPhysBytes, ulong availPhysBytes)
    {
        if (totalPhysBytes == 0)
        {
            return 0.0;
        }

        ulong usedBytes = totalPhysBytes > availPhysBytes ? totalPhysBytes - availPhysBytes : 0;
        return Math.Clamp((double)usedBytes / totalPhysBytes * 100.0, 0.0, 100.0);
    }

    private static ulong ToTicks(in FILETIME fileTime) => ((ulong)fileTime.DwHighDateTime << 32) | fileTime.DwLowDateTime;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint DwLowDateTime;
        public uint DwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
