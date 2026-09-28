using SysPulse.Core.Settings;

namespace SysPulse.Tests.Settings;

/// <summary>Covers spec §8 test item 9: settings clamp invalid values.</summary>
public class SysPulseOptionsTests
{
    [Fact]
    public void Defaults_AreAlreadyValid_AndUnchangedByNormalize()
    {
        var options = new SysPulseOptions();

        var normalized = options.Normalize();

        Assert.Equal(options, normalized);
    }

    [Fact]
    public void Normalize_RetryIntervalGreaterThanOrEqualToScanInterval_IsLoweredBelowScan()
    {
        var options = new SysPulseOptions { ScanIntervalSeconds = 10, RetryIntervalSeconds = 15 };

        var normalized = options.Normalize();

        Assert.Equal(10, normalized.ScanIntervalSeconds);
        Assert.Equal(9, normalized.RetryIntervalSeconds);
        Assert.True(normalized.RetryIntervalSeconds < normalized.ScanIntervalSeconds);
    }

    [Fact]
    public void Normalize_RetryIntervalEqualToScanInterval_IsLoweredBelowScan()
    {
        var options = new SysPulseOptions { ScanIntervalSeconds = 10, RetryIntervalSeconds = 10 };

        var normalized = options.Normalize();

        Assert.Equal(9, normalized.RetryIntervalSeconds);
    }

    [Fact]
    public void Normalize_RetryIntervalFloorIsOne_WhenScanIntervalAtMinimum()
    {
        var options = new SysPulseOptions { ScanIntervalSeconds = 2, RetryIntervalSeconds = 2 };

        var normalized = options.Normalize();

        Assert.Equal(2, normalized.ScanIntervalSeconds);
        Assert.Equal(1, normalized.RetryIntervalSeconds);
    }

    [Theory]
    [InlineData(0, SysPulseOptions.MinScanIntervalSeconds)]
    [InlineData(1_000, SysPulseOptions.MaxScanIntervalSeconds)]
    public void Normalize_ClampsScanIntervalToRange(int input, int expected)
    {
        var options = new SysPulseOptions { ScanIntervalSeconds = input, RetryIntervalSeconds = 1 };

        var normalized = options.Normalize();

        Assert.Equal(expected, normalized.ScanIntervalSeconds);
    }

    [Theory]
    [InlineData(-5, SysPulseOptions.MinRetryCount)]
    [InlineData(99, SysPulseOptions.MaxRetryCount)]
    public void Normalize_ClampsRetryCountToRange(int input, int expected)
    {
        var options = new SysPulseOptions { RetryCount = input };

        var normalized = options.Normalize();

        Assert.Equal(expected, normalized.RetryCount);
    }

    [Theory]
    [InlineData(0, SysPulseOptions.MinThreshold)]
    [InlineData(500, SysPulseOptions.MaxThreshold)]
    public void Normalize_ClampsCpuAndMemoryThresholds(double input, double expected)
    {
        var options = new SysPulseOptions { CpuThreshold = input, MemoryThreshold = input };

        var normalized = options.Normalize();

        Assert.Equal(expected, normalized.CpuThreshold);
        Assert.Equal(expected, normalized.MemoryThreshold);
    }

    [Theory]
    [InlineData(-1, SysPulseOptions.MinHysteresisPercent)]
    [InlineData(50, SysPulseOptions.MaxHysteresisPercent)]
    public void Normalize_ClampsHysteresisPercent(double input, double expected)
    {
        var options = new SysPulseOptions { HysteresisPercent = input };

        var normalized = options.Normalize();

        Assert.Equal(expected, normalized.HysteresisPercent);
    }

    /// <summary>Code-review finding 5: <c>Math.Clamp(double,...)</c> leaves NaN unchanged and passes infinities through; both must fall back to the property's default instead.</summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Normalize_NonFiniteCpuAndMemoryThresholds_FallBackToDefault(double input)
    {
        var options = new SysPulseOptions { CpuThreshold = input, MemoryThreshold = input };

        var normalized = options.Normalize();

        Assert.Equal(new SysPulseOptions().CpuThreshold, normalized.CpuThreshold);
        Assert.Equal(new SysPulseOptions().MemoryThreshold, normalized.MemoryThreshold);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Normalize_NonFiniteHysteresisPercent_FallsBackToDefault(double input)
    {
        var options = new SysPulseOptions { HysteresisPercent = input };

        var normalized = options.Normalize();

        Assert.Equal(new SysPulseOptions().HysteresisPercent, normalized.HysteresisPercent);
    }

    [Fact]
    public void Clamp_IsStaticEquivalentOfNormalize()
    {
        var options = new SysPulseOptions { ScanIntervalSeconds = 5, RetryIntervalSeconds = 5 };

        Assert.Equal(options.Normalize(), SysPulseOptions.Clamp(options));
    }

    [Theory]
    [InlineData("chrome.exe, notepad, NOTEPAD, Chrome.EXE, , explorer.exe ", new[] { "chrome", "notepad", "explorer" })]
    [InlineData("", new string[0])]
    [InlineData(null, new string[0])]
    [InlineData("   ", new string[0])]
    [InlineData("svchost.EXE", new[] { "svchost" })]
    public void ParseProtectedProcesses_TrimsStripsExeAndDedupesCaseInsensitively(string? raw, string[] expected)
    {
        IReadOnlyList<string> result = SysPulseOptions.ParseProtectedProcesses(raw);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ParsedProtectedProcesses_ReflectsRawProperty()
    {
        var options = new SysPulseOptions { ProtectedProcesses = "foo.exe, Foo, bar" };

        Assert.Equal(["foo", "bar"], options.ParsedProtectedProcesses);
    }
}
