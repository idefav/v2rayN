using ServiceLib.Models.Dto;

namespace v2rayN.Desktop.Common;

/// <summary>One operation, one notice, one monotonic countdown. No window or core operations.</summary>
internal sealed class ConnectionFeedbackState(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _lastTick = (clock ?? TimeProvider.System).GetTimestamp();
    private TimeSpan _elapsed;
    private TimeSpan _remaining;
    private bool _dismissed, _paused, _reopened;
    internal ConnectionTransition? Current { get; private set; }
    internal bool Visible { get; private set; }
    internal bool HadHandoff { get; private set; }
    internal bool CancelRequested { get; private set; }
    internal bool CanCancel => Current is { IsTerminal: false, Disconnecting: false, UpdatingProxy: false } && !CancelRequested;

    internal bool Update(ConnectionTransition state)
    {
        Tick();
        if (Current is { } old && state.OperationId > 0 && old.OperationId > 0
            && (state.OperationId < old.OperationId || state.OperationId == old.OperationId && old.IsTerminal)) return false;
        if (state == Current) return false;
        var fresh = Current == null || state.OperationId != Current.OperationId
            || state.OperationId == 0 && Current.IsTerminal && !state.IsTerminal;
        if (fresh)
        {
            Visible = false; _elapsed = TimeSpan.Zero; _dismissed = state.IsTerminal;
            _reopened = false;
            HadHandoff = CancelRequested = false;
        }
        var firstTerminal = state.IsTerminal && (fresh || Current?.IsTerminal != true);
        Current = state;
        HadHandoff |= state.Stage == ConnectionStage.Waiting;
        if (firstTerminal)
        {
            _remaining = TimeSpan.FromSeconds(state.Stage == ConnectionStage.Completed ? 3 : state.Stage == ConnectionStage.Cancelled ? 4 : 8);
            Visible = !_dismissed && (!state.IsBackground || _reopened);
        }
        Tick();
        return true;
    }

    internal void Tick()
    {
        var now = _clock.GetTimestamp();
        var delta = _clock.GetElapsedTime(_lastTick, now); _lastTick = now;
        if (Current == null) return;
        _elapsed += delta;
        if (!Current.IsTerminal)
        {
            Visible = !_dismissed && (!Current.IsBackground || _reopened) && _elapsed >= TimeSpan.FromMilliseconds(400);
            return;
        }
        if (Visible && !_paused)
        {
            _remaining -= delta;
            if (_remaining <= TimeSpan.Zero) Dismiss();
        }
    }

    internal void SetPaused(bool paused) { Tick(); _paused = paused; }
    internal void Dismiss() { _dismissed = true; Visible = false; }
    internal void Reopen()
    {
        if (Current == null) return;
        _lastTick = _clock.GetTimestamp(); _dismissed = false; _reopened = true; Visible = true;
        _elapsed = TimeSpan.FromMilliseconds(400);
        _remaining = TimeSpan.FromSeconds(Current.Stage == ConnectionStage.Completed ? 3 : Current.Stage == ConnectionStage.Cancelled ? 4 : 8);
    }
    internal bool RequestCancellation()
    {
        if (!CanCancel) return false;
        CancelRequested = true;
        return true;
    }
}
