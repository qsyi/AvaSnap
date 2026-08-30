namespace AvaSnap;

/// <summary>Undo に値するオーバーレイのプロパティのスナップショット(位置・サイズ・
/// 回転・不透明度・PNG のルック調整)。クリックスルーは除外(編集ではなくモード
/// トグル)。読み込み画像のパスも除外: OverlayWindow は明示的な LoadImage() でしか
/// ピクセルを読み直さないので、Undo/Redo でパスだけ書き換わると表示画像とずれ、
/// それが settings.json に保存されて次回起動で別 PNG が復元されていた。</summary>
public sealed record OverlaySnapshot(
    double X, double Y, double Width, double Height, double RotationDegrees, double Opacity,
    double EdgeBlurRadius, double Brightness, double Contrast, double Saturation,
    double Vibrance, double Temperature, double Tint, double Hue,
    double Highlights, double Shadows, double Whites, double Blacks,
    double ColorTintStrength, byte ColorTintR, byte ColorTintG, byte ColorTintB);

/// <summary><see cref="OverlayState"/> の Undo/Redo と、<see cref="CaptureExtra"/>/
/// <see cref="ApplyExtra"/> 経由で OverlayState 外の合成モード専用状態
/// (ControlPanelWindow の写真ルック・グレイン/ビネット・配置)。Ctrl+Z は
/// 「直前に変えたもの」を戻すべきなので、両者を別スタックにせず1つのタイムラインに乗せる。
///
/// 使い方: 編集開始直前に <see cref="BeginChange"/>(ハンドルの mouse-down、Snap の前、
/// テキスト/スライダーの GotFocus)、完了時に <see cref="CommitChange"/>(mouse-up、
/// Snap 完了後、LostFocus)。ドラッグ全体を1 Undo ステップにまとめる。
///
/// Begin/Commit は正当に入れ子になり得る(スライダードラッグ中に VRChat リサイズが
/// 別の自動再配置を起こす等)。単一 pending スナップショットだと内側の Commit が外側の
/// pending を消してしまうので、深さカウンタで最外の Begin だけ捕捉・最外の Commit だけ
/// 確定する。重なった呼び出しは1ステップにマージされる。</summary>
public sealed class UndoManager
{
    private sealed record Snapshot(OverlaySnapshot Overlay, object? Extra);

    private readonly OverlayState _state;
    private readonly Stack<Snapshot> _undoStack = new();
    private readonly Stack<Snapshot> _redoStack = new();
    private Snapshot? _pendingBefore;
    private int _depth;

    /// <summary>OverlayState 外の undo 対象フィールドを持つウィンドウが一度だけ設定する。
    /// UndoManager が具体型を知らずに同じスナップショットへ畳み込めるようにするため。</summary>
    public Func<object?>? CaptureExtra { get; set; }
    public Action<object?>? ApplyExtra { get; set; }

    /// <summary>Undo/Redo が実際に変更を適用した直後に発火(空スタックの no-op では
    /// 発火しない)。UI が反応(変わった行をフラッシュ、アイコン表示)できるように。
    /// 先頭の bool は Redo=true / Undo=false。2つのスナップショットはジャンプ直前と
    /// 適用後で、リスナーがフィールド単位で差分を取れる。</summary>
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
        // 1 Undo/Redo ステップは 20+ フィールドに触れても1つの論理操作。バッチで
        // PropertyChanged を1回にまとめ、通知ごとに UI を作り直す購読者(ガイド再描画、
        // RefreshFromState)がフィールド単位ではなくステップ単位で走るようにする。
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

    /// <summary>両スタック(と開きかけの pending)を消す。合成の写真ソースが変わった時
    /// (新しいスクショ、新規「背景なしで作成」)に呼ぶ。各スナップショットの配置/ルック/
    /// デカール/背景色はその写真に対してのみ意味を持ち、境界を跨いで Ctrl+Z すると
    /// 別画像に対して無意味な状態を復元してしまうため。</summary>
    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        _pendingBefore = null;
        _depth = 0;
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

    // OverlayWindow と ControlPanelWindow は別々のトップレベルウィンドウで、それぞれ
    // 独立に Ctrl+Z を受け得る。1回の物理キー押下が二重配送されるケースへの保険として、
    // この短時間内に届いた2つの Undo/Redo は1つにまとめる。
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
