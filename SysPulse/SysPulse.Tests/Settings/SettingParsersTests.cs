using System.Globalization;
using SysPulse.Core.Settings;

namespace SysPulse.Tests.Settings;

/// <summary>Covers the Settings-phase requirement: parse numeric TextSetting values, tolerating both invariant and current-culture (e.g. Turkish comma-decimal) input, falling back to a default on garbage.</summary>
public class SettingParsersTests
{
    [Theory]
    [InlineData("10", 10)]
    [InlineData("2.5", 2.5)]
    [InlineData("0", 0)]
    [InlineData("-3", -3)]
    public void ParseNumber_ValidInvariantInput_ReturnsParsedValue(string text, double expected)
    {
        Assert.Equal(expected, SettingParsers.ParseNumber(text, -1));
    }

    [Theory]
    [InlineData("  10  ")]
    [InlineData("\t42\n")]
    public void ParseNumber_SurroundingWhitespace_IsTrimmedAndParsed(string text)
    {
        Assert.Equal(text.Trim() == "10" ? 10 : 42, SettingParsers.ParseNumber(text, -1));
    }

    [Fact]
    public void ParseNumber_CommaDecimal_ParsesUnderCurrentCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

            Assert.Equal(2.5, SettingParsers.ParseNumber("2,5", -1));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void ParseNumber_CommaDecimal_FallsBackUnderNonMatchingCurrentCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("en-US");

            // Under en-US, a bare "2,5" is neither a valid invariant number nor a valid en-US
            // one (Float doesn't allow a thousands separator), so it must fall back.
            Assert.Equal(-1, SettingParsers.ParseNumber("2,5", -1));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12abc")]
    [InlineData("--5")]
    [InlineData("1.2.3")]
    public void ParseNumber_Garbage_ReturnsFallback(string text)
    {
        Assert.Equal(-1, SettingParsers.ParseNumber(text, -1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseNumber_EmptyOrWhitespaceOrNull_ReturnsFallback(string? text)
    {
        Assert.Equal(-1, SettingParsers.ParseNumber(text, -1));
    }
}
