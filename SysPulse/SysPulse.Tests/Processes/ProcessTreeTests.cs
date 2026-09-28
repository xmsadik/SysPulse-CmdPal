using SysPulse.Core.Processes;

namespace SysPulse.Tests.Processes;

/// <summary>Covers code-review finding 2: descendant computation for "kill process tree".</summary>
public class ProcessTreeTests
{
    private static ProcessSample P(int pid, int parentPid, long createTime) =>
        new(pid, $"p{pid}.exe", createTime, CpuPercent: 0, PrivateBytes: 0, WorkingSetBytes: 0, ParentPid: parentPid);

    [Fact]
    public void GetDescendants_MultiLevelTree_ReturnsEveryGenerationBreadthFirst()
    {
        ProcessSample[] all =
        [
            P(1, parentPid: 0, createTime: 100), // root
            P(2, parentPid: 1, createTime: 110), // child
            P(3, parentPid: 1, createTime: 111), // child
            P(4, parentPid: 2, createTime: 120), // grandchild
            P(5, parentPid: 4, createTime: 130), // great-grandchild
            P(6, parentPid: 999, createTime: 140), // unrelated
        ];

        IReadOnlyList<ProcessSample> descendants = ProcessTree.GetDescendants(all, rootPid: 1, rootCreateTime: 100);

        Assert.Equal([2, 3, 4, 5], descendants.Select(p => p.Pid));
    }

    [Fact]
    public void GetDescendants_ChildOlderThanClaimedParent_IsExcluded_PidReuseGuard()
    {
        // pid 2 claims parent 1, but was created BEFORE pid 1's own creation time -- it cannot
        // really be a child of the current pid-1 process, only a stale entry from some earlier,
        // unrelated process that happened to report pid 1 (since reused) as its parent.
        ProcessSample[] all =
        [
            P(1, parentPid: 0, createTime: 200),
            P(2, parentPid: 1, createTime: 100),
        ];

        IReadOnlyList<ProcessSample> descendants = ProcessTree.GetDescendants(all, rootPid: 1, rootCreateTime: 200);

        Assert.Empty(descendants);
    }

    [Fact]
    public void GetDescendants_Cycle_TerminatesAndDoesNotRevisitRoot()
    {
        // Contrived cycle: 1 reports parent 2, and 2 reports parent 1 (impossible for a real
        // process tree, but the walk must still terminate rather than loop forever).
        ProcessSample[] all =
        [
            P(1, parentPid: 2, createTime: 100),
            P(2, parentPid: 1, createTime: 100),
        ];

        IReadOnlyList<ProcessSample> descendants = ProcessTree.GetDescendants(all, rootPid: 1, rootCreateTime: 100);

        // pid 2 is a legitimate child of the root by the create-time rule; pid 1 (the root itself)
        // is never re-added even though pid 2 "claims" it as a child.
        Assert.Equal([2], descendants.Select(p => p.Pid));
    }

    [Fact]
    public void GetDescendants_NoChildren_ReturnsEmpty()
    {
        ProcessSample[] all = [P(1, parentPid: 0, createTime: 100)];

        IReadOnlyList<ProcessSample> descendants = ProcessTree.GetDescendants(all, rootPid: 1, rootCreateTime: 100);

        Assert.Empty(descendants);
    }

    [Fact]
    public void GetDescendants_SiblingsAtSameDepth_BothIncluded()
    {
        ProcessSample[] all =
        [
            P(1, parentPid: 0, createTime: 100),
            P(2, parentPid: 1, createTime: 110),
            P(3, parentPid: 1, createTime: 120),
        ];

        IReadOnlyList<ProcessSample> descendants = ProcessTree.GetDescendants(all, rootPid: 1, rootCreateTime: 100);

        Assert.Equal(2, descendants.Count);
        Assert.Contains(descendants, p => p.Pid == 2);
        Assert.Contains(descendants, p => p.Pid == 3);
    }
}
