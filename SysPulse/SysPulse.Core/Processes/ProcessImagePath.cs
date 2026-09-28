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
    public static long? TryGetCreateTimeTicks(int pid) =>
        TryGetCreateTimeTicksResult(pid) is { Status: ProcessTimeQueryStatus.Found } result ? result.CreateTime : null;

    /// <summary>
    /// Same query as <see cref="TryGetCreateTimeTicks"/>, but distinguishes "no such process"
    /// (already exited) from "access denied" (the process exists but this unelevated caller
    /// cannot open it) -- code-review finding 8b: a caller that uses this for an identity check
    /// must not treat "denied" the same as "exited", since a denied process is still very much
    /// alive.
    /// </summary>
    /// <param name="pid">The process id.</param>
    /// <returns>The query status and, when <see cref="ProcessTimeQueryStatus.Found"/>, the creation time.</returns>
    public static ProcessTimeQueryResult TryGetCreateTimeTicksResult(int pid)
    {
        nint handle = OpenProcess(ProcessQueryLimitedInformation, false, unchecked((uint)pid));
        if (handle == 0)
        {
            return ClassifyFailure(Marshal.GetLastWin32Error());
        }

        try
        {
            if (!GetProcessTimes(handle, out long creationTime, out _, out _, out _))
            {
                return ClassifyFailure(Marshal.GetLastWin32Error());
            }

            return new ProcessTimeQueryResult(ProcessTimeQueryStatus.Found, creationTime);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Maps a Win32 error from <c>OpenProcess</c>/<c>GetProcessTimes</c> to a
    /// <see cref="ProcessTimeQueryResult"/>: <c>ERROR_ACCESS_DENIED</c> (5) is reported as
    /// <see cref="ProcessTimeQueryStatus.AccessDenied"/>; anything else (including
    /// <c>ERROR_INVALID_PARAMETER</c>, 87, for a PID that no longer exists) as
    /// <see cref="ProcessTimeQueryStatus.NotFound"/>.
    /// </summary>
    private static ProcessTimeQueryResult ClassifyFailure(int win32Error) =>
        win32Error == ErrorAccessDenied
            ? new ProcessTimeQueryResult(ProcessTimeQueryStatus.AccessDenied, 0)
            : new ProcessTimeQueryResult(ProcessTimeQueryStatus.NotFound, 0);

    private const int ErrorAccessDenied = 5;

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

/// <summary>The outcome of a <see cref="ProcessImagePath.TryGetCreateTimeTicksResult"/> query.</summary>
public enum ProcessTimeQueryStatus
{
    /// <summary>The process was found and its creation time read.</summary>
    Found,

    /// <summary>No such process exists (already exited, or never existed).</summary>
    NotFound,

    /// <summary>The process exists but could not be opened due to insufficient rights.</summary>
    AccessDenied,
}

/// <summary>The result of a <see cref="ProcessImagePath.TryGetCreateTimeTicksResult"/> call.</summary>
/// <param name="Status">Which of the possible outcomes occurred.</param>
/// <param name="CreateTime">The process's creation time, populated only when <paramref name="Status"/> is <see cref="ProcessTimeQueryStatus.Found"/>.</param>
public readonly record struct ProcessTimeQueryResult(ProcessTimeQueryStatus Status, long CreateTime);
