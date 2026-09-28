using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SysPulse.Core.Formatting;
using SysPulse.Core.Monitoring;
using SysPulse.Core.Processes;
using SysPulse.Core.Settings;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace SysPulse.Notifications;

/// <summary>
/// <see cref="IAlertNotifier"/> implementation that shows a Windows toast via the WinRT
/// <see cref="ToastNotificationManager"/> (DECISIONS.md D10 — no COM activator/AppNotificationManager,
/// no click-to-open).
/// </summary>
/// <remarks>
/// <see cref="NotifyAlert"/> is called synchronously from <see cref="MonitorLoop"/>'s background
/// loop and must return immediately: all work (process sampling, toast construction/display)
/// happens on a background <see cref="Task"/>. Every failure is caught and logged via
/// <see cref="Debug.WriteLine(string)"/> — this notifier never throws back into the monitor loop.
/// Owns a dedicated <see cref="ProcessSampler"/> (not shared with <c>TopProcessesPage</c>, which
/// is not thread-safe) so it can take its own two-sample CPU delta without racing the flyout's
/// sampling. Callers must <see cref="Dispose"/> this instance to release that sampler.
/// </remarks>
public sealed partial class AlertNotifier : IAlertNotifier, IDisposable
{
    private const string ToastTag = "syspulse-alert";
    private const string ToastGroup = "syspulse";

    // CPU%/composite ranking needs a delta between two samples (spec §5.1); memory ranking only
    // needs one. Kept short so the alert toast isn't noticeably delayed.
    private static readonly TimeSpan CpuSampleGap = TimeSpan.FromSeconds(1);

    private readonly Func<SysPulseOptions> _optionsAccessor;
    private readonly ProcessSampler _processSampler = new();
    private volatile bool _toastDisabledForSession;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="AlertNotifier"/> class.
    /// </summary>
    /// <param name="optionsAccessor">
    /// Returns the current, live <see cref="SysPulseOptions"/> at call time (the provider's
    /// options can change between alerts via the settings page).
    /// </param>
    public AlertNotifier(Func<SysPulseOptions> optionsAccessor)
    {
        ArgumentNullException.ThrowIfNull(optionsAccessor);
        _optionsAccessor = optionsAccessor;
    }

    /// <inheritdoc />
    public void NotifyAlert(SystemSnapshot snapshot, BreachKind breachKind)
    {
        // Must return immediately -- this is invoked from MonitorLoop's own sampling cycle.
        try
        {
            _ = Task.Run(() => NotifyAlertCore(snapshot, breachKind));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SysPulse: AlertNotifier failed to schedule alert work: {ex}");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _processSampler.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SysPulse: AlertNotifier failed to dispose its ProcessSampler: {ex}");
        }
    }

    private void NotifyAlertCore(SystemSnapshot snapshot, BreachKind breachKind)
    {
        try
        {
            SysPulseOptions options = _optionsAccessor();
            int approxDurationSeconds = Math.Max(0, options.RetryCount * options.RetryIntervalSeconds);
            ProcessSample? topProcess = TrySampleTopProcess(breachKind, options, snapshot);
            string body = AlertMessageFormatter.BuildBody(snapshot, breachKind, approxDurationSeconds, topProcess);

            ShowToast(body);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SysPulse: AlertNotifier failed to build/show the alert toast: {ex}");
        }
    }

    private ProcessSample? TrySampleTopProcess(BreachKind breachKind, SysPulseOptions options, SystemSnapshot snapshot)
    {
        try
        {
            IReadOnlyList<ProcessSample> samples;
            if (breachKind == BreachKind.Memory)
            {
                // Memory ranking needs no delta -- one sample suffices.
                samples = _processSampler.Sample();
            }
            else
            {
                // Cpu/Both: CPU% is delta-based, so prime with a first sample and wait before the
                // one actually used for ranking.
                _processSampler.Sample();
                Thread.Sleep(CpuSampleGap);
                samples = _processSampler.Sample();
            }

            IReadOnlyList<ProcessSample> top = ProcessRanker.Top(samples, breachKind, HealthState.Alert, options, snapshot.TotalPhysBytes, 1);
            return top.Count > 0 ? top[0] : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SysPulse: AlertNotifier failed to sample the top process: {ex}");
            return null;
        }
    }

    private void ShowToast(string body)
    {
        if (_toastDisabledForSession)
        {
            return;
        }

        try
        {
            XmlDocument xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
            XmlNodeList textNodes = xml.GetElementsByTagName("text");

            // DOM text nodes are serialized/escaped by the XmlDocument itself -- no manual
            // string-concatenation-into-XML, so embedded "&", "<", ">" etc. in a process name or
            // percentage text can never break the markup.
            textNodes.Item(0).AppendChild(xml.CreateTextNode(AlertMessageFormatter.Title));
            textNodes.Item(1).AppendChild(xml.CreateTextNode(body));

            var toast = new ToastNotification(xml)
            {
                Tag = ToastTag,
                Group = ToastGroup,
            };

            // Packaged identity: CreateToastNotifier() with no AUMID argument.
            ToastNotificationManager.CreateToastNotifier().Show(toast);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SysPulse: toast display failed (identity/policy?), disabling further toasts for this session: {ex}");
            _toastDisabledForSession = true;
        }
    }
}
