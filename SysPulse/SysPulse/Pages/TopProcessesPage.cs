// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using SysPulse.Commands;
using SysPulse.Core.Monitoring;
using SysPulse.Core.Processes;
using SysPulse.Core.Settings;
using SysPulse.Dock;

namespace SysPulse.Pages;

/// <summary>
/// The Top-5 processes flyout (spec §5.5). Replaces the scaffolded <c>SysPulsePage</c> both as the
/// dock band's click target and as the top-level command.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fixed slots:</b> exactly 5 <see cref="ProcessListItem"/> instances are created once in the
/// constructor and updated in place; <see cref="GetItems"/> returns one of 6 pre-built array
/// slices (lengths 0..5 of the same 5 item references) built once, so it never allocates and
/// always returns the same item identities.
/// </para>
/// <para>
/// <b>Lifecycle (DECISIONS.md D3):</b> this page extends the ported <see cref="OnLoadDynamicListPage"/>
/// (same load-gating pattern as the dock band, <see cref="OnLoadDockBandItem"/>) so the monitor
/// loop's lease is acquired/released exactly while the flyout is open. The <paramref name="onLoaded"/>/
/// <paramref name="onUnloaded"/> callbacks are the provider's existing <c>OnBandLoaded</c>/
/// <c>OnBandUnloaded</c>, shared with the dock band so the lease is ref-counted across both.
/// </para>
/// <para>
/// <b>Refresh:</b> <see cref="GetItems"/> never blocks -- it returns the current cached slice and,
/// if the last process sample is older than 2s, kicks off a single-flight background refresh that
/// updates the slots and raises <c>ItemsChanged</c>. While loaded, every
/// <see cref="MonitorLoop.Sampled"/> tick also triggers a refresh (not gated by the 2s staleness
/// check). On load, two priming samples are taken (immediately, then ~1s later) since
/// <see cref="ProcessSampler"/> needs two samples one wall-clock interval apart to compute CPU%.
/// </para>
/// </remarks>
internal sealed partial class TopProcessesPage : OnLoadDynamicListPage
{
    private const int SlotCount = 5;
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PrimingInterval = TimeSpan.FromSeconds(1);

    private readonly IProcessSampler _sampler;
    private readonly HealthMonitor _healthMonitor;
    private readonly MonitorLoop _monitorLoop;
    private readonly Func<SysPulseOptions> _optionsAccessor;
    private readonly Func<ProtectedProcessList> _protectedListAccessor;
    private readonly Action _onLoadedExternal;
    private readonly Action _onUnloadedExternal;

    private readonly ProcessListItem[] _slots;
    private readonly IListItem[][] _slices;

    private int _usedCount;
    private int _refreshing;
    private long _lastSampleUtcTicks;
    private CancellationTokenSource? _primingCts;

    public TopProcessesPage(
        IProcessSampler sampler,
        HealthMonitor healthMonitor,
        MonitorLoop monitorLoop,
        Func<SysPulseOptions> optionsAccessor,
        Func<ProtectedProcessList> protectedListAccessor,
        Action onLoaded,
        Action onUnloaded)
    {
        ArgumentNullException.ThrowIfNull(sampler);
        ArgumentNullException.ThrowIfNull(healthMonitor);
        ArgumentNullException.ThrowIfNull(monitorLoop);
        ArgumentNullException.ThrowIfNull(optionsAccessor);
        ArgumentNullException.ThrowIfNull(protectedListAccessor);
        ArgumentNullException.ThrowIfNull(onLoaded);
        ArgumentNullException.ThrowIfNull(onUnloaded);

        _sampler = sampler;
        _healthMonitor = healthMonitor;
        _monitorLoop = monitorLoop;
        _optionsAccessor = optionsAccessor;
        _protectedListAccessor = protectedListAccessor;
        _onLoadedExternal = onLoaded;
        _onUnloadedExternal = onUnloaded;

        // Non-empty Id required: this page also doubles as the dock band item's Command, and
        // CmdPal ignores dock band items whose Command.Id is empty (spec §3).
        Id = "com.syspulse.topprocesses";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        Title = "Top processes";
        Name = "Open";

        EmptyContent = new CommandItem(new NoOpCommand())
        {
            Title = "No process data yet",
            Subtitle = "Sampling processes…",
        };

        var refreshCommand = new RefreshProcessesCommand(() => StartRefreshIfIdle());
        var refreshContextItem = new CommandContextItem(refreshCommand);

        _slots = new ProcessListItem[SlotCount];
        for (int i = 0; i < SlotCount; i++)
        {
            _slots[i] = new ProcessListItem(_optionsAccessor, () => StartRefreshIfIdle(), refreshContextItem);
        }

        _slices = new IListItem[SlotCount + 1][];
        for (int count = 0; count <= SlotCount; count++)
        {
            var slice = new IListItem[count];
            Array.Copy(_slots, slice, count);
            _slices[count] = slice;
        }
    }

    public override IListItem[] GetItems()
    {
        if (IsStale())
        {
            StartRefreshIfIdle();
        }

        return _slices[Volatile.Read(ref _usedCount)];
    }

    // The Top-5 page has no search box (spec §5.5 always shows the ranked top 5); nothing to do.
    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
    }

    protected override void Loaded()
    {
        try
        {
            _onLoadedExternal();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SysPulse: TopProcessesPage onLoaded callback failed: {ex}");
        }

        _monitorLoop.Sampled += OnMonitorSampled;

        var cts = new CancellationTokenSource();
        _primingCts = cts;
        _ = Task.Run(() => RunPrimingSequenceAsync(cts.Token), cts.Token);
    }

    protected override void Unloaded()
    {
        _monitorLoop.Sampled -= OnMonitorSampled;

        CancellationTokenSource? cts = _primingCts;
        _primingCts = null;
        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            cts.Dispose();
        }

        try
        {
            _onUnloadedExternal();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SysPulse: TopProcessesPage onUnloaded callback failed: {ex}");
        }
    }

    private void OnMonitorSampled(HealthEvaluation evaluation, SystemSnapshot snapshot) => StartRefreshIfIdle();

    private async Task RunPrimingSequenceAsync(CancellationToken token)
    {
        // ProcessSampler needs two samples one interval apart to compute CPU% (spec §5.5):
        // an immediate priming sample, then a second ~1s later.
        StartRefreshIfIdle();

        try
        {
            await Task.Delay(PrimingInterval, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!token.IsCancellationRequested)
        {
            StartRefreshIfIdle();
        }
    }

    private bool IsStale()
    {
        long lastTicks = Interlocked.Read(ref _lastSampleUtcTicks);
        if (lastTicks == 0)
        {
            return true;
        }

        var age = DateTimeOffset.UtcNow - new DateTimeOffset(lastTicks, TimeSpan.Zero);
        return age >= StaleThreshold;
    }

    private void StartRefreshIfIdle()
    {
        // Single-flight: if a refresh is already in progress, this tick's request is dropped --
        // the next GetItems()/Sampled tick will try again, and no samples ever overlap.
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(RunRefresh);
    }

    private void RunRefresh()
    {
        try
        {
            IReadOnlyList<ProcessSample> samples = _sampler.Sample();
            ApplySamples(samples);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SysPulse: Top-5 process refresh failed: {ex}");
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private void ApplySamples(IReadOnlyList<ProcessSample> samples)
    {
        SysPulseOptions options = _optionsAccessor();
        HealthState state = _healthMonitor.State;
        BreachKind breachKind = _healthMonitor.BreachKind;
        ulong totalPhysBytes = _healthMonitor.LastSnapshot.TotalPhysBytes;
        ProtectedProcessList protectedList = _protectedListAccessor();

        IReadOnlyList<ProcessSample> top =
            ProcessRanker.Top(samples, breachKind, state, options, totalPhysBytes, SlotCount);

        for (int i = 0; i < top.Count; i++)
        {
            ProcessSample sample = top[i];
            bool isProtected = protectedList.IsProtected(sample.Name, sample.Pid);
            _slots[i].Update(sample, isProtected);
        }

        Interlocked.Exchange(ref _lastSampleUtcTicks, DateTimeOffset.UtcNow.Ticks);
        Volatile.Write(ref _usedCount, top.Count);

        RaiseItemsChanged(top.Count);
    }
}
