using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Media;
using System.Windows.Media;
using System.Windows.Threading;

namespace Reacher;

/// <summary>
/// Owns every reach: queues them while the user is away, shows bubbles when they can be seen,
/// drives the per-frame animation, and resolves callers waiting on a response.
/// </summary>
internal sealed class Coordinator
{
    private const int HistoryLimit = 200;

    private readonly Dispatcher _ui;
    private readonly object _gate = new();
    private readonly List<ReachItem> _history = [];
    private readonly List<ReachItem> _pending = [];
    private readonly List<BubbleWindow> _bubbles = [];
    private readonly DispatcherTimer _presenceTimer;
    private PresenceState _presenceState;
    private Native.RECT _dockWork;
    private bool _rendering;
    private TimeSpan? _lastFrame;
    private bool _doNotDisturb;

    public Presence Presence { get; } = new();

    /// <summary>Raised on the UI thread whenever queue or visible bubbles change.</summary>
    public event Action? Changed;

    public Coordinator(Dispatcher ui)
    {
        _ui = ui;
        _presenceState = Presence.State;
        _presenceTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => OnPresenceTick(), ui);
        _presenceTimer.Start();
    }

    public bool DoNotDisturb
    {
        get => _doNotDisturb;
        set { _doNotDisturb = value; _ui.BeginInvoke(Pump); }
    }

    public int PendingCount { get { lock (_gate) return _pending.Count; } }

    // ---- thread-safe API used by the HTTP server and tray ----

    public ReachItem Submit(ReachRequest request)
    {
        var item = ReachItem.From(request);
        lock (_gate)
        {
            _history.Add(item);
            if (_history.Count > HistoryLimit) _history.RemoveAt(0);
            _pending.Add(item);
        }
        // Synchronous so the caller's response already says whether it showed or was queued (and why).
        _ui.Invoke(Pump);
        return item;
    }

    public ReachItem? Find(string id)
    {
        lock (_gate) return _history.FirstOrDefault(i => i.Id == id);
    }

    public IReadOnlyList<ReachItem> Recent(int count)
    {
        lock (_gate) return _history.AsEnumerable().Reverse().Take(count).ToList();
    }

    public bool Cancel(string id)
    {
        var item = Find(id);
        if (item is null || item.IsFinal) return false;
        _ui.Invoke(() => Finish(item, ReachStatus.Cancelled, null));
        return true;
    }

    public void DismissAll() => _ui.BeginInvoke(() =>
    {
        foreach (var b in _bubbles.ToList()) Finish(b.Item, ReachStatus.Dismissed, null);
    });

    // ---- UI thread ----

    private void OnPresenceTick()
    {
        _presenceState = Presence.State;
        bool hasPending;
        lock (_gate) hasPending = _pending.Count > 0;
        if (hasPending) Pump();
    }

    private string? HoldReason(ReachItem item)
    {
        switch (_presenceState)
        {
            case PresenceState.Locked: return "locked";
            case PresenceState.Idle: return "away";
        }
        if (item.Priority == Priority.Urgent) return null;
        if (_doNotDisturb) return "do-not-disturb";
        if (_presenceState == PresenceState.Fullscreen) return "fullscreen";
        return null;
    }

    private void Pump()
    {
        _presenceState = Presence.State;
        var ready = new List<ReachItem>();
        lock (_gate)
        {
            foreach (var item in _pending.ToList())
            {
                if (item.IsFinal) { _pending.Remove(item); continue; }
                var reason = HoldReason(item);
                if (reason is null) { ready.Add(item); _pending.Remove(item); }
                else item.QueuedReason = reason;
            }
        }
        if (ready.Count > 0)
        {
            // When several land at once (e.g. the user just came back), only the most important, newest one chases.
            var chaser = ready.OrderBy(i => i.Priority).ThenBy(i => i.CreatedAt).Last();
            foreach (var item in ready) Show(item, chase: item == chaser);
        }
        Changed?.Invoke();
    }

    private void Show(ReachItem item, bool chase)
    {
        item.Status = ReachStatus.Showing;
        item.ShownAt = DateTimeOffset.Now;
        item.QueuedReason = null;

        var willChase = chase && item.ChaseSeconds > 0;
        if (willChase)
            foreach (var other in _bubbles.Where(b => b.Phase == BubblePhase.Chasing)) DockBubble(other);

        var bubble = new BubbleWindow(item);
        bubble.UserResponded += (b, status, response) => Finish(b.Item, status, response);
        bubble.Closed += (_, _) => { _bubbles.Remove(bubble); Changed?.Invoke(); };
        _bubbles.Add(bubble);
        if (willChase) bubble.StartChase(item.ChaseSeconds);
        else DockBubble(bubble);
        bubble.Show();

        if (item.Sound)
            (item.Priority == Priority.Urgent ? SystemSounds.Exclamation : SystemSounds.Asterisk).Play();

        EnsureRendering();
    }

    private void DockBubble(BubbleWindow bubble)
    {
        _dockWork = Native.WorkAreaAt(Native.CursorPos());
        bubble.Dock();
    }

    private void Finish(ReachItem item, ReachStatus status, string? response)
    {
        if (item.IsFinal) return;
        item.Status = status;
        item.Response = response;
        item.ClosedAt = DateTimeOffset.Now;
        lock (_gate) _pending.Remove(item);

        if (status == ReachStatus.Acknowledged && response is null && item.Open is { } target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write($"open '{target}' failed: {ex.Message}"); }
        }

        _bubbles.FirstOrDefault(b => b.Item == item)?.FadeOut();
        Log.Write($"{item.Id} {status.ToString().ToLowerInvariant()}{(response is null ? "" : $" '{response}'")}");
        item.Done.TrySetResult();
        Changed?.Invoke();
    }

    private void EnsureRendering()
    {
        if (_rendering) return;
        _rendering = true;
        _lastFrame = null;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_lastFrame == now) return; // Rendering can fire more than once per frame
        var dt = _lastFrame is { } last ? Math.Clamp((now - last).TotalSeconds, 0, 0.1) : 1 / 60.0;
        _lastFrame = now;

        if (_bubbles.Count == 0)
        {
            CompositionTarget.Rendering -= OnFrame;
            _rendering = false;
            return;
        }

        var present = _presenceState is PresenceState.Active or PresenceState.Fullscreen;
        foreach (var bubble in _bubbles.ToList())
        {
            switch (bubble.Advance(dt, present))
            {
                case BubbleSignal.ChaseEnded: DockBubble(bubble); break;
                case BubbleSignal.Expired: Finish(bubble.Item, ReachStatus.Expired, null); break;
            }
        }

        var cursor = Native.CursorPos();
        var cursorWork = Native.WorkAreaAt(cursor);
        foreach (var bubble in _bubbles.Where(b => b.Phase == BubblePhase.Chasing))
            bubble.ChaseToward(cursor, cursorWork, dt);

        // Docked bubbles stack upward from the bottom-right corner, most recently docked at the bottom.
        var scale = _bubbles.Count > 0 ? VisualTreeHelper.GetDpi(_bubbles[0]).DpiScaleX : 1;
        double right = _dockWork.Right - 14 * scale, bottom = _dockWork.Bottom - 14 * scale;
        foreach (var bubble in _bubbles.Where(b => b.Phase == BubblePhase.Docked).OrderByDescending(b => b.DockedAt))
        {
            bubble.DockToward(right, bottom, dt);
            bottom -= bubble.CardHeight + 10 * scale;
        }
    }
}
