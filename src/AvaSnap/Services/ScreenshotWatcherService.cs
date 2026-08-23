using System.IO;
using System.Windows.Threading;

namespace AvaSnap.Services;

/// <summary>Watches VRChat's screenshot folder for newly-created PNGs and
/// raises <see cref="ScreenshotDetected"/> once each file is fully written.
/// Defaults to VRChat's own default save location (Pictures\VRChat, which
/// VRChat further splits into year-month subfolders), but a manual folder can
/// override that. Only reacts to files created AFTER watching starts -- it
/// never scans existing folder contents, so relaunching AvaSnap won't
/// re-notify about old screenshots.</summary>
public sealed class ScreenshotWatcherService : IDisposable
{
    public event Action<string>? ScreenshotDetected;

    private const int MaxReadyAttempts = 30; // ~9s at the 300ms poll interval below

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

    /// <summary>A user-chosen folder that overrides the default, or null/empty
    /// to auto-detect. Setting this restarts the watcher on the new folder.</summary>
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

    /// <summary>VRChat's screenshot write isn't instantaneous -- the Created
    /// event fires as soon as the (still-empty/partial) file appears, so a
    /// notification fired immediately would try to thumbnail a half-written
    /// PNG. Poll until the file can be opened exclusively (nothing else still
    /// has it open for writing), giving up after <see cref="MaxReadyAttempts"/>
    /// so a file that gets renamed/deleted before finishing doesn't loop
    /// forever.</summary>
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
