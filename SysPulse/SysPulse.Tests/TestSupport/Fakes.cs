using SysPulse.Core.Monitoring;

namespace SysPulse.Tests.TestSupport;

/// <summary>An <see cref="ISystemSampler"/> that replays a fixed, scripted sequence of snapshots.</summary>
internal sealed class ScriptedSampler : ISystemSampler
{
    private readonly Queue<SystemSnapshot> _snapshots;

    public ScriptedSampler(IEnumerable<SystemSnapshot> snapshots) => _snapshots = new Queue<SystemSnapshot>(snapshots);

    public int RemainingCount => _snapshots.Count;

    public SystemSnapshot Sample()
    {
        if (_snapshots.Count == 0)
        {
            throw new InvalidOperationException("ScriptedSampler has no more scripted snapshots.");
        }

        return _snapshots.Dequeue();
    }
}

/// <summary>An <see cref="IAlertNotifier"/> that records every call for assertions.</summary>
internal sealed class RecordingNotifier : IAlertNotifier
{
    public List<(SystemSnapshot Snapshot, BreachKind BreachKind)> Calls { get; } = [];

    public void NotifyAlert(SystemSnapshot snapshot, BreachKind breachKind) => Calls.Add((snapshot, breachKind));
}
