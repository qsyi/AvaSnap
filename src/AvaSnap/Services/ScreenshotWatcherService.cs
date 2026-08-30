using System.IO;
using System.Windows.Threading;

namespace AvaSnap.Services;

/// <summary>VRChat のスクリーンショットフォルダを監視し、新規 PNG が書き込み完了
/// したら <see cref="ScreenshotDetected"/> を発火する。既定は VRChat の保存先
/// (Pictures\VRChat。VRChat がさらに年月サブフォルダに分ける)だが、手動フォルダで
/// 上書きできる。監視開始「後」に作られたファイルにしか反応しない(既存内容は
/// スキャンしないので、再起動で古いスクショを再通知しない)。</summary>
public sealed class ScreenshotWatcherService : IDisposable
{
    public event Action<string>? ScreenshotDetected;

    private const int MaxReadyAttempts = 30; // 下の 300ms ポーリングで約9秒

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _readyPollTimer;
    private readonly Dictionary<string, int> _pendingReadyChecks = new();
    private FileSystemWatcher? _watcher;
    private string? _manualFolder;

    public ScreenshotWatcherService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _readyPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _readyPollTimer.Tick += ReadyPollTimer_Tick;
    }

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "VRChat");

    /// <summary>既定を上書きするユーザー選択フォルダ。null/空で自動検出。
    /// 設定すると新フォルダで監視を再起動する。</summary>
    public string? ManualFolder
    {
        get => _manualFolder;
        set
        {
            _manualFolder = value;
            Restart();
        }
    }

    public bool IsUsingManualFolder => !string.IsNullOrEmpty(_manualFolder) && Directory.Exists(_manualFolder);

    public string ActiveFolder => IsUsingManualFolder ? _manualFolder! : DefaultFolder;

    public void Start() => Restart();

    private void Restart()
    {
        _watcher?.Dispose();
        _watcher = null;
        _pendingReadyChecks.Clear();

        var folder = ActiveFolder;
        if (!Directory.Exists(folder)) return;

        var watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = true,
            Filter = "*.png",
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
        };
        watcher.Created += (_, args) => _dispatcher.BeginInvoke(() => EnqueueReadyCheck(args.FullPath));
        watcher.EnableRaisingEvents = true;
        _watcher = watcher;
        _readyPollTimer.Start();
    }

    private void EnqueueReadyCheck(string path)
    {
        _pendingReadyChecks.TryAdd(path, 0);
    }

    /// <summary>VRChat の書き込みは一瞬で終わらない(Created は空/途中のファイルが
    /// 現れた時点で発火する)ので、即通知すると書きかけ PNG のサムネイルを作ろうとする。
    /// 排他で開けるようになるまでポーリングし、<see cref="MaxReadyAttempts"/> で諦める
    /// (完了前にリネーム/削除されても無限ループしないように)。</summary>
    private void ReadyPollTimer_Tick(object? sender, EventArgs e)
    {
        if (_pendingReadyChecks.Count == 0) return;

        foreach (var path in _pendingReadyChecks.Keys.ToList())
        {
            if (!File.Exists(path))
            {
                _pendingReadyChecks.Remove(path);
                continue;
            }
            if (IsFileReady(path))
            {
                _pendingReadyChecks.Remove(path);
                ScreenshotDetected?.Invoke(path);
                continue;
            }
            int attempts = _pendingReadyChecks[path] + 1;
            if (attempts >= MaxReadyAttempts)
            {
                _pendingReadyChecks.Remove(path);
            }
            else
            {
                _pendingReadyChecks[path] = attempts;
            }
        }
    }

    private static bool IsFileReady(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return stream.Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _readyPollTimer.Stop();
    }
}
