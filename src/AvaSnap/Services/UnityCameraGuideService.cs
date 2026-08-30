using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace AvaSnap.Services;

/// <summary>Unity の CameraCompositionGuideExporter が今エクスポートしている
/// カメラの FOV/pitch/roll。AvaSnap が FOV を手入力せずに同等の一点透視ガイドを
/// 描くのに足りる。ワールド座標は含めない(AvaSnap に 3D シーンは無く、ガイドは
/// フレーム中央に置く)。</summary>
public sealed record UnityCameraGuideData(double Fov, double Pitch, double Roll);

/// <summary>Unity が書く生 JSON の形。public 必須: System.Text.Json は別アセンブリの
/// private/nested 型を構築できず、Deserialize が静かに失敗する(以前は nested private で、
/// 接続バッジがずっと 未接続 のままだった原因)。</summary>
public sealed class UnityCameraGuideExport
{
    public double fov { get; set; }
    public double pitch { get; set; }
    public double roll { get; set; }
    public string timestampUtc { get; set; } = "";
}

/// <summary>Unity の CameraCompositionGuideExporter が書く JSON を監視
/// (FileSystemWatcher が効けばそれ + 常に 500ms ポーリングのフォールバック)し、
/// 読み取りが本当に新しいエクスポート(<see cref="_lastSeenTimestampUtc"/> で判定)
/// だったときだけ <see cref="DataUpdated"/> を発火する。
///
/// リクエスト駆動: Unity は自分のカメラを定期ポーリングせず、
/// <see cref="RequestUpdate"/> が RequestPath を touch したら Unity 側の
/// FileSystemWatcher が反応して FilePath を1回書く。一回きりのスナップショットなので
/// 「古くなる」概念は無い(旧 BecameStale/StaleAfter は削除)。</summary>
public sealed class UnityCameraGuideService : IDisposable
{
    public event Action<UnityCameraGuideData>? DataUpdated;

    /// <summary>Unity の CameraCompositionGuideExporter が書くパス。コントロール
    /// パネルの「エクスプローラーで開く」ボタンがパス組み立てを重複させずに
    /// 指せるよう public。</summary>
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AvaSnap", "unity_camera_guide.json");

    /// <summary><see cref="RequestUpdate"/> が touch して Unity に新しい
    /// スナップショットを要求するファイル(中身は問わない。Unity の watcher は
    /// Changed/Created イベント自体に反応する)。FilePath と同じ AppData フォルダ。</summary>
    private static readonly string RequestPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AvaSnap", "unity_camera_guide_request.txt");

    private readonly Dispatcher _dispatcher;

    /// <summary>ポーリングのフォールバック。FileSystemWatcher はプロセス跨ぎ・
    /// ファイルシステム跨ぎで信頼性がまちまち(この構成で一度も発火しなかった実例あり)
    /// なので、watcher イベントは効けば速い経路、正しさを保証するのはこのタイマー。</summary>
    private readonly DispatcherTimer _pollTimer;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private FileSystemWatcher? _watcher;

    /// <summary>最後に DataUpdated を発火したファイルの Unity 側エクスポート
    /// タイムスタンプ。「本当に新しい取得」と「ポーリングが同じ古いファイルを
    /// 読み直しただけ」を区別する。これが無いと古いファイルで毎 tick 発火し続けて
    /// Unity が閉じていても応答しているように見えていた。</summary>
    private string? _lastSeenTimestampUtc;

    public UnityCameraGuideService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _pollTimer = new DispatcherTimer { Interval = PollInterval };
        _pollTimer.Tick += (_, _) => TryRead();
    }

    public void Start()
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        // watcher が張り付く実ディレクトリが要る(存在しないと FileSystemWatcher が投げる)。
        Directory.CreateDirectory(dir);

        // 既に FilePath にあるもの(前セッションの残り等)はこのセッションの取得ではない。
        // タイムスタンプを基準として記録し、TryRead が「既読」扱いにする。これが無いと
        // 起動直後に「取得」を押していないのに接続バッジが取得済みになっていた。
        SeedLastSeenTimestamp();

        try
        {
            _watcher = new FileSystemWatcher(dir)
            {
                Filter = Path.GetFileName(FilePath),
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            };
            _watcher.Changed += (_, _) => _dispatcher.BeginInvoke(TryRead);
            _watcher.Created += (_, _) => _dispatcher.BeginInvoke(TryRead);
            _watcher.EnableRaisingEvents = true;
        }
        catch (IOException)
        {
            // watcher なしでも下のポーリングが代替する(ほぼ即時が最大 PollInterval 遅延になるだけ)。
            _watcher = null;
        }

        _pollTimer.Start();
    }

    private void SeedLastSeenTimestamp()
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            var export = JsonSerializer.Deserialize<UnityCameraGuideExport>(File.ReadAllText(FilePath));
            _lastSeenTimestampUtc = export?.timestampUtc;
        }
        catch
        {
            // null のままでよい。最悪でも Start() 直後の新規書き込みは TryRead が拾う。
        }
    }

    /// <summary>Unity は File.WriteAllText(アトミックな rename ではない)で書くので、
    /// 書きかけで Changed が飛び得る。読みとパースを try で包んで、途中読みは黙って
    /// スキップする。リクエスト駆動なので、途中読みはその「取得」が空振りしただけ
    /// (再度押せば拾える。バックグラウンドリトライは無い)。</summary>
    private void TryRead()
    {
        if (!File.Exists(FilePath)) return;

        string json;
        try
        {
            json = File.ReadAllText(FilePath);
        }
        catch (IOException)
        {
            return;
        }

        UnityCameraGuideExport? export;
        try
        {
            export = JsonSerializer.Deserialize<UnityCameraGuideExport>(json);
        }
        catch (Exception ex)
        {
            // 別プロセス(Unity、古い exporter かもしれない)の内容。パース失敗は
            // 黙ってスキップしクラッシュさせない。ただし Trace には出す。
            Debug.WriteLine($"UnityCameraGuideService: failed to parse {FilePath}: {ex}");
            return;
        }
        if (export is null) return;
        // 前回と同じエクスポートなら発火しない(でないとポーリングが古いファイルで
        // 毎 PollInterval 発火し続ける)。
        if (export.timestampUtc == _lastSeenTimestampUtc) return;
        _lastSeenTimestampUtc = export.timestampUtc;
        DataUpdated?.Invoke(new UnityCameraGuideData(export.fov, export.pitch, export.roll));
    }

    /// <summary>Unity の CameraCompositionGuideExporter(Editor 起動中なら。Unity 側の
    /// 設定は不要)に新スナップショットを要求する ── RequestPath を touch すると Unity 側が
    /// FilePath を1回書き、それを TryRead が拾う。撃ちっぱなし: Unity 未起動なら黙って何も
    /// しない(ハンドシェイクは無く「新ファイルが来たら DataUpdated」だけ)。</summary>
    public void RequestUpdate()
    {
        try
        {
            var dir = Path.GetDirectoryName(RequestPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(RequestPath, DateTime.UtcNow.ToString("o"));
            Debug.WriteLine($"UnityCameraGuideService: wrote request to {RequestPath}");
        }
        catch (IOException ex)
        {
            // Unity がこのファイルを開いている瞬間かもしれない。無害(再度「取得」でよい)。
            Debug.WriteLine($"UnityCameraGuideService: RequestUpdate IOException: {ex}");
        }
        catch (Exception ex)
        {
            // 権限・パス等。Trace に出す。
            Debug.WriteLine($"UnityCameraGuideService: RequestUpdate failed: {ex}");
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _pollTimer.Stop();
    }
}
