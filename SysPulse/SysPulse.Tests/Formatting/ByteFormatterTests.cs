using SysPulse.Core.Formatting;

namespace SysPulse.Tests.Formatting;

public class ByteFormatterTests
{
    [Theory]
    [InlineData(0UL, "0 B")]
    [InlineData(512UL, "512 B")]
    [InlineData(1023UL, "1023 B")]
    [InlineData(1024UL, "1 KB")]
    [InlineData(49_152UL, "48 KB")]
    [InlineData(1024UL * 1024, "1 MB")]
    [InlineData(256UL * 1024 * 1024, "256 MB")]
    public void Format_SubGigabyteValues_UseWholeNumberUnit(ulong bytes, string expected)
    {
        Assert.Equal(expected, ByteFormatter.Format(bytes));
    }

    [Fact]
    public void Format_Gigabyte_UsesOneDecimalPlace()
    {
        // 1.2 * 1024^3, spec §5.5's own example ("RAM 1.2 GB").
        ulong bytes = (ulong)(1.2 * 1024 * 1024 * 1024);

        Assert.Equal("1.2 GB", ByteFormatter.Format(bytes));
    }

    [Fact]
    public void Format_UsesInvariantCultureDecimalSeparator()
    {
        var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
            ulong bytes = (ulong)(1.5 * 1024 * 1024 * 1024);

            Assert.Equal("1.5 GB", ByteFormatter.Format(bytes));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
