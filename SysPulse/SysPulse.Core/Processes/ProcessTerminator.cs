using System.Runtime.InteropServices;

namespace SysPulse.Core.Processes;

/// <summary>
/// Kills a single process by PID with a creation-time identity check, TOCTOU-safe: opens exactly
/// one handle (<c>PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION</c>), reads its creation
/// time and compares it to the caller's expectation, and terminates through that <em>same</em>
/// handle -- so the PID can never be silently reused by an unrelated process between the identity
/// check and the kill (code-review findings 1/3: the previous implementation opened a handle to
/// check identity via <c>ProcessImagePath</c>, closed it, then opened a second handle via
/// <c>Process.GetProcessById</c>/<c>Process.Kill</c> -- a window in which the PID could be reused).
/// </summary>
public static partial class ProcessTerminator
{
    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorAccessDenied = 5;

    /// <summary>
    /// Attempts to terminate the process identified by <paramref name="pid"/>, but only if its
    /// creation time still matches <paramref name="expectedCreateTime"/> -- i.e. it is still the
    /// same process the caller observed earlier, not a different process that has since reused
    /// the same PID.
    /// </summary>
    /// <param name="pid">The process id to terminate.</param>
    /// <param name="expectedCreateTime">
    /// The creation time (FILETIME ticks, same representation as <see cref="ProcessSample.CreateTime"/>)
    /// the caller last observed for this PID.
    /// </param>
    /// <returns>The outcome of the attempt. Never throws.</returns>
    public static TerminateResult TryTerminate(int pid, long expectedCreateTime)
    {
        nint handle = OpenProcess(ProcessTerminate | ProcessQueryLimitedInformation, false, unchecked((uint)pid));
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

            if (creationTime != expectedCreateTime)
            {
                return TerminateResult.IdentityMismatch;
            }

            return TerminateProcess(handle, 1) ? TerminateResult.Terminated : ClassifyFailure(Marshal.GetLastWin32Error());
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Maps a Win32 error from <c>OpenProcess</c>/<c>GetProcessTimes</c>/<c>TerminateProcess</c> to
    /// the appropriate <see cref="TerminateResult"/>: <c>ERROR_INVALID_PARAMETER</c> (87) from
    /// <c>OpenProcess</c> means no such process exists (already exited);
    /// <c>ERROR_ACCESS_DENIED</c> (5) from any of the three calls means the process exists but
    /// this (unelevated) process cannot touch it; anything else is reported as-is.
    /// </summary>
    private static TerminateResult ClassifyFailure(int win32Error) => win32Error switch
    {
        ErrorInvalidParameter => TerminateResult.NotFound,
        ErrorAccessDenied => TerminateResult.AccessDenied,
        _ => TerminateResult.Failed(win32Error),
    };

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint hProcess, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint hProcess, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}

/// <summary>The outcome of a <see cref="ProcessTerminator.TryTerminate"/> attempt.</summary>
public enum TerminateOutcome
{
    /// <summary>The process was found, its identity matched <c>expectedCreateTime</c>, and it was terminated.</summary>
    Terminated,

    /// <summary>No such process exists -- it has already exited (or the PID never existed).</summary>
    NotFound,

    /// <summary>A process exists with this PID, but its creation time no longer matches: the PID was reused by an unrelated process.</summary>
    IdentityMismatch,

    /// <summary>The process exists but could not be opened or terminated due to insufficient rights (e.g. an elevated process while SysPulse runs unelevated).</summary>
    AccessDenied,

    /// <summary>An unexpected Win32 error occurred; see <see cref="TerminateResult.Win32Error"/>.</summary>
    Failed,
}

/// <summary>The result of a <see cref="ProcessTerminator.TryTerminate"/> call.</summary>
/// <param name="Outcome">Which of the possible outcomes occurred.</param>
/// <param name="Win32Error">The Win32 error code, populated only for <see cref="TerminateOutcome.Failed"/>.</param>
public readonly record struct TerminateResult(TerminateOutcome Outcome, int Win32Error = 0)
{
    /// <summary>A successful termination.</summary>
    public static readonly TerminateResult Terminated = new(TerminateOutcome.Terminated);

    /// <summary>The process no longer exists.</summary>
    public static readonly TerminateResult NotFound = new(TerminateOutcome.NotFound);

    /// <summary>The PID exists but no longer refers to the expected process.</summary>
    public static readonly TerminateResult IdentityMismatch = new(TerminateOutcome.IdentityMismatch);

    /// <summary>The caller lacks the rights to open or terminate the process.</summary>
    public static readonly TerminateResult AccessDenied = new(TerminateOutcome.AccessDenied);

    /// <summary>Builds a <see cref="TerminateOutcome.Failed"/> result carrying the Win32 error code.</summary>
    public static TerminateResult Failed(int win32Error) => new(TerminateOutcome.Failed, win32Error);
}
