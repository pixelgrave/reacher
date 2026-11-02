using System;
using Microsoft.Win32;

namespace Reacher;

public enum PresenceState { Active, Idle, Locked, Fullscreen }

/// <summary>Works out whether the user is at the machine and able to see a bubble.</summary>
public sealed class Presence
{
    public TimeSpan IdleThreshold { get; set; } = TimeSpan.FromSeconds(
        double.TryParse(Environment.GetEnvironmentVariable("REACHER_IDLE_SECONDS"), out var s) && s > 0 ? s : 60);

    private volatile bool _locked;

    public Presence()
    {
        SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
                _locked = true;
            else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
                _locked = false;
        };
    }

    public double IdleSeconds => Native.IdleMilliseconds() / 1000.0;

    public PresenceState State
    {
        get
        {
            if (_locked) return PresenceState.Locked;
            if (IdleSeconds >= IdleThreshold.TotalSeconds) return PresenceState.Idle;
            if (Native.SHQueryUserNotificationState(out var q) == 0 &&
                q is Native.QUNS_RUNNING_D3D_FULL_SCREEN or Native.QUNS_PRESENTATION_MODE)
                return PresenceState.Fullscreen;
            return PresenceState.Active;
        }
    }

    public object ToDto() => new
    {
        state = State.ToString().ToLowerInvariant(),
        idleSeconds = Math.Round(IdleSeconds),
    };
}
