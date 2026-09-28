namespace SysPulse.Core.Processes;

/// <summary>
/// Computes a process's live descendant set from a snapshot of all processes (spec §5.6 "kill
/// process tree", code-review finding 2), using each process's <see cref="ProcessSample.ParentPid"/>
/// plus a creation-time guard against PID reuse: a candidate only counts as a child of a given
/// parent if its own <see cref="ProcessSample.CreateTime"/> is at or after that parent's -- a
/// process cannot be the real child of a process created after it, so a stale entry that merely
/// happens to report the parent's (reused) PID is excluded.
/// </summary>
public static class ProcessTree
{
    /// <summary>
    /// Returns every live descendant (children, grandchildren, ...) of the process identified by
    /// <paramref name="rootPid"/>/<paramref name="rootCreateTime"/>, walked iteratively,
    /// breadth-first (shallowest generation first).
    /// </summary>
    /// <param name="all">A snapshot of every currently running process.</param>
    /// <param name="rootPid">The ancestor's process id.</param>
    /// <param name="rootCreateTime">
    /// The ancestor's creation time -- used as the guard baseline for its direct children, and to
    /// seed the visited set so a cyclical/corrupt snapshot can never walk back into the root.
    /// </param>
    /// <returns>
    /// Every descendant, in breadth-first order. Reversing this list yields a safe kill order
    /// (deepest descendants first, so a child is never orphaned by killing its parent first).
    /// </returns>
    public static IReadOnlyList<ProcessSample> GetDescendants(IReadOnlyList<ProcessSample> all, int rootPid, long rootCreateTime)
    {
        ArgumentNullException.ThrowIfNull(all);

        var result = new List<ProcessSample>();
        var visited = new HashSet<(int Pid, long CreateTime)> { (rootPid, rootCreateTime) };
        var frontier = new List<(int Pid, long CreateTime)> { (rootPid, rootCreateTime) };

        while (frontier.Count > 0)
        {
            var nextFrontier = new List<(int Pid, long CreateTime)>();

            foreach ((int parentPid, long parentCreateTime) in frontier)
            {
                foreach (ProcessSample candidate in all)
                {
                    if (candidate.ParentPid != parentPid || candidate.CreateTime < parentCreateTime)
                    {
                        continue;
                    }

                    var key = (candidate.Pid, candidate.CreateTime);
                    if (!visited.Add(key))
                    {
                        // Already counted -- guards cycles/duplicate edges in a corrupt snapshot,
                        // and keeps the walk from ever revisiting the root.
                        continue;
                    }

                    result.Add(candidate);
                    nextFrontier.Add(key);
                }
            }

            frontier = nextFrontier;
        }

        return result;
    }
}
