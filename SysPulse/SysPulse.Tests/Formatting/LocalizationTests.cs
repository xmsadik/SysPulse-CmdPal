using System.Globalization;
using SysPulse.Core.Formatting;
using SysPulse.Core.Monitoring;
using SysPulse.Core.Processes;
using SysPulse.Core.Properties;

namespace SysPulse.Tests.Formatting;

/// <summary>
/// Task requirement H1: "add a test that tr-TR returns Turkish for 2–3 key strings." Covers
/// <see cref="AlertMessageFormatter"/> and <see cref="ProcessSubtitleFormatter"/> -- the two Core
/// formatters whose text now comes from <c>SysPulse.Core.Properties.Resources</c> -- switching
/// <see cref="CultureInfo.CurrentUICulture"/> (which resource lookups key off) rather than
/// <see cref="CultureInfo.CurrentCulture"/> (which only affects number formatting; see
/// <see cref="AlertMessageFormatterTests.BuildBody_UsesInvariantCultureRegardlessOfCurrentCulture"/>).
/// </summary>
public class LocalizationTests
{
    private static readonly SystemSnapshot Snapshot = new(94.4, 93.4, 16UL * 1024 * 1024 * 1024, DateTimeOffset.UtcNow);
    private static readonly ProcessSample Chrome = new(1234, "chrome.exe", 0, 38.0, (ulong)(1.2 * 1024 * 1024 * 1024), 0);

    [Fact]
    public void BuildBody_TrTrUiCulture_ReturnsTurkishText()
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");

            string body = AlertMessageFormatter.BuildBody(Snapshot, BreachKind.Cpu, 9, Chrome);

            Assert.Equal("CPU %94 seviyesinde, yaklaşık 9 sn boyunca. En çok kaynak kullanan: chrome.exe (38%).", body);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void Format_ProtectedProcess_TrTrUiCulture_AppendsTurkishProtectedSuffix()
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var sample = new ProcessSample(4, "System", CreateTime: 1, CpuPercent: 0, PrivateBytes: 0, WorkingSetBytes: 0);

            string subtitle = ProcessSubtitleFormatter.Format(sample, isProtected: true);

            Assert.Equal("CPU 0% · RAM 0 B · PID 4 · korumalı", subtitle);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void Resources_TrTrUiCulture_TranslatesAlertBothPhrase()
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");

            Assert.Equal("CPU %{0} · bellek %{1}", Resources.Alert_Both);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void Resources_EnUsUiCulture_ReturnsEnglishByDefault()
    {
        // Sanity check that CultureFixture's ModuleInitializer actually pinned en-US -- if this
        // ever fails, every English-string assertion elsewhere in the suite is untrustworthy.
        Assert.Equal("en-US", CultureInfo.CurrentUICulture.Name);
        Assert.Equal("CPU {0}% · memory {1}%", Resources.Alert_Both);
    }
}
