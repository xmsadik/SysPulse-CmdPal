using System.Globalization;

namespace SysPulse.Core.Settings;

/// <summary>
/// Pure parsing helpers for the numeric settings the Settings phase stores as raw text (spec §6,
/// DECISIONS.md D5: the toolkit has no numeric setting type, so every number is a
/// <c>TextSetting</c> parsed by hand). Kept in <c>SysPulse.Core</c> so it is unit-testable without
/// the MSIX/COM-server project.
/// </summary>
public static class SettingParsers
{
    /// <summary>
    /// Parses <paramref name="text"/> as a floating-point number, tolerating both
    /// <see cref="CultureInfo.InvariantCulture"/> (e.g. <c>"2.5"</c>) and
    /// <see cref="CultureInfo.CurrentCulture"/> formatting (e.g. Turkish-locale <c>"2,5"</c>).
    /// Falls back to <paramref name="fallback"/> for null/blank/unparsable input, never throws.
    /// </summary>
    /// <param name="text">The raw text from a <c>TextSetting</c>'s <c>Value</c>, or null.</param>
    /// <param name="fallback">The value to return when <paramref name="text"/> is blank or not a valid number in either culture.</param>
    /// <returns>The parsed number, or <paramref name="fallback"/>.</returns>
    public static double ParseNumber(string? text, double fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        string trimmed = text.Trim();

        // Code-review finding 5: double.TryParse accepts "NaN"/"Infinity"/"-Infinity" (their
        // NumberFormatInfo symbols) regardless of style flags; a settings value that parses to a
        // non-finite number is exactly as unusable downstream (clamping, arithmetic) as one that
        // fails to parse at all, so it falls back the same way.
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double invariantValue))
        {
            return double.IsFinite(invariantValue) ? invariantValue : fallback;
        }

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out double currentCultureValue))
        {
            return double.IsFinite(currentCultureValue) ? currentCultureValue : fallback;
        }

        return fallback;
    }
}
