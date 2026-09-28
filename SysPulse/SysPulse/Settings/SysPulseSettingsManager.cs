// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Globalization;
using System.IO;
using Microsoft.CommandPalette.Extensions.Toolkit;
using SysPulse.Core.Settings;
using SysPulse.Properties;

namespace SysPulse.Settings;

/// <summary>
/// SysPulse's <see cref="JsonSettingsManager"/> subclass (spec §6). Declares all 13 settings keys
/// as <see cref="TextSetting"/>/<see cref="ToggleSetting"/> instances (DECISIONS.md D5: the
/// installed toolkit has no numeric setting type), persists them under the packaged app's
/// LocalState folder, and exposes <see cref="ToOptions"/> to turn the raw, unclamped setting
/// values into a validated <see cref="SysPulseOptions"/>.
/// </summary>
/// <remarks>
/// Pattern mirrored from the built-in extensions (e.g.
/// <c>Microsoft.CmdPal.Ext.TimeDate.Helpers.SettingsManager</c>,
/// <c>Microsoft.CmdPal.Ext.PerformanceMonitor.SettingsManager</c>): compute <see cref="JsonSettingsManager.FilePath"/>
/// via <see cref="Utilities.BaseSettingsPath(string)"/>, add every setting in the constructor, call
/// <see cref="JsonSettingsManager.LoadSettings"/>, and save whenever the settings card is submitted
/// (<c>Settings.SettingsChanged += (s, a) =&gt; SaveSettings();</c>).
/// </remarks>
public sealed class SysPulseSettingsManager : JsonSettingsManager
{
    private static readonly SysPulseOptions Defaults = new();

    private readonly TextSetting _scanIntervalSeconds;
    private readonly TextSetting _retryIntervalSeconds;
    private readonly TextSetting _retryCount;
    private readonly ToggleSetting _cpuMonitoringEnabled;
    private readonly TextSetting _cpuThreshold;
    private readonly ToggleSetting _memoryMonitoringEnabled;
    private readonly TextSetting _memoryThreshold;
    private readonly TextSetting _hysteresisPercent;
    private readonly ToggleSetting _showToast;
    private readonly ToggleSetting _confirmKill;
    private readonly ToggleSetting _killProcessTree;
    private readonly ToggleSetting _compactLabel;
    private readonly TextSetting _protectedProcesses;

    // Guards the write-back in ToOptions() (task requirement 2): programmatically setting a
    // Setting<T>.Value does not currently raise Settings.SettingsChanged (verified against the
    // installed toolkit's Settings.cs -- RaiseSettingsChanged is only called from the rendered
    // settings-card submit path), so this flag is not load-bearing today, but it documents the
    // invariant and protects against a future toolkit change wiring Value's setter to the event.
    private bool _isApplyingNormalization;

    public SysPulseSettingsManager()
        : this(SettingsJsonPath())
    {
    }

    internal SysPulseSettingsManager(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        FilePath = filePath;

        _scanIntervalSeconds = new TextSetting(
            nameof(SysPulseOptions.ScanIntervalSeconds),
            Resources.Settings_ScanIntervalSeconds_Label,
            Resources.Settings_ScanIntervalSeconds_Description,
            Inv(Defaults.ScanIntervalSeconds));

        _retryIntervalSeconds = new TextSetting(
            nameof(SysPulseOptions.RetryIntervalSeconds),
            Resources.Settings_RetryIntervalSeconds_Label,
            Resources.Settings_RetryIntervalSeconds_Description,
            Inv(Defaults.RetryIntervalSeconds));

        _retryCount = new TextSetting(
            nameof(SysPulseOptions.RetryCount),
            Resources.Settings_RetryCount_Label,
            Resources.Settings_RetryCount_Description,
            Inv(Defaults.RetryCount));

        _cpuMonitoringEnabled = new ToggleSetting(
            nameof(SysPulseOptions.CpuMonitoringEnabled),
            Resources.Settings_CpuMonitoringEnabled_Label,
            Resources.Settings_CpuMonitoringEnabled_Description,
            Defaults.CpuMonitoringEnabled);

        _cpuThreshold = new TextSetting(
            nameof(SysPulseOptions.CpuThreshold),
            Resources.Settings_CpuThreshold_Label,
            Resources.Settings_CpuThreshold_Description,
            Inv(Defaults.CpuThreshold));

        _memoryMonitoringEnabled = new ToggleSetting(
            nameof(SysPulseOptions.MemoryMonitoringEnabled),
            Resources.Settings_MemoryMonitoringEnabled_Label,
            Resources.Settings_MemoryMonitoringEnabled_Description,
            Defaults.MemoryMonitoringEnabled);

        _memoryThreshold = new TextSetting(
            nameof(SysPulseOptions.MemoryThreshold),
            Resources.Settings_MemoryThreshold_Label,
            Resources.Settings_MemoryThreshold_Description,
            Inv(Defaults.MemoryThreshold));

        _hysteresisPercent = new TextSetting(
            nameof(SysPulseOptions.HysteresisPercent),
            Resources.Settings_HysteresisPercent_Label,
            Resources.Settings_HysteresisPercent_Description,
            Inv(Defaults.HysteresisPercent));

        _showToast = new ToggleSetting(
            nameof(SysPulseOptions.ShowToast),
            Resources.Settings_ShowToast_Label,
            Resources.Settings_ShowToast_Description,
            Defaults.ShowToast);

        _confirmKill = new ToggleSetting(
            nameof(SysPulseOptions.ConfirmKill),
            Resources.Settings_ConfirmKill_Label,
            Resources.Settings_ConfirmKill_Description,
            Defaults.ConfirmKill);

        _killProcessTree = new ToggleSetting(
            nameof(SysPulseOptions.KillProcessTree),
            Resources.Settings_KillProcessTree_Label,
            Resources.Settings_KillProcessTree_Description,
            Defaults.KillProcessTree);

        _compactLabel = new ToggleSetting(
            nameof(SysPulseOptions.CompactLabel),
            Resources.Settings_CompactLabel_Label,
            Resources.Settings_CompactLabel_Description,
            Defaults.CompactLabel);

        _protectedProcesses = new TextSetting(
            nameof(SysPulseOptions.ProtectedProcesses),
            Resources.Settings_ProtectedProcesses_Label,
            Resources.Settings_ProtectedProcesses_Description,
            Defaults.ProtectedProcesses);

        Settings.Add(_scanIntervalSeconds);
        Settings.Add(_retryIntervalSeconds);
        Settings.Add(_retryCount);
        Settings.Add(_cpuMonitoringEnabled);
        Settings.Add(_cpuThreshold);
        Settings.Add(_memoryMonitoringEnabled);
        Settings.Add(_memoryThreshold);
        Settings.Add(_hysteresisPercent);
        Settings.Add(_showToast);
        Settings.Add(_confirmKill);
        Settings.Add(_killProcessTree);
        Settings.Add(_compactLabel);
        Settings.Add(_protectedProcesses);

        LoadSettings();

        Settings.SettingsChanged += (_, _) =>
        {
            if (_isApplyingNormalization)
            {
                return;
            }

            SaveSettings();
        };
    }

    /// <summary>
    /// Builds a validated <see cref="SysPulseOptions"/> from the current, possibly-raw setting
    /// values (task requirement 2). Numeric text is parsed with
    /// <see cref="SettingParsers.ParseNumber"/> (invariant culture, then current culture -- so a
    /// Turkish-locale user typing <c>"2,5"</c> is accepted -- falling back to the matching
    /// <see cref="SysPulseOptions"/> default on garbage input), then the whole result is run
    /// through <see cref="SysPulseOptions.Normalize"/>. If normalization changed a numeric value
    /// (e.g. <c>RetryIntervalSeconds &gt;= ScanIntervalSeconds</c>), the corrected value is written
    /// back into the owning <see cref="TextSetting"/> so the settings UI reflects the effective
    /// value, and the change is persisted.
    /// </summary>
    /// <returns>A normalized, in-range <see cref="SysPulseOptions"/>.</returns>
    public SysPulseOptions ToOptions()
    {
        var raw = new SysPulseOptions
        {
            ScanIntervalSeconds = ParseInt(_scanIntervalSeconds.Value, Defaults.ScanIntervalSeconds),
            RetryIntervalSeconds = ParseInt(_retryIntervalSeconds.Value, Defaults.RetryIntervalSeconds),
            RetryCount = ParseInt(_retryCount.Value, Defaults.RetryCount),
            CpuMonitoringEnabled = _cpuMonitoringEnabled.Value,
            CpuThreshold = SettingParsers.ParseNumber(_cpuThreshold.Value, Defaults.CpuThreshold),
            MemoryMonitoringEnabled = _memoryMonitoringEnabled.Value,
            MemoryThreshold = SettingParsers.ParseNumber(_memoryThreshold.Value, Defaults.MemoryThreshold),
            HysteresisPercent = SettingParsers.ParseNumber(_hysteresisPercent.Value, Defaults.HysteresisPercent),
            ShowToast = _showToast.Value,
            ConfirmKill = _confirmKill.Value,
            KillProcessTree = _killProcessTree.Value,
            CompactLabel = _compactLabel.Value,
            ProtectedProcesses = _protectedProcesses.Value ?? string.Empty,
        };

        SysPulseOptions normalized = raw.Normalize();
        WriteBackIfChanged(normalized);
        return normalized;
    }

    private static int ParseInt(string? text, int fallback) =>
        (int)Math.Round(SettingParsers.ParseNumber(text, fallback), MidpointRounding.AwayFromZero);

    private void WriteBackIfChanged(SysPulseOptions normalized)
    {
        _isApplyingNormalization = true;
        bool changed = false;
        try
        {
            changed |= SetIfChanged(_scanIntervalSeconds, Inv(normalized.ScanIntervalSeconds));
            changed |= SetIfChanged(_retryIntervalSeconds, Inv(normalized.RetryIntervalSeconds));
            changed |= SetIfChanged(_retryCount, Inv(normalized.RetryCount));
            changed |= SetIfChanged(_cpuThreshold, Inv(normalized.CpuThreshold));
            changed |= SetIfChanged(_memoryThreshold, Inv(normalized.MemoryThreshold));
            changed |= SetIfChanged(_hysteresisPercent, Inv(normalized.HysteresisPercent));
        }
        finally
        {
            _isApplyingNormalization = false;
        }

        if (changed)
        {
            SaveSettings();
        }
    }

    private static bool SetIfChanged(TextSetting setting, string value)
    {
        if (string.Equals(setting.Value, value, StringComparison.Ordinal))
        {
            return false;
        }

        setting.Value = value;
        return true;
    }

    private static string Inv(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Inv(double value) => value.ToString(CultureInfo.InvariantCulture);

    private static string SettingsJsonPath()
    {
        string directory = Utilities.BaseSettingsPath("SysPulse");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "settings.json");
    }
}
