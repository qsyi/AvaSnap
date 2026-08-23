namespace AvaSnap;

/// <summary>Snapshot of the overlay properties that are worth undoing (position,
/// size, rotation, opacity, PNG look adjustments). Click-through is
/// intentionally excluded -- it's a mode toggle, not an edit. The loaded
/// image's path is ALSO excluded: OverlayWindow only ever reloads the actual
/// pixel buffer in response to an explicit LoadImage() call, not to
/// _state.ImagePath changing, so having Undo/Redo silently rewrite that path
/// (without the displayed image actually reloading to match) desynced it from
/// whatever was really on screen -- and that wrong path is what got persisted
/// to settings.json at exit, restoring the wrong PNG on the next launch.</summary>
public sealed record OverlaySnapshot(
    double X, double Y, double Width, double Height, double RotationDegrees, double Opacity,
    double EdgeBlurRadius, double Brightness, double Contrast, double Saturation,
    double Vibrance, double Temperature, double Tint, double Hue,
    double Highlights, double Shadows, double Whites, double Blacks,
    double ColorTintStrength, byte ColorTintR, byte ColorTintG, byte ColorTintB);

/// <summary>
/// Undo/redo for <see cref="OverlayState"/>, plus (via <see cref="CaptureExtra"/>/
/// <see cref="ApplyExtra"/>) any composite-mode-only state that lives outside
/// OverlayState (ControlPanelWindow's photo look, grain/vignette, and
/// placement fields) -- both ride the same single undo timeline instead of
/// needing a separate stack, since a Ctrl+Z should undo "whatever the user
/// just changed" regardless of which bucket of state it happened to land in.
///
/// Usage: call <see cref="BeginChange"/> right before a user-driven edit
/// starts (mouse-down on a drag/resize/rotate handle, before a Snap, before a
/// text box/slider edit begins via GotFocus), then <see cref="CommitChange"/>
/// once it's done (mouse-up, after Snap completes, on LostFocus) -- this
/// groups a whole drag gesture into a single undo step instead of one step
/// per intermediate mouse-move.
///
/// Begin/Commit calls can legitimately overlap: e.g. the user can be mid-drag
/// on a slider (focus gained, BeginChange already called) when VRChat's window
/// resizes and triggers an unrelated auto-reposition, which makes its own
/// Begin/Commit call before the slider's own LostFocus ever fires. A naive
/// single-pending-snapshot implementation would have the inner call's Commit
/// silently clear the outer call's pending snapshot, so the slider edit that
/// was in progress would never make it onto the undo stack at all. Tracking a
/// nesting depth instead -- only the outermost Begin captures, only the
/// outermost Commit finalizes -- fixes that: overlapping calls just merge into
/// one undo step spanning from whichever started first to whichever ended last.
/// </summary>
public sealed class UndoManager
{
    private sealed record Snapshot(OverlaySnapshot Overlay, object? Extra);

    private readonly OverlayState _state;
    private readonly Stack<Snapshot> _undoStack = new();
    private readonly Stack<Snapshot> _redoStack = new();
    private Snapshot? _pendingBefore;
    private int _depth;

    /// <summary>Set once by whichever window owns the extra (non-OverlayState)
    /// undoable fields, so Capture/Apply below can fold them into the same
    /// snapshot without UndoManager needing to know their concrete type.</summary>
    public Func<object?>? CaptureExtra { get; set; }
    public Action<object?>? ApplyExtra { get; set; }

    /// <summary>Fired right after Undo/Redo actually applies a change (not
    /// on a no-op call with an empty stack) -- lets a UI layer react (flash
    /// whichever rows changed, show a small icon) without UndoManager itself
    /// needing to know anything about WPF controls. The leading bool is true
    /// for a Redo, false for an Undo; the two snapshots are the one from
    /// just before the jump and the one just applied, so a listener can
    /// diff them field-by-field to find out what actually changed.</summary>
    public event Action<bool, OverlaySnapshot, OverlaySnapshot, object?, object?>? Applied;

    public UndoManager(OverlayState state)
    {
        _state = state;
    }

    private Snapshot Capture() => new(
        new OverlaySnapshot(
            _state.X, _state.Y, _state.Width, _state.Height, _state.RotationDegrees, _state.Opacity,
            _state.EdgeBlurRadius, _state.Brightness, _state.Contrast, _state.Saturation,
            _state.Vibrance, _state.Temperature, _state.Tint, _state.Hue,
            _state.Highlights, _state.Shadows, _state.Whites, _state.Blacks,
            _state.ColorTintStrength, _state.ColorTintR, _state.ColorTintG, _state.ColorTintB),
        CaptureExtra?.Invoke());

    private void Apply(Snapshot s)
    {
        // One undo/redo step is one logical operation even though it touches
        // 20+ OverlayState fields -- batching collapses those into a single
        // PropertyChanged, so subscribers that rebuild real UI on every
        // notification (OverlayWindow's guide redraw, ControlPanelWindow's
        // RefreshFromState) do that once per Undo/Redo instead of once per
        // field. See OverlayState.BeginBatch's own doc comment.
        _state.BeginBatch();
        try
        {
            ApplyCore(s);
        }
        finally
        {
            _state.EndBatch();
        }
    }

    private void ApplyCore(Snapshot s)
    {
        _state.X = s.Overlay.X;
        _state.Y = s.Overlay.Y;
        _state.Width = s.Overlay.Width;
        _state.Height = s.Overlay.Height;
        _state.RotationDegrees = s.Overlay.RotationDegrees;
        _state.Opacity = s.Overlay.Opacity;
        _state.EdgeBlurRadius = s.Overlay.EdgeBlurRadius;
        _state.Brightness = s.Overlay.Brightness;
        _state.Contrast = s.Overlay.Contrast;
        _state.Saturation = s.Overlay.Saturation;
        _state.Vibrance = s.Overlay.Vibrance;
        _state.Temperature = s.Overlay.Temperature;
        _state.Tint = s.Overlay.Tint;
        _state.Hue = s.Overlay.Hue;
        _state.Highlights = s.Overlay.Highlights;
        _state.Shadows = s.Overlay.Shadows;
        _state.Whites = s.Overlay.Whites;
        _state.Blacks = s.Overlay.Blacks;
        _state.ColorTintStrength = s.Overlay.ColorTintStrength;
        _state.ColorTintR = s.Overlay.ColorTintR;
        _state.ColorTintG = s.Overlay.ColorTintG;
        _state.ColorTintB = s.Overlay.ColorTintB;
        ApplyExtra?.Invoke(s.Extra);
    }

    public void BeginChange()
    {
        if (_depth == 0) _pendingBefore = Capture();
        _depth++;
    }

    public void CommitChange()
    {
        if (_depth == 0) return; // stray Commit with no matching Begin
        _depth--;
        if (_depth > 0) return; // still inside an outer, not-yet-committed BeginChange

        if (_pendingBefore is not { } before) return;
        _pendingBefore = null;
        var after = Capture();
        if (before == after) return; // nothing actually changed; don't clutter the undo stack
        _undoStack.Push(before);
        _redoStack.Clear();
    }

    // Both OverlayWindow and ControlPanelWindow are separate top-level windows
    // that can each independently receive a Ctrl+Z keypress; as a defensive
    // safety net against any double-delivery of what the user perceives as one
    // physical key press (whichever window-focus quirk might cause that), two
    // Undo/Redo calls arriving within this short a window collapse into one.
    private static readonly TimeSpan CallDebounce = TimeSpan.FromMilliseconds(60);
    private DateTime _lastUndoCall = DateTime.MinValue;
    private DateTime _lastRedoCall = DateTime.MinValue;

    public void Undo()
    {
        var now = DateTime.UtcNow;
        if (now - _lastUndoCall < CallDebounce) return;
        _lastUndoCall = now;

        if (_undoStack.Count == 0) return;
        var current = Capture();
        var previous = _undoStack.Pop();
        _redoStack.Push(current);
        Apply(previous);
        Applied?.Invoke(false, current.Overlay, previous.Overlay, current.Extra, previous.Extra);
    }

    public void Redo()
    {
        var now = DateTime.UtcNow;
        if (now - _lastRedoCall < CallDebounce) return;
        _lastRedoCall = now;

        if (_redoStack.Count == 0) return;
        var current = Capture();
        var next = _redoStack.Pop();
        _undoStack.Push(current);
        Apply(next);
        Applied?.Invoke(true, current.Overlay, next.Overlay, current.Extra, next.Extra);
    }
}
