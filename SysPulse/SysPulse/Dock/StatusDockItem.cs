// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.Globalization;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using SysPulse.Core.Monitoring;
using SysPulse.Core.Settings;

namespace SysPulse.Dock;

/// <summary>
/// The single dock-band status item: a compact "CPU x% · MEM y%" readout that switches to a
/// yellow warning state while <see cref="HealthMonitor"/> is in <see cref="HealthState.Alert"/>.
/// Clicking it invokes <see cref="Command"/> (a page opened in the flyout).
/// </summary>
/// <remarks>
/// Instance reuse: per spec §3's leak-avoidance pitfall and the Performance Monitor band's
/// PR #48880 fix, a single <see cref="StatusDockItem"/> is created once by the provider and
/// reused for the extension's lifetime -- <see cref="Apply"/> only mutates its properties.
/// Threading: per DECISIONS.md D4 (confirmed against <c>NowDockBand</c>), <see cref="Apply"/>
/// is called directly from the monitor loop's background thread; there is no dispatcher, and
/// every property set is wrapped in its own try/catch so a throwing host-side PropChanged
/// handler (PowerToys issue #50483) cannot block later updates to this item.
/// </remarks>
public sealed partial class StatusDockItem : ListItem
{
    private const string InitialTitle = "CPU –%";
    private const string InitialSubtitle = "MEM –%";

    private static readonly IconInfo NormalIconValue = IconHelpers.FromRelativePath("Assets\\pulse.svg");
    private static readonly IconInfo WarningIconValue = IconHelpers.FromRelativePath("Assets\\warning-yellow.svg");

    // DockLabelWidth / SetDockLabelReservations / SetDockLabelWidthLimits (used by the built-in
    // Performance Monitor band, see PerformanceMonitorDockItemPresentation.cs) are NOT present in
    // the installed Microsoft.CommandPalette.Extensions 0.12.260812002 package -- verified absent
    // from Microsoft.CommandPalette.Extensions.Toolkit.dll's type table (no "DockLabel*" types).
    // These APIs post-date this SDK release on the PowerToys main branch. Skipped; see the task
    // report for details. The title format is fixed-ish width in practice (two 3-digit percentages)
    // so jitter is minor, but this should be revisited once the toolkit package ships the API.
    private bool _isAlert;

    /// <summary>
    /// Initializes a new instance of the <see cref="StatusDockItem"/> class.
    /// </summary>
    /// <param name="command">
    /// The command invoked when the band item is clicked (a page opened in the flyout). Must
    /// have a non-empty <see cref="ICommand.Id"/> -- CmdPal ignores dock band items whose
    /// command Id is empty.
    /// </param>
    public StatusDockItem(ICommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrEmpty(command.Id))
        {
            throw new ArgumentException("The dock band item's Command must have a non-empty Id.", nameof(command));
        }

        Command = command;
        Title = InitialTitle;
        Subtitle = InitialSubtitle;
        Icon = NormalIconValue;
    }

    /// <summary>
    /// Updates <see cref="ListItem.Title"/>, <see cref="ListItem.Subtitle"/>, and
    /// <see cref="ListItem.Icon"/> for the given evaluation/snapshot, per spec §5.3.
    /// </summary>
    /// <remarks>
    /// Both CPU and memory percentages are always shown, even if
    /// <see cref="SysPulseOptions.CpuMonitoringEnabled"/> or
    /// <see cref="SysPulseOptions.MemoryMonitoringEnabled"/> is off for alerting purposes --
    /// a disabled metric is still informative to glance at on the band, it just never
    /// contributes to a breach/alert. <see cref="HealthState.Verifying"/> is rendered
    /// identically to <see cref="HealthState.Normal"/> so the user never sees a flicker
    /// while a breach is being re-checked.
    /// </remarks>
    /// <param name="eval">The latest state-machine evaluation.</param>
    /// <param name="snap">The snapshot that produced <paramref name="eval"/>.</param>
    /// <param name="opts">The current options (for <see cref="SysPulseOptions.CompactLabel"/>).</param>
    public void Apply(HealthEvaluation eval, SystemSnapshot snap, SysPulseOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);

        bool alert = eval.State == HealthState.Alert;
        (string title, string subtitle) = BuildLabel(snap, opts, alert ? eval.BreachKind : BreachKind.None);

        SetTitleIfChanged(title);
        SetSubtitleIfChanged(subtitle);
        SetIconIfChanged(alert);
    }

    private void SetTitleIfChanged(string title)
    {
        if (string.Equals(Title, title, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            Title = title;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SysPulse: failed to set dock item Title: {ex}");
        }
    }

    private void SetSubtitleIfChanged(string subtitle)
    {
        if (string.Equals(Subtitle, subtitle, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            Subtitle = subtitle;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SysPulse: failed to set dock item Subtitle: {ex}");
        }
    }

    private void SetIconIfChanged(bool alert)
    {
        if (_isAlert == alert)
        {
            return;
        }

        try
        {
            Icon = alert ? WarningIconValue : NormalIconValue;
            _isAlert = alert;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SysPulse: failed to set dock item Icon: {ex}");
        }
    }

    /// <summary>
    /// Two-line label (DECISIONS.md D11): CPU in the title, memory in the subtitle, the alert
    /// reason appended to the subtitle. CompactLabel keeps a single "30% | 50%" title with only
    /// the alert reason below. The yellow icon is the alert cue; no "⚠" in the text.
    /// </summary>
    internal static (string Title, string Subtitle) BuildLabel(SystemSnapshot snap, SysPulseOptions opts, BreachKind alertKind)
    {
        int cpu = RoundPercent(snap.CpuPercent);
        int mem = RoundPercent(snap.MemoryPercent);
        string reason = BuildAlertSubtitle(alertKind);

        if (opts.CompactLabel)
        {
            return (string.Format(CultureInfo.InvariantCulture, "{0}% | {1}%", cpu, mem), reason);
        }

        string title = string.Format(CultureInfo.InvariantCulture, "CPU {0}%", cpu);
        string memText = string.Format(CultureInfo.InvariantCulture, "MEM {0}%", mem);
        return (title, reason.Length == 0 ? memText : memText + " · " + reason);
    }

    private static string BuildAlertSubtitle(BreachKind kind) => kind switch
    {
        BreachKind.Cpu => "High CPU",
        BreachKind.Memory => "High memory",
        BreachKind.Both => "High CPU & memory",
        _ => string.Empty,
    };

    private static int RoundPercent(double value) => (int)Math.Round(Math.Clamp(value, 0, 100), MidpointRounding.AwayFromZero);
}
