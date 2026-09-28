using SysPulse.Core.Processes;

namespace SysPulse.Tests.Processes;

/// <summary>Covers the native Top-5 icon/kill-identity helper (spec §5.5/§5.6).</summary>
public class ProcessImagePathTests
{
    [Fact]
    public void TryGetImagePath_OwnProcess_ReturnsExecutablePath()
    {
        string? path = ProcessImagePath.TryGetImagePath(Environment.ProcessId);

        Assert.NotNull(path);
        Assert.EndsWith(".exe", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryGetImagePath_NonExistentPid_ReturnsNull()
    {
        string? path = ProcessImagePath.TryGetImagePath(NonExistentPid());

        Assert.Null(path);
    }

    [Fact]
    public void TryGetCreateTimeTicks_OwnProcess_ReturnsPositiveValue()
    {
        long? ticks = ProcessImagePath.TryGetCreateTimeTicks(Environment.ProcessId);

        Assert.NotNull(ticks);
        Assert.True(ticks > 0);
    }

    [Fact]
    public void TryGetCreateTimeTicks_NonExistentPid_ReturnsNull()
    {
        long? ticks = ProcessImagePath.TryGetCreateTimeTicks(NonExistentPid());

        Assert.Null(ticks);
    }

    /// <summary>
    /// A PID very unlikely to correspond to any live process (Windows PIDs are small multiples
    /// of 4, well below this value, even on long-running machines).
    /// </summary>
    private static int NonExistentPid() => 999_999;
}
