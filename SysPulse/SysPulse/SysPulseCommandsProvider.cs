// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using SysPulse.Core.Monitoring;
using SysPulse.Core.Processes;
using SysPulse.Core.Settings;
using SysPulse.Dock;
using SysPulse.Pages;

namespace SysPulse;

public partial class SysPulseCommandsProvider : CommandProvider
{
    private readonly ICommandItem[] _commands;
    private readonly ICommandItem[] _dockBands;

    private readonly TopProcessesPage _topProcessesPage;
    private readonly SysPulseOptions _options;
    private readonly SystemSampler _sampler;
    private readonly ProcessSampler _processSampler;
    private readonly HealthMonitor _healthMonitor;
    private readonly MonitorLoop _monitorLoop;
    private readonly StatusDockItem _statusItem;
    private readonly OnLoadDockBandItem _dockBandItem;
    private readonly MonitorLease _lease = new();

    private ProtectedProcessList _protectedProcessList;
    private bool _disposed;

    public SysPulseCommandsProvider()
    {
        Id = "com.syspulse.extension";
        DisplayName = "SysPulse";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");

        // Settings page comes later; defaults for now, per task instructions.
        _options = new SysPulseOptions();
        _sampler = new SystemSampler();
        _processSampler = new ProcessSampler();
        _protectedProcessList = ProtectedProcessList.Create(_options, Environment.ProcessId);
        _healthMonitor = new HealthMonitor(_options);
        _monitorLoop = new MonitorLoop(_sampler, _healthMonitor, _options);
        _monitorLoop.Sampled += OnSampled;
        _monitorLoop.Faulted += OnFaulted;

        // Created ONCE and kept for the whole lifetime (spec §4.1 / §3 leak-avoidance pitfall):
        // reuse stable instances, never rebuild the band/page item arrays on each tick. The same
        // page instance is used both as the top-level command and as the dock band's click
        // target (task requirement 4), and its Id is non-empty (set in its own constructor),
        // satisfying the dock band Command.Id requirement (spec §3).
        _topProcessesPage = new TopProcessesPage(
            _processSampler,
            _healthMonitor,
            _monitorLoop,
            () => _options,
            () => _protectedProcessList,
            OnBandLoaded,
            OnBandUnloaded);

        _commands = [
            new CommandItem(_topProcessesPage) { Title = "SysPulse – Top processes" },
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
    }

    public override ICommandItem[] TopLevelCommands()
    {
        return _commands;
    }

    public override ICommandItem[]? GetDockBands() => _dockBands;

    /// <summary>
    /// Rebuilds <see cref="_protectedProcessList"/> from the current <see cref="_options"/> (task
    /// requirement 3). Not yet called anywhere -- the settings page (spec §6) will call this after
    /// applying a new <see cref="SysPulseOptions.ProtectedProcesses"/> value; until then the list
    /// only reflects the built-ins plus SysPulse's own process id.
    /// </summary>
    internal void RebuildProtectedProcessList()
    {
        _protectedProcessList = ProtectedProcessList.Create(_options, Environment.ProcessId);
    }

    private void OnSampled(HealthEvaluation evaluation, SystemSnapshot snapshot)
    {
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
        Debug.WriteLine($"SysPulse: monitor loop cycle faulted: {ex}");
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
        }
        catch (Exception ex)
        {
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
        }
        catch (Exception ex)
        {
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
