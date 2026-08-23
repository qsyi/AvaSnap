using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace AvaSnap.Services;

/// <summary>FOV/pitch/roll of whatever camera Unity's own
/// CameraCompositionGuideWindow editor script is currently exporting --
/// enough for AvaSnap to draw an equivalent one-point-perspective guide
/// over the live VRChat window without the user re-typing FOV by hand.
/// World position is deliberately not part of this: AvaSnap has no 3D
/// scene to place it in, so the guide is centered on the frame the same
/// way the Unity-side version was.</summary>
public sealed record UnityCameraGuideData(double Fov, double Pitch, double Roll);

/// <summary>Raw JSON shape Unity writes -- MUST be public: System.Text.Json's
/// default reflection-based (de)serializer silently fails to construct a
/// private/nested type from another assembly (this used to be private,
/// nested inside UnityCameraGuideService below, and every single
/// Deserialize call was throwing -- caught by TryRead's broad catch, so it
/// never crashed, it just never produced any data either, which is exactly
/// why the connection badge sat on 未接続 forever despite the file
/// genuinely updating).</summary>
public sealed class UnityCameraGuideExport
{
    public double fov { get; set; }
    public double pitch { get; set; }
    public double roll { get; set; }
    public string timestampUtc { get; set; } = "";
}

/// <summary>Watches (via FileSystemWatcher when that works, and always via
/// a 500ms poll-fallback regardless -- see _pollTimer) the JSON file
/// Unity's CameraCompositionGuideWindow editor script writes (see that
/// script's own doc comment for the writer side) and raises
/// <see cref="DataUpdated"/> on every successful read. <see cref="BecameStale"/>
/// fires once the most recently read timestamp is old enough that Unity is
/// presumed to have stopped exporting (closed, or the guide toggled off
/// there), so a caller can hide the guide instead of freezing it on a
/// last-known angle forever.</summary>
public sealed class UnityCameraGuideService : IDisposable
{
    public event Action<UnityCameraGuideData>? DataUpdated;
    public event Action? BecameStale;

    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(2);

    /// <summary>Same path Unity's CameraCompositionGuideWindow editor script
    /// writes to -- exposed publicly so the control panel's own "エクスプ
    /// ローラーで開く" button can point at it without duplicating the path
    /// logic.</summary>
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AvaSnap", "unity_camera_guide.json");

    private readonly Dispatcher _dispatcher;

    /// <summary>Both the poll-fallback and the staleness check ride this one
    /// timer (see its Tick handler in Start()) -- FileSystemWatcher's
    /// cross-process, cross-filesystem reliability is inconsistent enough
    /// in practice (observed: it simply never fired for this exact file/
    /// folder in testing, likely environment-specific -- antivirus,
    /// indexing, or just an OS quirk) that watcher events are now only the
    /// FAST path when they happen to work; this timer is what actually
    /// guarantees correctness regardless.</summary>
    private readonly DispatcherTimer _pollTimer;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private FileSystemWatcher? _watcher;
    private DateTime _lastGoodTimestampUtc;
    private bool _isStale = true;

    public UnityCameraGuideService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _pollTimer = new DispatcherTimer { Interval = PollInterval };
        _pollTimer.Tick += (_, _) => { TryRead(); CheckStale(); };
    }

    public void Start()
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        // Created even if Unity has never written anything yet, purely so
        // the watcher has a real directory to attach to -- FileSystemWatcher
        // throws if the path doesn't exist.
        Directory.CreateDirectory(dir);

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
            // No watcher, no problem -- the poll timer below covers
            // everything the watcher would have, just with up to
            // PollInterval of extra latency instead of near-instant.
            _watcher = null;
        }

        _pollTimer.Start();
        TryRead();
    }

    /// <summary>Unity writes via a plain File.WriteAllText (not an atomic
    /// rename), so a Changed event can fire while the file is only
    /// partially written -- both the read and the parse are wrapped so a
    /// torn read just gets silently skipped, and the NEXT Changed event
    /// (Unity's next export tick, ~150ms later) picks it up instead.</summary>
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
            // Content from a separate process (Unity), potentially a
            // different/older version of the exporter script -- any parse
            // failure here should be silently skipped, never crash AvaSnap.
            // Traced (not swallowed silently) so a future mismatch shows up
            // in a debugger/DebugView instead of just quietly never
            // producing data the way the private-type bug this replaced did.
            Debug.WriteLine($"UnityCameraGuideService: failed to parse {FilePath}: {ex}");
            return;
        }
        if (export is null) return;
        // RoundtripKind and AdjustToUniversal can't be combined (DateTime.
        // TryParse throws ArgumentException, "Try" only covers parse-format
        // failures, not invalid style-flag combinations) -- parse with
        // RoundtripKind alone (Unity's DateTime.UtcNow.ToString("o") already
        // round-trips as Kind=Utc) and convert explicitly afterward instead.
        if (!DateTime.TryParse(export.timestampUtc, null,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var parsedTimestamp))
            return;
        var timestampUtc = parsedTimestamp.Kind == DateTimeKind.Utc ? parsedTimestamp : parsedTimestamp.ToUniversalTime();

        _lastGoodTimestampUtc = timestampUtc;
        _isStale = false;
        DataUpdated?.Invoke(new UnityCameraGuideData(export.fov, export.pitch, export.roll));
    }

    private void CheckStale()
    {
        if (_isStale) return;
        if (DateTime.UtcNow - _lastGoodTimestampUtc <= StaleAfter) return;
        _isStale = true;
        BecameStale?.Invoke();
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _pollTimer.Stop();
    }
}
