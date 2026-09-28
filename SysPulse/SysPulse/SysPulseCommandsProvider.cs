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
using SysPulse.Core.Settings;
using SysPulse.Dock;

namespace SysPulse;

public partial class SysPulseCommandsProvider : CommandProvider
{
    private readonly ICommandItem[] _commands;
    private readonly ICommandItem[] _dockBands;

    private readonly SysPulsePage _statusPage;
    private readonly SysPulseOptions _options;
    private readonly SystemSampler _sampler;
    private readonly HealthMonitor _healthMonitor;
    private readonly MonitorLoop _monitorLoop;
    private readonly StatusDockItem _statusItem;
    private readonly OnLoadDockBandItem _dockBandItem;
    private readonly MonitorLease _lease = new();

    private bool _disposed;

    public SysPulseCommandsProvider()
    {
        Id = "com.syspulse.extension";
        DisplayName = "SysPulse";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");

        // Created ONCE and kept for the whole lifetime (spec §4.1 / §3 leak-avoidance pitfall):
        // reuse stable instances, never rebuild the band/page item arrays on each tick.
        _statusPage = new SysPulsePage();
        _commands = [
            new CommandItem(_statusPage) { Title = DisplayName },
        ];

        // Settings page comes later; defaults for now, per task instructions.
        _options = new SysPulseOptions();
        _sampler = new SystemSampler();
        _healthMonitor = new HealthMonitor(_options);
        _monitorLoop = new MonitorLoop(_sampler, _healthMonitor, _options);
        _monitorLoop.Sampled += OnSampled;
        _monitorLoop.Faulted += OnFaulted;

        // Clicking the band item opens _statusPage (the existing template page) in the flyout;
        // the Top-5 page will replace it later. _statusPage.Id is non-empty (set in its own
        // constructor), satisfying the dock band Command.Id requirement (spec §3).
        _statusItem = new StatusDockItem(_statusPage);

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

        GC.SuppressFinalize(this);
        base.Dispose();
    }

    /// <summary>
    /// Thread-safe ref-count gating the monitor loop's start/stop: the Dock status band and
    /// (later) the Top-5 flyout page both call <see cref="Acquire"/>/<see cref="Release"/> when
    /// they load/unload, so the loop runs only while at least one of them is on screen.
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
