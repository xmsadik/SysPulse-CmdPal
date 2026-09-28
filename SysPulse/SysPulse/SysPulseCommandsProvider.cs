// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using SysPulse.Core.Monitoring;
using SysPulse.Core.Processes;
using SysPulse.Core.Settings;
using SysPulse.Dock;
using SysPulse.Logging;
using SysPulse.Notifications;
using SysPulse.Pages;
using SysPulse.Properties;
using SysPulse.Settings;

namespace SysPulse;

public partial class SysPulseCommandsProvider : CommandProvider
{
    private readonly ICommandItem[] _commands;
    private readonly ICommandItem[] _dockBands;

    private readonly TopProcessesPage _topProcessesPage;
    private readonly SysPulseSettingsManager _settingsManager;
    private readonly SystemSampler _sampler;
    private readonly ProcessSampler _processSampler;
    private readonly HealthMonitor _healthMonitor;
    private readonly MonitorLoop _monitorLoop;
    private readonly AlertNotifier _alertNotifier;
    private readonly StatusDockItem _statusItem;
    private readonly OnLoadDockBandItem _dockBandItem;
    private readonly MonitorLease _lease = new();
    private readonly Lock _lastSampleLock = new();

    // Published from the monitor loop's background thread (OnSampled) and read from whatever
    // thread raises Settings.SettingsChanged (task requirement 3); a plain volatile reference
    // assignment is enough for SysPulseOptions since it's an immutable record, but the two
    // "last sample" fields below must be updated together, hence the explicit lock.
    private volatile SysPulseOptions _options;
    private ProtectedProcessList _protectedProcessList;
    private HealthEvaluation? _lastEvaluation;
    private SystemSnapshot? _lastSnapshot;
    private bool _disposed;

    public SysPulseCommandsProvider()
    {
#if DEBUG
        Log.Initialize(debugEnabled: true);
#else
        Log.Initialize(debugEnabled: false);
#endif
        Log.Info("SysPulse extension starting up.");

        Id = "com.syspulse.extension";
        DisplayName = "SysPulse";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");

        _settingsManager = new SysPulseSettingsManager();
        _options = _settingsManager.ToOptions();
        Settings = _settingsManager.Settings;

        _sampler = new SystemSampler();
        _processSampler = new ProcessSampler();
        _protectedProcessList = ProtectedProcessList.Create(_options, Environment.ProcessId);
        _healthMonitor = new HealthMonitor(_options);
        _alertNotifier = new AlertNotifier(() => _options);
        _monitorLoop = new MonitorLoop(_sampler, _healthMonitor, _options, notifier: _alertNotifier);
        _monitorLoop.Sampled += OnSampled;
        _monitorLoop.Faulted += OnFaulted;

        // Created ONCE and kept for the whole lifetime (spec §4.1 / §3 leak-avoidance pitfall):
        // reuse stable instances, never rebuild the band/page item arrays on each tick. The same
        // page instance is used both as the top-level command and as the dock band's click
        // target (task requirement 4), and its Id is non-empty (set in its own constructor),
        // satisfying the dock band Command.Id requirement (spec §3). The options accessor reads
        // the live _options field on every call, so every consumer (TopProcessesPage,
        // ProcessListItem, KillProcessCommand) observes settings changes without a stale copy.
        _topProcessesPage = new TopProcessesPage(
            _processSampler,
            _healthMonitor,
            _monitorLoop,
            () => _options,
            () => _protectedProcessList,
            OnBandLoaded,
            OnBandUnloaded,
            _settingsManager.Settings.SettingsPage);

        // Settings reachability (task requirement 3): mirrors the built-in TimeDate/Performance
        // Monitor extensions, which expose the auto-generated settings card both via the
        // provider's `Settings` property (assigned above -- the host's own "Extension settings"
        // surface, e.g. the CmdPal Extensions settings page) *and* as a context-menu entry on a
        // top-level command, since nothing in the installed SDK adds one automatically.
        _commands = [
            new CommandItem(_topProcessesPage)
            {
                Title = Resources.Provider_TopLevelTitle,
                MoreCommands = [new CommandContextItem(_settingsManager.Settings.SettingsPage)],
            },
        ];

        _statusItem = new StatusDockItem(_topProcessesPage);

        // DECISIONS.md D3/D8: lifecycle-gated band, ported from PowerToys TimeDate. The monitor
        // loop only runs while the band (or, later, the Top-5 page) is actually on screen.
        _dockBandItem = new OnLoadDockBandItem(
            [_statusItem],
            "com.syspulse.dockband",
            "SysPulse",
            OnBandLoaded,
            OnBandUnloaded);

        _dockBands = [_dockBandItem];

        _settingsManager.Settings.SettingsChanged += OnSettingsChanged;
    }

    public override ICommandItem[] TopLevelCommands()
    {
        return _commands;
    }

    public override ICommandItem[]? GetDockBands() => _dockBands;

    /// <summary>
    /// Rebuilds <see cref="_protectedProcessList"/> from the current <see cref="_options"/> (task
    /// requirement 3). Called from <see cref="OnSettingsChanged"/> whenever the user edits
    /// <see cref="SysPulseOptions.ProtectedProcesses"/> (or any other setting).
    /// </summary>
    internal void RebuildProtectedProcessList()
    {
        _protectedProcessList = ProtectedProcessList.Create(_options, Environment.ProcessId);
    }

    private void OnSampled(HealthEvaluation evaluation, SystemSnapshot snapshot)
    {
        lock (_lastSampleLock)
        {
            _lastEvaluation = evaluation;
            _lastSnapshot = snapshot;
        }

        // Alert enter/leave, with the values that drove the transition (task requirement H2).
        // Deliberately not logged on every tick -- only on the edges -- to keep the file small.
        if (evaluation.EnteredAlert)
        {
            Log.Info(FormattableString.Invariant(
                $"Alert entered: breach={evaluation.BreachKind}, cpu={snapshot.CpuPercent:F1}%, mem={snapshot.MemoryPercent:F1}%."));
        }
        else if (evaluation.LeftAlert)
        {
            Log.Info(FormattableString.Invariant(
                $"Alert left: cpu={snapshot.CpuPercent:F1}%, mem={snapshot.MemoryPercent:F1}%."));
        }

        try
        {
            _statusItem.Apply(evaluation, snapshot, _options);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SysPulse: StatusDockItem.Apply threw: {ex}");
        }
    }

    private void OnFaulted(Exception ex)
    {
        Log.Error("Monitor loop sampling cycle faulted", ex);
        Debug.WriteLine($"SysPulse: monitor loop cycle faulted: {ex}");
    }

    /// <summary>
    /// Reacts to any settings-card save (task requirement 3): rebuilds <see cref="_options"/> from
    /// the settings manager (parsing/clamping happens in <see cref="SysPulseSettingsManager.ToOptions"/>),
    /// publishes it via the volatile <see cref="_options"/> field so every accessor-based consumer
    /// (TopProcessesPage, ProcessListItem, KillProcessCommand) picks it up on its next read,
    /// pushes it into the running <see cref="MonitorLoop"/>, rebuilds the protected-process list,
    /// and immediately re-renders the dock label from the last known sample -- so a toggle like
    /// <see cref="SysPulseOptions.CompactLabel"/> takes effect without waiting for the next tick.
    /// </summary>
    private void OnSettingsChanged(object sender, Microsoft.CommandPalette.Extensions.Toolkit.Settings settings)
    {
        try
        {
            SysPulseOptions newOptions = _settingsManager.ToOptions();
            _options = newOptions;
            _monitorLoop.UpdateOptions(newOptions);
            RebuildProtectedProcessList();
            ReapplyStatusLabel(newOptions);
            Log.Info(string.Format(
                CultureInfo.InvariantCulture,
                "Settings changed: scan={0}s, retry={1}s x{2}, cpuThreshold={3}% (enabled={4}), memThreshold={5}% (enabled={6}).",
                newOptions.ScanIntervalSeconds,
                newOptions.RetryIntervalSeconds,
                newOptions.RetryCount,
                newOptions.CpuThreshold,
                newOptions.CpuMonitoringEnabled,
                newOptions.MemoryThreshold,
                newOptions.MemoryMonitoringEnabled));
        }
        catch (Exception ex)
        {
            Log.Error("Failed to apply changed settings", ex);
            Debug.WriteLine($"SysPulse: failed to apply changed settings: {ex}");
        }
    }

    private void ReapplyStatusLabel(SysPulseOptions options)
    {
        HealthEvaluation? evaluation;
        SystemSnapshot? snapshot;
        lock (_lastSampleLock)
        {
            evaluation = _lastEvaluation;
            snapshot = _lastSnapshot;
        }

        if (evaluation is null || snapshot is null)
        {
            // No sample yet (band never ticked, e.g. it isn't on screen) -- OnSampled will apply
            // the current options on the first tick once the loop is running.
            return;
        }

        try
        {
            _statusItem.Apply(evaluation.Value, snapshot.Value, options);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SysPulse: StatusDockItem.Apply threw during settings reapply: {ex}");
        }
    }

    private void OnBandLoaded()
    {
        if (!_lease.Acquire())
        {
            return;
        }

        try
        {
            _monitorLoop.Start(TimeSpan.FromSeconds(1));
            Log.Info("Monitor loop started.");
        }
        catch (Exception ex)
        {
            Log.Error("Failed to start monitor loop", ex);
            Debug.WriteLine($"SysPulse: failed to start monitor loop: {ex}");
        }
    }

    private void OnBandUnloaded()
    {
        if (!_lease.Release())
        {
            return;
        }

        // Fire-and-forget: Unloaded() is called from CmdPal's own thread and must not block.
        _ = StopMonitorLoopAsync();
    }

    private async Task StopMonitorLoopAsync()
    {
        try
        {
            await _monitorLoop.StopAsync().ConfigureAwait(false);
            Log.Info("Monitor loop stopped.");
        }
        catch (Exception ex)
        {
            Log.Error("Failed to stop monitor loop", ex);
            Debug.WriteLine($"SysPulse: failed to stop monitor loop: {ex}");
        }
    }

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _settingsManager.Settings.SettingsChanged -= OnSettingsChanged;
        _monitorLoop.Sampled -= OnSampled;
        _monitorLoop.Faulted -= OnFaulted;

        // Fire-and-forget for the same reason as OnBandUnloaded: don't block the caller.
        _ = StopMonitorLoopAsync();

        try
        {
            _processSampler.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SysPulse: failed to dispose ProcessSampler: {ex}");
        }

        try
        {
            _alertNotifier.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SysPulse: failed to dispose AlertNotifier: {ex}");
        }

        GC.SuppressFinalize(this);
        base.Dispose();
    }

    /// <summary>
    /// Thread-safe ref-count gating the monitor loop's start/stop: the Dock status band and the
    /// Top-5 flyout page both call <see cref="Acquire"/>/<see cref="Release"/> when they
    /// load/unload, so the loop runs only while at least one of them is on screen.
    /// </summary>
    private sealed class MonitorLease
    {
        private readonly Lock _lock = new();
        private int _count;

        /// <summary>Increments the lease count. Returns true exactly on the 0→1 transition.</summary>
        public bool Acquire()
        {
            lock (_lock)
            {
                return ++_count == 1;
            }
        }

        /// <summary>
        /// Decrements the lease count, never below zero (the host may unsubscribe without a
        /// matching subscribe, which still raises Unloaded). Returns true exactly on 1→0.
        /// </summary>
        public bool Release()
        {
            lock (_lock)
            {
                if (_count == 0)
                {
                    return false;
                }

                return --_count == 0;
            }
        }
    }
}
