using SysPulse.Core.Settings;

namespace SysPulse.Core.Monitoring;

/// <summary>
/// The result of evaluating one <see cref="SystemSnapshot"/> against the current
/// <see cref="HealthMonitor"/> state.
/// </summary>
/// <param name="State">The state after evaluating this snapshot.</param>
/// <param name="BreachKind">The metric(s) responsible for the current state (see <see cref="Monitoring.BreachKind"/>).</param>
/// <param name="EnteredAlert">True exactly on the transition into <see cref="HealthState.Alert"/>.</param>
/// <param name="LeftAlert">True exactly on the transition out of <see cref="HealthState.Alert"/> back to <see cref="HealthState.Normal"/>.</param>
/// <param name="NextDelay">The delay to wait before the next sample (the scan or retry cadence).</param>
public readonly record struct HealthEvaluation(HealthState State, BreachKind BreachKind, bool EnteredAlert, bool LeftAlert, TimeSpan NextDelay);

/// <summary>
/// Pure state machine implementing the SysPulse breach-verification and alert-hysteresis rules
/// (spec §5.2). Contains no timers, threads, or I/O — a driver (see <see cref="MonitorLoop"/>)
/// feeds it snapshots and acts on <see cref="HealthEvaluation.NextDelay"/>.
/// </summary>
/// <remarks>
/// <para>
/// Rules: a breach is <c>(CPU monitoring enabled and CPU% ≥ CpuThreshold) or (memory monitoring
/// enabled and memory% ≥ MemoryThreshold)</c>. From <see cref="HealthState.Normal"/>, a breach
/// moves to <see cref="HealthState.Verifying"/> and switches cadence to the retry interval. Each
/// subsequent sample while <see cref="HealthState.Verifying"/> is a retry: a non-breaching retry
/// returns to <see cref="HealthState.Normal"/>; a breaching retry increments the attempt counter,
/// and once the counter reaches <c>RetryCount</c> the monitor enters <see cref="HealthState.Alert"/>
/// (cadence reverts to the scan interval). The <see cref="Monitoring.BreachKind"/> recorded for the
/// alert is the union of every metric that breached across the triggering sample and all retries.
/// While in <see cref="HealthState.Alert"/>, <see cref="BreachKind"/> tracks whichever metric(s)
/// are currently breaching (and is left unchanged on a sample where nothing breaches, so the UI
/// keeps showing the last known culprit). The monitor leaves <see cref="HealthState.Alert"/> only
/// once every enabled metric is below <c>threshold − HysteresisPercent</c> for one full scan.
/// </para>
/// <para>
/// Thread-safety: this type is not thread-safe. The caller (typically a single-threaded driver
/// loop) must serialize all calls to <see cref="Evaluate"/> and <see cref="UpdateOptions"/> —
/// no internal locking is performed.
/// </para>
/// </remarks>
public sealed class HealthMonitor
{
    private SysPulseOptions _options;
    private int _verifyAttempt;
    private BreachKind _verifyBreachUnion = BreachKind.None;

    /// <summary>
    /// Initializes a new instance of the <see cref="HealthMonitor"/> class, starting in
    /// <see cref="HealthState.Normal"/>.
    /// </summary>
    /// <param name="options">The initial options. Clamped via <see cref="SysPulseOptions.Normalize"/>.</param>
    public HealthMonitor(SysPulseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Normalize();
    }

    /// <summary>The current health state.</summary>
    public HealthState State { get; private set; } = HealthState.Normal;

    /// <summary>The metric(s) responsible for the current state; <see cref="Monitoring.BreachKind.None"/> in <see cref="HealthState.Normal"/>.</summary>
    public BreachKind BreachKind { get; private set; } = BreachKind.None;

    /// <summary>The most recent snapshot passed to <see cref="Evaluate"/>, or the default value if none yet.</summary>
    public SystemSnapshot LastSnapshot { get; private set; }

    /// <summary>
    /// Applies new options, taking effect on the next <see cref="Evaluate"/> call.
    /// </summary>
    /// <remarks>
    /// Design choice: if the monitor is currently <see cref="HealthState.Verifying"/>, the
    /// in-progress verification is <em>not</em> restarted — the attempt counter and the
    /// accumulated <see cref="Monitoring.BreachKind"/> union are kept as-is, and the next sample
    /// is simply evaluated against the new thresholds/cadence. This avoids letting frequent
    /// settings edits indefinitely postpone an in-flight verification, while still applying the
    /// new limits immediately (including a shortened <c>RetryCount</c> potentially completing
    /// verification on the very next sample).
    /// </remarks>
    /// <param name="options">The new options. Clamped via <see cref="SysPulseOptions.Normalize"/>.</param>
    public void UpdateOptions(SysPulseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Normalize();
    }

    /// <summary>
    /// Evaluates one new snapshot, advancing the state machine and returning the outcome.
    /// </summary>
    /// <param name="snapshot">The snapshot to evaluate.</param>
    /// <returns>The resulting state, breach kind, alert-edge flags, and next sampling delay.</returns>
    public HealthEvaluation Evaluate(SystemSnapshot snapshot)
    {
        LastSnapshot = snapshot;
        BreachKind currentBreach = ComputeBreach(snapshot, _options);
        bool isBreaching = currentBreach != BreachKind.None;

        HealthEvaluation result = State switch
        {
            HealthState.Normal => EvaluateFromNormal(currentBreach, isBreaching),
            HealthState.Verifying => EvaluateFromVerifying(currentBreach, isBreaching),
            HealthState.Alert => EvaluateFromAlert(snapshot, currentBreach, isBreaching),
            _ => throw new InvalidOperationException($"Unreachable HealthState: {State}"),
        };

        State = result.State;
        BreachKind = result.BreachKind;
        return result;
    }

    private HealthEvaluation EvaluateFromNormal(BreachKind currentBreach, bool isBreaching)
    {
        if (!isBreaching)
        {
            return new HealthEvaluation(HealthState.Normal, BreachKind.None, false, false, ScanDelay);
        }

        _verifyAttempt = 0;
        _verifyBreachUnion = currentBreach;
        return new HealthEvaluation(HealthState.Verifying, currentBreach, false, false, RetryDelay);
    }

    private HealthEvaluation EvaluateFromVerifying(BreachKind currentBreach, bool isBreaching)
    {
        if (!isBreaching)
        {
            _verifyAttempt = 0;
            _verifyBreachUnion = BreachKind.None;
            return new HealthEvaluation(HealthState.Normal, BreachKind.None, false, false, ScanDelay);
        }

        _verifyBreachUnion = Union(_verifyBreachUnion, currentBreach);
        _verifyAttempt++;

        if (_verifyAttempt >= _options.RetryCount)
        {
            BreachKind alertBreach = _verifyBreachUnion;
            _verifyAttempt = 0;
            _verifyBreachUnion = BreachKind.None;
            return new HealthEvaluation(HealthState.Alert, alertBreach, true, false, ScanDelay);
        }

        return new HealthEvaluation(HealthState.Verifying, _verifyBreachUnion, false, false, RetryDelay);
    }

    private HealthEvaluation EvaluateFromAlert(SystemSnapshot snapshot, BreachKind currentBreach, bool isBreaching)
    {
        BreachKind displayedBreach = isBreaching ? currentBreach : BreachKind;

        if (IsRecovered(snapshot, _options))
        {
            return new HealthEvaluation(HealthState.Normal, BreachKind.None, false, true, ScanDelay);
        }

        return new HealthEvaluation(HealthState.Alert, displayedBreach, false, false, ScanDelay);
    }

    private TimeSpan ScanDelay => TimeSpan.FromSeconds(_options.ScanIntervalSeconds);

    private TimeSpan RetryDelay => TimeSpan.FromSeconds(_options.RetryIntervalSeconds);

    /// <summary>Pure breach test, kept separately testable from the rest of the state machine.</summary>
    /// <param name="snapshot">The snapshot to test.</param>
    /// <param name="options">The thresholds/enable flags to test against.</param>
    /// <returns>Which metric(s), if any, are at or above their threshold.</returns>
    internal static BreachKind ComputeBreach(SystemSnapshot snapshot, SysPulseOptions options)
    {
        bool cpuBreach = options.CpuMonitoringEnabled && snapshot.CpuPercent >= options.CpuThreshold;
        bool memBreach = options.MemoryMonitoringEnabled && snapshot.MemoryPercent >= options.MemoryThreshold;

        if (cpuBreach && memBreach)
        {
            return BreachKind.Both;
        }

        if (cpuBreach)
        {
            return BreachKind.Cpu;
        }

        return memBreach ? BreachKind.Memory : BreachKind.None;
    }

    /// <summary>Pure recovery test (alert exit), kept separately testable from the rest of the state machine.</summary>
    /// <param name="snapshot">The snapshot to test.</param>
    /// <param name="options">The thresholds/hysteresis/enable flags to test against.</param>
    /// <returns>True if every enabled metric is below <c>threshold − HysteresisPercent</c>.</returns>
    internal static bool IsRecovered(SystemSnapshot snapshot, SysPulseOptions options)
    {
        bool cpuOk = !options.CpuMonitoringEnabled || snapshot.CpuPercent < options.CpuThreshold - options.HysteresisPercent;
        bool memOk = !options.MemoryMonitoringEnabled || snapshot.MemoryPercent < options.MemoryThreshold - options.HysteresisPercent;
        return cpuOk && memOk;
    }

    private static BreachKind Union(BreachKind a, BreachKind b)
    {
        if (a == BreachKind.None)
        {
            return b;
        }

        if (b == BreachKind.None || a == b)
        {
            return a;
        }

        return BreachKind.Both;
    }
}
