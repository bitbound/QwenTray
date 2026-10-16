namespace QwenTray;

public enum DaemonState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Crashed
}

internal readonly record struct DaemonCommandAvailability(bool CanStart, bool CanStop, bool CanRestart)
{
    public static DaemonCommandAvailability From(DaemonState state, bool busy = false) => busy
        ? new(false, false, false)
        : state switch
        {
            DaemonState.Stopped => new(true, false, false),
            DaemonState.Starting => new(false, true, false),
            DaemonState.Running => new(false, true, true),
            DaemonState.Stopping => new(false, false, false),
            DaemonState.Crashed => new(false, false, true),
            _ => new(false, false, false),
        };
}

public sealed class DaemonStateMachine
{
    public DaemonState State { get; private set; } = DaemonState.Stopped;
    public int ConsecutiveFailures { get; private set; }

    public void BeginStart() => State = DaemonState.Starting;
    public void BeginStop() => State = DaemonState.Stopping;
    public void Adopt() { State = DaemonState.Running; ConsecutiveFailures = 0; }
    public void HealthSucceeded() { State = DaemonState.Running; ConsecutiveFailures = 0; }
    public void HealthFailed() { if (State is DaemonState.Starting or DaemonState.Running) State = DaemonState.Starting; }
    public void UnexpectedExit() { State = DaemonState.Crashed; ConsecutiveFailures++; }
    public void UserStopped() { State = DaemonState.Stopped; ConsecutiveFailures = 0; }

    public int BackoffSeconds(int maximum)
    {
        var exponent = Math.Clamp(ConsecutiveFailures - 1, 0, 30);
        return Math.Min(maximum, (int)Math.Min(maximum, Math.Pow(2, exponent)));
    }
}
