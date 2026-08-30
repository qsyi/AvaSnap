using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AvaSnap;

/// <summary>オーバーレイ画像の共有・監視可能な状態。オーバーレイウィンドウ(描画)と
/// コントロールパネル(数値編集)の両方がバインドする。</summary>
public sealed class OverlayState : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    // ---- バッチ: いくつかの呼び出し元(UndoManager.Apply、ルック一致ボタン、
    //      配置推定の再計算)が10数個のプロパティを1つの論理操作としてセットする。
    //      バッチしないと Set ごとに PropertyChanged が飛び、通知ごとに UI を作り直す
    //      購読者がフィールド単位で走る。BeginBatch/EndBatch は入れ子になる
    //      (UndoManager と同じ深さカウンタ)。最後に PropertyChangedEventArgs(null) を
    //      1回だけ流す(INotifyPropertyChanged の「複数変わった」慣習)。 ----
    private int _batchDepth;
    private bool _batchChangedAny;

    public void BeginBatch() => _batchDepth++;

    public void EndBatch()
    {
        if (_batchDepth == 0) return; // 対応する BeginBatch の無い迷子 EndBatch
        _batchDepth--;
        if (_batchDepth > 0) return; // まだ外側の BeginBatch 内
        if (!_batchChangedAny) return;
        _batchChangedAny = false;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        if (_batchDepth > 0)
        {
            _batchChangedAny = true;
            return;
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private string? _imagePath;
    public string? ImagePath { get => _imagePath; set => Set(ref _imagePath, value); }

    private double _x = 200;
    public double X { get => _x; set => Set(ref _x, value); }

    private double _y = 200;
    public double Y { get => _y; set => Set(ref _y, value); }

    // FrameworkElement の Width/Height を負値にすると WPF が投げてアプリごと落ちる。
    // ここでクランプし、上流の不正値が「とても小さい」に劣化するだけで済むようにする。
    private double _width = 400;
    public double Width { get => _width; set => Set(ref _width, Math.Max(1, value)); }

    private double _height = 300;
    public double Height { get => _height; set => Set(ref _height, Math.Max(1, value)); }

    private double _rotationDegrees;
    public double RotationDegrees { get => _rotationDegrees; set => Set(ref _rotationDegrees, value); }

    private double _opacity = 0.9;
    public double Opacity { get => _opacity; set => Set(ref _opacity, value); }

    private bool _isClickThrough = true;
    public bool IsClickThrough { get => _isClickThrough; set => Set(ref _isClickThrough, value); }

    private bool _isImageVisible = true;
    /// <summary>アバターオーバーレイ(とハンドル)の完全な表示/非表示。Opacity(フェード)
    /// とも IsClickThrough(マウス操作のみ)とも別。IsClickThrough と同じモードトグルなので
    /// undo スナップショットには入れない。</summary>
    public bool IsImageVisible { get => _isImageVisible; set => Set(ref _isImageVisible, value); }

    // ---- PNG ルック調整: 位置合わせモードのライブプレビュー(VRChat 上)と合成モードの
    //      プレビュー(撮影写真上)で共有。「この切り抜きがどう見えるか」は背景に依らない
    //      ので、どちらから編集しても同じ値を変える。 ----

    private double _edgeBlurRadius = 5;
    /// <summary>PNG のアルファチャンネルにだけかけるぼかしのピクセル半径(内部ディテールを
    /// ぼかさずシルエットだけ柔らかく)。0 = オフ。</summary>
    public double EdgeBlurRadius { get => _edgeBlurRadius; set => Set(ref _edgeBlurRadius, Math.Max(0, value)); }

    private double _brightness;
    /// <summary>-100..100、0 = 変化なし。</summary>
    public double Brightness { get => _brightness; set => Set(ref _brightness, value); }

    private double _contrast;
    /// <summary>-100..100、0 = 変化なし。</summary>
    public double Contrast { get => _contrast; set => Set(ref _contrast, value); }

    private double _saturation;
    /// <summary>-100..100、0 = 変化なし、-100 = グレースケール。</summary>
    public double Saturation { get => _saturation; set => Set(ref _saturation, value); }

    private double _vibrance;
    /// <summary>-100..100、0 = 変化なし。彩度に似るが、既に十分彩度の高い画素
    /// (肌色等)では効果を弱めるので、上げても顔が過飽和にならない。</summary>
    public double Vibrance { get => _vibrance; set => Set(ref _vibrance, value); }

    private double _temperature;
    /// <summary>-100..100、0 = 変化なし。負 = 寒色(青寄り)、正 = 暖色(橙寄り)。</summary>
    public double Temperature { get => _temperature; set => Set(ref _temperature, value); }

    private double _tint;
    /// <summary>-100..100、0 = 変化なし。負 = マゼンタ寄り、正 = グリーン寄り。</summary>
    public double Tint { get => _tint; set => Set(ref _tint, value); }

    private double _hue;
    /// <summary>色相環まわりの色相回転 -180..180 度。0 = 変化なし。</summary>
    public double Hue { get => _hue; set => Set(ref _hue, value); }

    private double _highlights;
    /// <summary>-100..100、0 = 変化なし。明部を暗部より強く明暗する。</summary>
    public double Highlights { get => _highlights; set => Set(ref _highlights, value); }

    private double _shadows;
    /// <summary>-100..100、0 = 変化なし。暗部を明部より強く明暗する。</summary>
    public double Shadows { get => _shadows; set => Set(ref _shadows, value); }

    private double _whites;
    /// <summary>-100..100、0 = 変化なし。ハイライトに似るが、最も明るい階調(白クリップ点)へより狭く集中。</summary>
    public double Whites { get => _whites; set => Set(ref _whites, value); }

    private double _blacks;
    /// <summary>-100..100、0 = 変化なし。シャドウに似るが、最も暗い階調(黒クリップ点)へより狭く集中。</summary>
    public double Blacks { get => _blacks; set => Set(ref _blacks, value); }

    private double _colorTintStrength;
    /// <summary>0..100、0 = オフ。ColorTintR/G/B へ寄せる、輝度を保つ色被せ
    /// (上の色温度/色かぶりとは別軸)。</summary>
    public double ColorTintStrength { get => _colorTintStrength; set => Set(ref _colorTintStrength, value); }

    private byte _colorTintR = 255, _colorTintG = 255, _colorTintB = 255;
    public byte ColorTintR { get => _colorTintR; set => Set(ref _colorTintR, value); }
    public byte ColorTintG { get => _colorTintG; set => Set(ref _colorTintG, value); }
    public byte ColorTintB { get => _colorTintB; set => Set(ref _colorTintB, value); }

    // ---- Unity連携ガイド: ライブの VRChat 窓上に表示(OverlayWindow)、位置合わせ
    //      モード側から操作する。表示時の補助でしかなく保存物には焼き込まれない。
    //      GuideManualFov/Pitch/Roll だけが描画の真実で、Unity 取得成功時もこの3つに
    //      書き込む(手入力と同じ)。 ----

    private bool _guideVisible;
    public bool GuideVisible { get => _guideVisible; set => Set(ref _guideVisible, value); }

    private double _guideManualFov = 45; // VRChat カメラの FOV 既定値
    public double GuideManualFov { get => _guideManualFov; set => Set(ref _guideManualFov, value); }

    private double _guideManualPitch;
    public double GuideManualPitch { get => _guideManualPitch; set => Set(ref _guideManualPitch, value); }

    private double _guideManualRoll;
    public double GuideManualRoll { get => _guideManualRoll; set => Set(ref _guideManualRoll, value); }
}
