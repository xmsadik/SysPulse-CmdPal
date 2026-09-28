using SysPulse.Core.Settings;

namespace SysPulse.Core.Processes;

/// <summary>
/// The set of process names/PIDs that must never be offered for killing (spec §5.6). Immutable —
/// call <see cref="Create"/> again to rebuild it when <see cref="SysPulseOptions"/> changes.
/// </summary>
public sealed class ProtectedProcessList
{
    /// <summary>
    /// Built-in protected names (spec §5.6): core OS/session processes, plus the Command Palette
    /// host and SysPulse's own display name. Own-process protection by PID is handled separately
    /// via <see cref="Create"/>'s <c>ownProcessId</c> parameter, since a display name is not a
    /// reliable match for "this process" (and the image name is always <c>SysPulse.exe</c> — kept
    /// here too as a defensive, name-based fallback).
    /// </summary>
    private static readonly string[] BuiltIn =
    [
        "System",
        "Idle",
        "Registry",
        "smss",
        "csrss",
        "wininit",
        "winlogon",
        "services",
        "lsass",
        "svchost",
        "dwm",
        "fontdrvhost",
        "Memory Compression",
        "MsMpEng",
        "Microsoft.CmdPal.UI",
        "PowerToys",
        "explorer",
        "SysPulse",
    ];

    private readonly HashSet<string> _names;
    private readonly HashSet<int> _pids;

    private ProtectedProcessList(HashSet<string> names, HashSet<int> pids)
    {
        _names = names;
        _pids = pids;
    }

    /// <summary>
    /// Builds a new protected-process list from the built-in names (spec §5.6), the caller's own
    /// process id, and <see cref="SysPulseOptions.ParsedProtectedProcesses"/>.
    /// </summary>
    /// <param name="options">Supplies the user-configurable extra protected names.</param>
    /// <param name="ownProcessId">
    /// SysPulse's own process id (the caller passes <see cref="Environment.ProcessId"/>; taken as
    /// a parameter rather than read internally so tests can supply an arbitrary value).
    /// </param>
    /// <returns>An immutable snapshot of the protected list.</returns>
    public static ProtectedProcessList Create(SysPulseOptions options, int ownProcessId)
    {
        ArgumentNullException.ThrowIfNull(options);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in BuiltIn)
        {
            names.Add(Normalize(name));
        }

        foreach (string name in options.ParsedProtectedProcesses)
        {
            names.Add(Normalize(name));
        }

        var pids = new HashSet<int> { ownProcessId };

        return new ProtectedProcessList(names, pids);
    }

    /// <summary>
    /// Tests whether a process must never be offered for killing.
    /// </summary>
    /// <param name="name">The process's image name, with or without a <c>.exe</c> suffix (case-insensitive).</param>
    /// <param name="pid">The process id.</param>
    /// <returns>True if the process is protected, either by PID (SysPulse's own process) or by name.</returns>
    public bool IsProtected(string name, int pid)
    {
        if (_pids.Contains(pid))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(name) && _names.Contains(Normalize(name));
    }

    private static string Normalize(string name)
    {
        string trimmed = name.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
    }
}
