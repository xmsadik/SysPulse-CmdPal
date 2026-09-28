using SysPulse.Core.Processes;

namespace SysPulse.Tests.Processes;

/// <summary>
/// Covers the TOCTOU-safe single-handle terminate helper (code-review findings 1/3). Never
/// actually terminates a real process: the "wrong identity" case is exercised against the test
/// process's own PID with a deliberately incorrect creation time, which must fail the identity
/// check before <c>TerminateProcess</c> is ever reached.
/// </summary>
public class ProcessTerminatorTests
{
    [Fact]
    public void TryTerminate_NonExistentPid_ReturnsNotFound()
    {
        TerminateResult result = ProcessTerminator.TryTerminate(NonExistentPid(), expectedCreateTime: 0);

        Assert.Equal(TerminateOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public void TryTerminate_WrongCreateTime_ReturnsIdentityMismatch_AndNeverTerminates()
    {
        long? actualCreateTime = ProcessImagePath.TryGetCreateTimeTicks(Environment.ProcessId);
        Assert.NotNull(actualCreateTime);

        TerminateResult result = ProcessTerminator.TryTerminate(Environment.ProcessId, actualCreateTime.Value - 1);

        Assert.Equal(TerminateOutcome.IdentityMismatch, result.Outcome);

        // The test process is still alive to make this assertion at all -- the strongest possible
        // proof TerminateProcess was never reached.
        Assert.True(Environment.ProcessId > 0);
    }

    /// <summary>A PID very unlikely to correspond to any live process.</summary>
    private static int NonExistentPid() => 999_999;
}
