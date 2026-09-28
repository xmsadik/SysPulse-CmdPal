namespace SysPulse.Core.Monitoring;

/// <summary>
/// Receives a notification when <see cref="MonitorLoop"/> enters <see cref="HealthState.Alert"/>.
/// Invoked at most once per alert episode (not once per sample while the alert continues).
/// </summary>
public interface IAlertNotifier
{
    /// <summary>
    /// Called exactly once when the monitor transitions into <see cref="HealthState.Alert"/>.
    /// </summary>
    /// <param name="snapshot">The snapshot that confirmed the alert.</param>
    /// <param name="breachKind">The metric(s) responsible for the alert.</param>
    void NotifyAlert(SystemSnapshot snapshot, BreachKind breachKind);
}
