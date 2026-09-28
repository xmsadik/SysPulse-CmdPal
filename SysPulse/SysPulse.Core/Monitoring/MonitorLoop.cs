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

            _cts = new CancellationTokenSource();
            _timer = new PeriodicTimer(firstTickDelay ?? CadenceFor(_monitor.State), _timeProvider);
            _loopTask = RunAsync(_timer, _cts.Token);
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

    private async Task RunAsync(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                bool showToast;
                try
                {
                    SystemSnapshot snapshot = _sampler.Sample();
                    HealthEvaluation evaluation;

                    // HealthMonitor is not thread-safe; UpdateOptions mutates it under the same gate.
                    lock (_gate)
                    {
                        evaluation = _monitor.Evaluate(snapshot);
                        showToast = _options.ShowToast;
                        if (_timer is not null)
                        {
                            _timer.Period = evaluation.NextDelay;
                        }
                    }

                    if (evaluation.EnteredAlert && showToast)
                    {
                        _notifier?.NotifyAlert(snapshot, evaluation.BreachKind);
                    }

                    Sampled?.Invoke(evaluation, snapshot);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Faulted?.Invoke(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }
}
