using SysPulse.Core.Settings;

namespace SysPulse.Core.Monitoring;

/// <summary>
/// Async driver that repeatedly samples system metrics with <see cref="ISystemSampler"/>, feeds
/// them through a <see cref="Monitoring.HealthMonitor"/>, and adjusts its own sampling cadence
/// (scan vs. retry interval) from the resulting <see cref="HealthEvaluation.NextDelay"/>.
/// </summary>
/// <remarks>
/// Runs a single non-overlapping loop on a <see cref="PeriodicTimer"/> built from the injected
/// <see cref="TimeProvider"/>, so tests can drive it deterministically with a fake time provider.
/// Each cycle is wrapped in its own try/catch: a sampling or evaluation failure is reported via
/// <see cref="Faulted"/> and the loop continues on the next tick. Not thread-safe beyond what is
/// documented per member; <see cref="UpdateOptions"/> and <see cref="RequestImmediateSample"/> are
/// safe to call from another thread while the loop is running.
/// </remarks>
public sealed class MonitorLoop : IAsyncDisposable, IDisposable
{
    private readonly ISystemSampler _sampler;
    private readonly HealthMonitor _monitor;
    private readonly TimeProvider _timeProvider;
    private readonly IAlertNotifier? _notifier;
    private readonly Lock _gate = new();

    private SysPulseOptions _options;
    private PeriodicTimer? _timer;
    private Task? _loopTask;
    private CancellationTokenSource? _cts;

    // Code-review finding 4: the task representing the *previous* Start/StopAsync cycle's loop,
    // kept around (even after StopAsync clears the fields above) so a subsequent Start -- called
    // before that StopAsync's caller ever awaits it -- can wait for the old loop to fully drain
    // before its own first tick. Without this, an unawaited StopAsync followed immediately by
    // Start could run two RunAsync loops concurrently, both touching the shared sampler/monitor.
    private Task _previousLoopTask = Task.CompletedTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="MonitorLoop"/> class. Does not start sampling;
    /// call <see cref="Start"/>.
    /// </summary>
    /// <param name="sampler">The system metrics sampler.</param>
    /// <param name="monitor">The state machine to feed each sample through.</param>
    /// <param name="options">The initial options (scan/retry cadence, ShowToast, etc.).</param>
    /// <param name="timeProvider">The time source for the internal timer. Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="notifier">Optional alert notifier, invoked once per alert episode when <see cref="SysPulseOptions.ShowToast"/> is true.</param>
    public MonitorLoop(ISystemSampler sampler, HealthMonitor monitor, SysPulseOptions options, TimeProvider? timeProvider = null, IAlertNotifier? notifier = null)
    {
        ArgumentNullException.ThrowIfNull(sampler);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(options);

        _sampler = sampler;
        _monitor = monitor;
        _options = options.Normalize();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _notifier = notifier;
    }

    /// <summary>Raised after every completed sampling cycle with the evaluation outcome and the snapshot that produced it.</summary>
    public event Action<HealthEvaluation, SystemSnapshot>? Sampled;

    /// <summary>Raised when an exception escapes a sampling cycle. The loop continues on the next tick regardless.</summary>
    public event Action<Exception>? Faulted;

    /// <summary>Starts the sampling loop. Not valid to call more than once without an intervening <see cref="StopAsync"/>.</summary>
    /// <param name="firstTickDelay">
    /// Delay before the first sample (e.g. a short delay so the UI shows values right after the
    /// band appears). Defaults to the cadence of the current state. After the first tick the
    /// regular scan/retry cadence applies.
    /// </param>
    public void Start(TimeSpan? firstTickDelay = null)
    {
        lock (_gate)
        {
            if (_loopTask is not null)
            {
                throw new InvalidOperationException("MonitorLoop is already started.");
            }

            Task previousLoopTask = _previousLoopTask;
            _previousLoopTask = Task.CompletedTask;

            _cts = new CancellationTokenSource();
            _timer = new PeriodicTimer(firstTickDelay ?? CadenceFor(_monitor.State), _timeProvider);
            _loopTask = RunAsync(previousLoopTask, _timer, _cts.Token);
        }
    }

    /// <summary>Stops the sampling loop and waits for the in-flight cycle (if any) to finish.</summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        PeriodicTimer? timer;
        Task? loopTask;

        lock (_gate)
        {
            cts = _cts;
            timer = _timer;
            loopTask = _loopTask;
            _cts = null;
            _timer = null;
            _loopTask = null;

            if (loopTask is not null)
            {
                // Published so a Start() called before this method's own caller awaits it can
                // still wait for this loop to fully drain (see _previousLoopTask's remarks).
                _previousLoopTask = loopTask;
            }
        }

        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        timer?.Dispose();

        if (loopTask is not null)
        {
            try
            {
                await loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        cts.Dispose();
    }

    /// <summary>
    /// Applies new options immediately: the state machine picks them up on its next evaluation,
    /// and the timer's <see cref="PeriodicTimer.Period"/> is updated right away to match the
    /// cadence (scan or retry interval) appropriate for the current state.
    /// </summary>
    /// <param name="options">The new options.</param>
    public void UpdateOptions(SysPulseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        SysPulseOptions normalized = options.Normalize();

        lock (_gate)
        {
            _options = normalized;
            _monitor.UpdateOptions(normalized);
            if (_timer is not null)
            {
                _timer.Period = CadenceFor(_monitor.State);
            }
        }
    }

    /// <summary>
    /// Best-effort nudge to sample sooner than the current cadence. The next regular cycle
    /// re-establishes the normal scan/retry period from the evaluation result, so this only
    /// shifts the timing of the very next tick.
    /// </summary>
    public void RequestImmediateSample()
    {
        lock (_gate)
        {
            if (_timer is not null)
            {
                _timer.Period = TimeSpan.FromMilliseconds(1);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private TimeSpan CadenceFor(HealthState state) => state == HealthState.Verifying
        ? TimeSpan.FromSeconds(_options.RetryIntervalSeconds)
        : TimeSpan.FromSeconds(_options.ScanIntervalSeconds);

    private async Task RunAsync(Task previousLoopTask, PeriodicTimer timer, CancellationToken token)
    {
        // Never run concurrently with the loop this one is replacing (code-review finding 4): if
        // Start is called again before a prior StopAsync's caller awaits it, drain that old loop
        // first. Its own Faulted/Sampled handling already reported whatever happened, so any
        // exception here (including OperationCanceledException from the cancellation that ended
        // it) is deliberately swallowed -- this is purely a hand-off point, not a place to surface
        // errors a second time.
        try
        {
            await previousLoopTask.ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                bool showToast = false;
                bool proceed;
                HealthEvaluation evaluation = default;
                try
                {
                    SystemSnapshot snapshot = _sampler.Sample();

                    // HealthMonitor is not thread-safe; UpdateOptions mutates it under the same
                    // gate. Re-check cancellation inside the gate: StopAsync may have run (and
                    // cleared/canceled) between the WaitForNextTickAsync above and here.
                    lock (_gate)
                    {
                        proceed = !token.IsCancellationRequested;
                        if (proceed)
                        {
                            evaluation = _monitor.Evaluate(snapshot);
                            showToast = _options.ShowToast;

                            // Use the local `timer` (this loop's own instance), never the `_timer`
                            // field: after a Stop/Start race the field may already point at a
                            // different loop's timer, and writing to it here would corrupt that
                            // other loop's cadence.
                            try
                            {
                                timer.Period = evaluation.NextDelay;
                            }
                            catch (ObjectDisposedException)
                            {
                                // A concurrent StopAsync disposed this loop's timer between the
                                // cancellation check above and here; the next
                                // WaitForNextTickAsync call will observe the cancellation and end
                                // the loop.
                            }
                        }
                    }

                    if (!proceed)
                    {
                        continue;
                    }

                    if (evaluation.EnteredAlert && showToast)
                    {
                        _notifier?.NotifyAlert(snapshot, evaluation.BreachKind);
                    }

                    Sampled?.Invoke(evaluation, snapshot);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    try
                    {
                        // Finding 8a: a throwing Faulted handler must not kill the loop.
                        Faulted?.Invoke(ex);
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }
}
