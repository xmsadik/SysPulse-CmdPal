using SysPulse.Core.Formatting;
using SysPulse.Core.Processes;

namespace SysPulse.Tests.Formatting;

/// <summary>Covers the Top-5 process list subtitle format (spec §5.5): <c>CPU 38% · RAM 1.2 GB · PID 12345</c>.</summary>
public class ProcessSubtitleFormatterTests
{
    [Fact]
    public void Format_TypicalProcess_MatchesSpecExample()
    {
        var sample = new ProcessSample(12345, "chrome.exe", CreateTime: 1, CpuPercent: 38.4, PrivateBytes: (ulong)(1.2 * 1024 * 1024 * 1024), WorkingSetBytes: 0);

        string subtitle = ProcessSubtitleFormatter.Format(sample, isProtected: false);

        Assert.Equal("CPU 38% · RAM 1.2 GB · PID 12345", subtitle);
    }

    [Fact]
    public void Format_ProtectedProcess_AppendsProtectedSuffix()
    {
        var sample = new ProcessSample(4, "System", CreateTime: 1, CpuPercent: 0, PrivateBytes: 0, WorkingSetBytes: 0);

        string subtitle = ProcessSubtitleFormatter.Format(sample, isProtected: true);

        Assert.Equal("CPU 0% · RAM 0 B · PID 4 · protected", subtitle);
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(37.5, 38)]
    [InlineData(100.0, 100)]
    [InlineData(-1.0, 0)]
    [InlineData(150.0, 100)]
    public void Format_CpuPercent_RoundsAndClamps(double cpuPercent, int expectedRounded)
    {
        var sample = new ProcessSample(1, "proc.exe", CreateTime: 1, CpuPercent: cpuPercent, PrivateBytes: 0, WorkingSetBytes: 0);

        string subtitle = ProcessSubtitleFormatter.Format(sample, isProtected: false);

        Assert.StartsWith($"CPU {expectedRounded}% ", subtitle, StringComparison.Ordinal);
    }
}
