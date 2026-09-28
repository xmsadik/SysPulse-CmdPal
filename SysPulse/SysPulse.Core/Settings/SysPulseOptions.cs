namespace SysPulse.Core.Settings;

/// <summary>
/// Immutable, validated configuration for the SysPulse monitor. Values are not guaranteed to be
/// in range until <see cref="Normalize"/> (or the static <see cref="Clamp(SysPulseOptions)"/>) has
/// been applied — call it once after loading raw/user-edited settings.
/// </summary>
public sealed record SysPulseOptions
{
    /// <summary>Minimum allowed value for <see cref="ScanIntervalSeconds"/>.</summary>
    public const int MinScanIntervalSeconds = 2;

    /// <summary>Maximum allowed value for <see cref="ScanIntervalSeconds"/>.</summary>
    public const int MaxScanIntervalSeconds = 300;

    /// <summary>Minimum allowed value for <see cref="RetryIntervalSeconds"/>.</summary>
    public const int MinRetryIntervalSeconds = 1;

    /// <summary>Maximum allowed value for <see cref="RetryIntervalSeconds"/>.</summary>
    public const int MaxRetryIntervalSeconds = 60;

    /// <summary>Minimum allowed value for <see cref="RetryCount"/>.</summary>
    public const int MinRetryCount = 1;

    /// <summary>Maximum allowed value for <see cref="RetryCount"/>.</summary>
    public const int MaxRetryCount = 10;

    /// <summary>Minimum allowed value for <see cref="CpuThreshold"/> and <see cref="MemoryThreshold"/>.</summary>
    public const double MinThreshold = 10;

    /// <summary>Maximum allowed value for <see cref="CpuThreshold"/> and <see cref="MemoryThreshold"/>.</summary>
    public const double MaxThreshold = 100;

    /// <summary>Minimum allowed value for <see cref="HysteresisPercent"/>.</summary>
    public const double MinHysteresisPercent = 0;

    /// <summary>Maximum allowed value for <see cref="HysteresisPercent"/>.</summary>
    public const double MaxHysteresisPercent = 20;

    /// <summary>How often (seconds) to sample system metrics while in <see cref="Monitoring.HealthState.Normal"/> or <see cref="Monitoring.HealthState.Alert"/>. Range 2–300.</summary>
    public int ScanIntervalSeconds { get; init; } = 10;

    /// <summary>How often (seconds) to re-check while <see cref="Monitoring.HealthState.Verifying"/> a breach. Range 1–60; must be less than <see cref="ScanIntervalSeconds"/>.</summary>
    public int RetryIntervalSeconds { get; init; } = 3;

    /// <summary>Number of consecutive retry checks that must all breach before entering <see cref="Monitoring.HealthState.Alert"/>. Range 1–10.</summary>
    public int RetryCount { get; init; } = 3;

    /// <summary>Whether CPU usage is considered when evaluating a breach.</summary>
    public bool CpuMonitoringEnabled { get; init; } = true;

    /// <summary>CPU percent at or above which a breach is considered, when <see cref="CpuMonitoringEnabled"/> is true. Range 10–100.</summary>
    public double CpuThreshold { get; init; } = 85;

    /// <summary>Whether memory usage is considered when evaluating a breach.</summary>
    public bool MemoryMonitoringEnabled { get; init; } = true;

    /// <summary>Memory percent at or above which a breach is considered, when <see cref="MemoryMonitoringEnabled"/> is true. Range 10–100.</summary>
    public double MemoryThreshold { get; init; } = 90;

    /// <summary>Margin below a threshold a metric must drop to before leaving <see cref="Monitoring.HealthState.Alert"/>, to avoid flapping. Range 0–20.</summary>
    public double HysteresisPercent { get; init; } = 5;

    /// <summary>Whether a Windows toast notification is shown on entering <see cref="Monitoring.HealthState.Alert"/>.</summary>
    public bool ShowToast { get; init; } = true;

    /// <summary>Whether killing a process requires user confirmation.</summary>
    public bool ConfirmKill { get; init; } = true;

    /// <summary>Whether killing a process also kills its descendant process tree.</summary>
    public bool KillProcessTree { get; init; }

    /// <summary>Whether the dock label uses the compact <c>30% | 50%</c> format instead of <c>CPU 30% · MEM 50%</c>.</summary>
    public bool CompactLabel { get; init; }

    /// <summary>
    /// Extra process names that must never be offered for killing, as raw comma-separated text
    /// (case-insensitive, <c>.exe</c> suffix optional). Use <see cref="ParsedProtectedProcesses"/>
    /// for the normalized list.
    /// </summary>
    public string ProtectedProcesses { get; init; } = string.Empty;

    /// <summary>
    /// <see cref="ProtectedProcesses"/> parsed into a trimmed, non-empty, <c>.exe</c>-stripped,
    /// case-insensitively de-duplicated list, preserving first-seen order.
    /// </summary>
    public IReadOnlyList<string> ParsedProtectedProcesses => ParseProtectedProcesses(ProtectedProcesses);

    /// <summary>
    /// Returns a copy of this instance with every value clamped into its valid range.
    /// Equivalent to <see cref="Clamp(SysPulseOptions)"/> applied to this instance.
    /// </summary>
    /// <returns>A valid, in-range copy of this options instance.</returns>
    public SysPulseOptions Normalize() => Clamp(this);

    /// <summary>
    /// Returns a copy of <paramref name="options"/> with every value clamped into its valid
    /// range. If <see cref="RetryIntervalSeconds"/> would not be strictly less than
    /// <see cref="ScanIntervalSeconds"/> after clamping, it is set to
    /// <c>max(1, ScanIntervalSeconds - 1)</c>.
    /// </summary>
    /// <param name="options">The options to validate.</param>
    /// <returns>A valid, in-range copy of <paramref name="options"/>.</returns>
    public static SysPulseOptions Clamp(SysPulseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        int scanInterval = Clamp(options.ScanIntervalSeconds, MinScanIntervalSeconds, MaxScanIntervalSeconds);
        int retryInterval = Clamp(options.RetryIntervalSeconds, MinRetryIntervalSeconds, MaxRetryIntervalSeconds);
        if (retryInterval >= scanInterval)
        {
            retryInterval = Math.Max(1, scanInterval - 1);
        }

        return options with
        {
            ScanIntervalSeconds = scanInterval,
            RetryIntervalSeconds = retryInterval,
            RetryCount = Clamp(options.RetryCount, MinRetryCount, MaxRetryCount),
            CpuThreshold = ClampFinite(options.CpuThreshold, MinThreshold, MaxThreshold, DefaultValues.CpuThreshold),
            MemoryThreshold = ClampFinite(options.MemoryThreshold, MinThreshold, MaxThreshold, DefaultValues.MemoryThreshold),
            HysteresisPercent = ClampFinite(options.HysteresisPercent, MinHysteresisPercent, MaxHysteresisPercent, DefaultValues.HysteresisPercent),
        };
    }

    /// <summary>Default property values, used by <see cref="Clamp(SysPulseOptions)"/> as the fallback for a non-finite double.</summary>
    private static readonly SysPulseOptions DefaultValues = new();

    /// <summary>
    /// Clamps <paramref name="value"/> to <c>[min, max]</c>, or returns <paramref name="fallbackDefault"/>
    /// if <paramref name="value"/> is <c>NaN</c> or infinite (code-review finding 5: <see cref="Math.Clamp(double, double, double)"/>
    /// leaves <c>NaN</c> unchanged -- every comparison against <c>NaN</c> is false -- and would
    /// pass an infinite value straight through unless it happens to already sit outside the range).
    /// </summary>
    private static double ClampFinite(double value, double min, double max, double fallbackDefault) =>
        double.IsFinite(value) ? Clamp(value, min, max) : fallbackDefault;

    /// <summary>Clamps <paramref name="value"/> to the inclusive range <c>[min, max]</c>.</summary>
    public static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);

    /// <summary>Clamps <paramref name="value"/> to the inclusive range <c>[min, max]</c>.</summary>
    public static double Clamp(double value, double min, double max) => Math.Clamp(value, min, max);

    /// <summary>
    /// Parses a raw comma-separated process-name list into a trimmed, non-empty, <c>.exe</c>-stripped,
    /// case-insensitively de-duplicated list, preserving first-seen order.
    /// </summary>
    /// <param name="raw">Raw comma-separated process names, e.g. <c>"foo.exe, Bar, bar"</c>.</param>
    /// <returns>The normalized list (empty if <paramref name="raw"/> is null/blank).</returns>
    public static IReadOnlyList<string> ParseProtectedProcesses(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (string part in raw.Split(','))
        {
            string name = part.Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^4];
            }

            if (name.Length == 0)
            {
                continue;
            }

            if (seen.Add(name))
            {
                result.Add(name);
            }
        }

        return result;
    }
}
