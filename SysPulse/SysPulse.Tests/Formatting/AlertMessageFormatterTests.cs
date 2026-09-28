using System.Globalization;
using SysPulse.Core.Formatting;
using SysPulse.Core.Monitoring;
using SysPulse.Core.Processes;

namespace SysPulse.Tests.Formatting;

public class AlertMessageFormatterTests
{
    private static readonly SystemSnapshot Snapshot = new(94.4, 93.4, 16UL * 1024 * 1024 * 1024, DateTimeOffset.UtcNow);
    private static readonly ProcessSample Chrome = new(1234, "chrome.exe", 0, 38.0, (ulong)(1.2 * 1024 * 1024 * 1024), 0);

    [Fact]
    public void BuildBody_Cpu_WithTopProcess_ShowsCpuPercentTop()
    {
        string body = AlertMessageFormatter.BuildBody(Snapshot, BreachKind.Cpu, 9, Chrome);

        Assert.Equal("CPU at 94% for the last ~9 s. Top: chrome.exe (38%).", body);
    }

    [Fact]
    public void BuildBody_Memory_WithTopProcess_ShowsByteSizeTop()
    {
        string body = AlertMessageFormatter.BuildBody(Snapshot, BreachKind.Memory, 9, Chrome);

        Assert.Equal("Memory at 93% for the last ~9 s. Top: chrome.exe (1.2 GB).", body);
    }

    [Fact]
    public void BuildBody_Both_WithTopProcess_ShowsBothMetricsAndCpuTop()
    {
        string body = AlertMessageFormatter.BuildBody(Snapshot, BreachKind.Both, 9, Chrome);

        Assert.Equal("CPU 94% · memory 93% for the last ~9 s. Top: chrome.exe (38%).", body);
    }

    [Fact]
    public void BuildBody_NoTopProcess_OmitsTopClause()
    {
        string body = AlertMessageFormatter.BuildBody(Snapshot, BreachKind.Cpu, 9, null);

        Assert.Equal("CPU at 94% for the last ~9 s.", body);
    }

    [Fact]
    public void BuildBody_UsesInvariantCultureRegardlessOfCurrentCulture()
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            string body = AlertMessageFormatter.BuildBody(Snapshot, BreachKind.Memory, 9, Chrome);

            Assert.Equal("Memory at 93% for the last ~9 s. Top: chrome.exe (1.2 GB).", body);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
