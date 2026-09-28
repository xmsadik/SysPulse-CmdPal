using System.Globalization;
using SysPulse.Core.Monitoring;
using SysPulse.Core.Processes;
using SysPulse.Core.Properties;

namespace SysPulse.Core.Formatting;

/// <summary>
/// Pure formatting for the alert toast body shown once per alert episode (spec §5.4). Kept in
/// Core, alongside <see cref="ByteFormatter"/> and <see cref="ProcessSubtitleFormatter"/>, so it
/// is unit-testable without the SDK or any WinRT toast API.
/// </summary>
public static class AlertMessageFormatter
{
    /// <summary>The fixed toast title (spec §5.4).</summary>
    public const string Title = "SysPulse";

    /// <summary>
    /// Builds the toast body, e.g. <c>CPU at 94% for the last ~9 s. Top: chrome.exe (38%).</c>
    /// </summary>
    /// <remarks>
    /// The top-process value shown matches the metric that drove the alert: CPU% for
    /// <see cref="BreachKind.Cpu"/> and <see cref="BreachKind.Both"/> (the dominant signal for a
    /// mixed breach), private-bytes size for <see cref="BreachKind.Memory"/>.
    /// </remarks>
    /// <param name="snapshot">The snapshot that confirmed the alert.</param>
    /// <param name="breachKind">Which metric(s) triggered the alert.</param>
    /// <param name="approxDurationSeconds">
    /// The estimated time the breach persisted before the alert fired (<c>RetryCount × RetryIntervalSeconds</c>
    /// at alert time), shown with a leading <c>~</c> since it is an estimate, not a measured duration.
    /// </param>
    /// <param name="topProcess">
    /// The highest-impact process for <paramref name="breachKind"/>, or <see langword="null"/> if
    /// process sampling failed or found nothing -- the "Top:" clause is omitted in that case.
    /// </param>
    /// <returns>The formatted, invariant-culture toast body.</returns>
    public static string BuildBody(SystemSnapshot snapshot, BreachKind breachKind, int approxDurationSeconds, ProcessSample? topProcess)
    {
        // Resource text is looked up via CurrentUICulture (the generated Designer.cs default);
        // every numeric substitution is formatted with InvariantCulture so digits never change
        // shape across locales (see AlertMessageFormatterTests.BuildBody_UsesInvariantCultureRegardlessOfCurrentCulture).
        string metrics = breachKind switch
        {
            BreachKind.Memory => string.Format(CultureInfo.InvariantCulture, Resources.Alert_Memory, RoundPercent(snapshot.MemoryPercent)),
            BreachKind.Both => string.Format(CultureInfo.InvariantCulture, Resources.Alert_Both, RoundPercent(snapshot.CpuPercent), RoundPercent(snapshot.MemoryPercent)),
            _ => string.Format(CultureInfo.InvariantCulture, Resources.Alert_Cpu, RoundPercent(snapshot.CpuPercent)),
        };

        string body = string.Format(CultureInfo.InvariantCulture, Resources.Alert_ForTheLast, metrics, approxDurationSeconds);

        if (topProcess is { } top)
        {
            string value = breachKind == BreachKind.Memory
                ? ByteFormatter.Format(top.PrivateBytes)
                : string.Format(CultureInfo.InvariantCulture, "{0}%", RoundPercent(top.CpuPercent));
            body += string.Format(CultureInfo.InvariantCulture, Resources.Alert_TopProcess, top.Name, value);
        }

        return body;
    }

    private static int RoundPercent(double value) => (int)Math.Round(Math.Clamp(value, 0, 100), MidpointRounding.AwayFromZero);
}
