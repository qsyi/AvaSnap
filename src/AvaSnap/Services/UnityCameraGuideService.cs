using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace AvaSnap.Services;

/// <summary>FOV/pitch/roll of whatever camera Unity's own
/// CameraCompositionGuideExporter editor script is currently exporting --
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
/// Unity's CameraCompositionGuideExporter editor script writes (see that
/// script's own doc comment for the writer side) and raises
/// <see cref="DataUpdated"/> whenever a read turns up a genuinely NEW
/// export (see _lastSeenTimestampUtc) -- not on every successful read,
/// which would otherwise fire every poll tick forever off the same
/// leftover file.
///
/// REQUEST-DRIVEN, not continuous: Unity no longer polls its own camera on
/// a timer -- it sits idle until <see cref="RequestUpdate"/> touches
/// RequestPath, which Unity's own FileSystemWatcher reacts to by writing
/// FilePath exactly once. There's deliberately no more staleness concept
/// here (the old BecameStale/StaleAfter pair, removed): a one-shot
/// snapshot doesn't go "stale" the way a continuous feed did, it's just
/// whatever the last successful RequestUpdate returned.</summary>
public sealed class UnityCameraGuideService : IDisposable
{
    public event Action<UnityCameraGuideData>? DataUpdated;

    /// <summary>Same path Unity's CameraCompositionGuideExporter editor script
    /// writes to -- exposed publicly so the control panel's own "エクスプ
    /// ローラーで開く" button can point at it without duplicating the path
    /// logic.</summary>
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AvaSnap", "unity_camera_guide.json");

    /// <summary>Touched (any content -- Unity's watcher reacts to the file
    /// Changed/Created event itself, not what's inside) by
    /// <see cref="RequestUpdate"/> to ask Unity's CameraCompositionGuideExporter
    /// for a fresh snapshot. Same AppData folder as FilePath, matching that
    /// script's own RequestPath.</summary>
    private static readonly string RequestPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AvaSnap", "unity_camera_guide_request.txt");

    private readonly Dispatcher _dispatcher;

    /// <summary>The poll-fallback (see its Tick handler in Start()) --
    /// FileSystemWatcher's cross-process, cross-filesystem reliability is
    /// inconsistent enough in practice (observed: it simply never fired for
    /// this exact file/folder in testing, likely environment-specific --
    /// antivirus, indexing, or just an OS quirk) that watcher events are now
    /// only the FAST path when they happen to work; this timer is what
    /// actually guarantees correctness regardless.</summary>
    private readonly DispatcherTimer _pollTimer;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private FileSystemWatcher? _watcher;

    /// <summary>Unity's own export timestamp from the last file we actually
    /// fired DataUpdated for -- lets TryRead tell "a genuinely new fetch
    /// landed" apart from "the 500ms poll timer re-read the exact same
    /// leftover file Unity wrote once, possibly in a previous session, and
    /// hasn't touched since". Without this, DataUpdated (and the
    /// 最終取得 badge it drives) kept firing every single poll tick forever
    /// off a stale file, making it look like Unity was continuously
    /// responding even while closed.</summary>
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
        // Created even if Unity has never written anything yet, purely so
        // the watcher has a real directory to attach to -- FileSystemWatcher
        // throws if the path doesn't exist.
        Directory.CreateDirectory(dir);

        // Whatever's already sitting at FilePath (e.g. left over from a
        // previous AvaSnap/Unity session, or from Unity's own "今すぐ送信
        // (テスト用)" button) is NOT a fetch THIS session ever asked for --
        // silently record its timestamp as the baseline so TryRead treats
        // it as "already seen" instead of firing DataUpdated for it the
        // moment polling starts. Without this, the connection badge showed
        // 取得済み immediately on launch even though 取得 was never pressed.
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
            // No watcher, no problem -- the poll timer below covers
            // everything the watcher would have, just with up to
            // PollInterval of extra latency instead of near-instant.
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
            // Leave it null -- worst case, a genuinely fresh write right
            // after Start() still gets picked up correctly by TryRead.
        }
    }

    /// <summary>Unity writes via a plain File.WriteAllText (not an atomic
    /// rename), so a Changed event can fire while the file is only
    /// partially written -- both the read and the parse are wrapped so a
    /// torn read just gets silently skipped. Since Unity is now request-
    /// driven (see RequestUpdate), a torn read here just means this
    /// particular 取得 request effectively went unanswered; the user
    /// pressing it again is what picks it up, not a background retry.</summary>
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
        // Dirty-check: skip firing if this is the same export Unity wrote
        // last time (see _lastSeenTimestampUtc's own doc comment) --
        // otherwise the poll-fallback alone would re-fire DataUpdated every
        // PollInterval forever off whatever file happens to already be on
        // disk, Unity running or not.
        if (export.timestampUtc == _lastSeenTimestampUtc) return;
        _lastSeenTimestampUtc = export.timestampUtc;
        DataUpdated?.Invoke(new UnityCameraGuideData(export.fov, export.pitch, export.roll));
    }

    /// <summary>Asks Unity's CameraCompositionGuideExporter (if the Editor
    /// is running -- no setup or toggle needed on that side at all) for a
    /// fresh snapshot -- touches RequestPath, which its FileSystemWatcher/
    /// poll-fallback reacts to by writing FilePath once; TryRead (via this
    /// service's own watcher/poll-fallback) picks that up the same way it
    /// always has. Fire-and-forget: if Unity isn't running, this silently
    /// does nothing -- there's no request/response handshake, just "if a
    /// fresh file shows up, DataUpdated fires".</summary>
    public void RequestUpdate()
    {
        try
        {
            var dir = Path.GetDirectoryName(RequestPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(RequestPath, DateTime.UtcNow.ToString("o"));
        }
        catch (IOException)
        {
            // Unity might have this file open at the exact wrong instant --
            // harmless, the user can just press 取得 again.
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _pollTimer.Stop();
    }
}
