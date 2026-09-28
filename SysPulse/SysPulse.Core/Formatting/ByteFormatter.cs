using System.Globalization;

namespace SysPulse.Core.Formatting;

/// <summary>
/// Pure byte-count formatting shared by the Top-5 process list (spec §5.5) and anywhere else a
/// human-readable memory size is shown. Kept in Core so it is unit-testable without the SDK.
/// </summary>
public static class ByteFormatter
{
    private const double KiloBytes = 1024.0;
    private const double MegaBytes = KiloBytes * 1024.0;
    private const double GigaBytes = MegaBytes * 1024.0;

    /// <summary>
    /// Formats <paramref name="bytes"/> using the largest whole unit (B/KB/MB/GB) that keeps the
    /// value at least 1, invariant culture. Only the GB tier shows a decimal place (e.g.
    /// <c>1.2 GB</c>); B/KB/MB are rounded to the nearest whole number.
    /// </summary>
    /// <param name="bytes">The byte count to format.</param>
    /// <returns>A human-readable size, e.g. <c>512 B</c>, <c>48 KB</c>, <c>256 MB</c>, <c>1.2 GB</c>.</returns>
    public static string Format(ulong bytes)
    {
        if (bytes >= (ulong)GigaBytes)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:F1} GB", bytes / GigaBytes);
        }

        if (bytes >= (ulong)MegaBytes)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} MB", RoundToLong(bytes / MegaBytes));
        }

        if (bytes >= (ulong)KiloBytes)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} KB", RoundToLong(bytes / KiloBytes));
        }

        return string.Format(CultureInfo.InvariantCulture, "{0} B", bytes);
    }

    private static long RoundToLong(double value) => (long)Math.Round(value, MidpointRounding.AwayFromZero);
}
