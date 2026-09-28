using System.Runtime.InteropServices;

namespace SysPulse.Core.Processes;

/// <summary>
/// Resolves a running process's executable image path and creation time via lightweight,
/// handle-opening Win32 calls (<c>OpenProcess</c> + <c>QueryFullProcessImageNameW</c> /
/// <c>GetProcessTimes</c>), for use by the Top-5 flyout's icon resolution (spec §5.5) and the
/// kill command's identity check (spec §5.6).
/// </summary>
/// <remarks>
/// Unlike <see cref="ProcessSampler"/> (which enumerates every process via
/// <c>NtQuerySystemInformation</c> without opening any handle), these calls open a single
/// short-lived handle per process with only <c>PROCESS_QUERY_LIMITED_INFORMATION</c> access —
/// the minimum right both APIs require, and one that succeeds even for elevated/protected
/// processes SysPulse (running un-elevated) cannot otherwise inspect. All failures (no such
/// process, access denied, etc.) are reported as <see langword="null"/>; this type never throws
/// for a bad or stale PID.
/// </remarks>
public static partial class ProcessImagePath
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ImagePathBufferChars = 1024;

    /// <summary>
    /// Attempts to resolve the full executable path of the process identified by
    /// <paramref name="pid"/>.
    /// </summary>
    /// <param name="pid">The process id.</param>
    /// <returns>
    /// The full image path (e.g. <c>C:\Windows\System32\notepad.exe</c>), or
    /// <see langword="null"/> if the process does not exist, has already exited, or cannot be
    /// opened (e.g. an elevated process while SysPulse runs un-elevated).
    /// </returns>
    public static unsafe string? TryGetImagePath(int pid)
    {
        nint handle = OpenProcess(ProcessQueryLimitedInformation, false, unchecked((uint)pid));
        if (handle == 0)
        {
            return null;
        }

        try
        {
            char* buffer = stackalloc char[ImagePathBufferChars];
            int size = ImagePathBufferChars;
            if (!QueryFullProcessImageNameW(handle, 0, buffer, ref size) || size <= 0)
            {
                return null;
            }

            return new string(buffer, 0, size);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Attempts to read the creation time of the process identified by <paramref name="pid"/>,
    /// in the same representation (FILETIME, 100ns ticks since 1601-01-01 UTC) as
    /// <see cref="ProcessSample.CreateTime"/>, so the two are directly comparable.
    /// </summary>
    /// <param name="pid">The process id.</param>
    /// <returns>
    /// The process's creation time, or <see langword="null"/> if the process does not exist, has
    /// already exited, or cannot be opened.
    /// </returns>
    public static long? TryGetCreateTimeTicks(int pid)
    {
        nint handle = OpenProcess(ProcessQueryLimitedInformation, false, unchecked((uint)pid));
        if (handle == 0)
        {
            return null;
        }

        try
        {
            if (!GetProcessTimes(handle, out long creationTime, out _, out _, out _))
            {
                return null;
            }

            return creationTime;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageNameW(nint hProcess, uint flags, char* exeName, ref int size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint hProcess, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
