using System.IO;
using System.Threading;

namespace AvaSnap.Services;

/// <summary>多重起動を1つに束ねる。2つ目の起動は、開きたい .avasnap のパスを
/// 受け渡しファイルへ書いてイベントで通知し、そのまま終了する。最初の起動が
/// <see cref="ListenForOpenRequests"/> でそれを受けて既存ウィンドウで開く。
/// ダブルクリックのファイル関連付け(1ファイル=1プロセスになりがち)対策。</summary>
internal static class SingleInstance
{
    private const string MutexName = "AvaSnap.SingleInstance.Mutex";
    private const string EventName = "AvaSnap.SingleInstance.Open";

    private static readonly string RequestFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AvaSnap", "open-request.txt");

    private static Mutex? _mutex;
    private static EventWaitHandle? _signal;

    /// <summary>この起動が唯一のインスタンスなら <c>true</c>。既に起動中なら、
    /// 引数の .avasnap を既存インスタンスへ引き渡してから <c>false</c> を返す
    /// (呼び出し側はそのままプロセス終了すること)。</summary>
    public static bool TryAcquire(string[] args)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (createdNew)
        {
            _signal = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
            return true;
        }

        // 既存インスタンスへ「このファイルを開いて」を通知
        string? path = FindProjectArg(args);
        try
        {
            if (!string.IsNullOrEmpty(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RequestFile)!);
                File.WriteAllText(RequestFile, path);
            }
            if (EventWaitHandle.TryOpenExisting(EventName, out var existing))
            {
                existing.Set();
                existing.Dispose();
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (WaitHandleCannotBeOpenedException) { }
        return false;
    }

    /// <summary>2つ目の起動からの「開いて」通知を待ち受ける。<paramref name="onOpen"/> は
    /// バックグラウンドスレッドから呼ばれるので、UI 反映は呼び出し側で Dispatcher へ。</summary>
    public static void ListenForOpenRequests(Action<string> onOpen)
    {
        if (_signal is null) return;
        var t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    _signal.WaitOne();
                    string path = File.Exists(RequestFile) ? File.ReadAllText(RequestFile).Trim() : "";
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        onOpen(path);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (ObjectDisposedException) { return; }
            }
        })
        { IsBackground = true, Name = "AvaSnap.SingleInstance.Listener" };
        t.Start();
    }

    public static void Release()
    {
        try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex?.Dispose();
        _signal?.Dispose();
        _mutex = null;
        _signal = null;
    }

    private static string? FindProjectArg(string[] args)
    {
        foreach (var a in args)
        {
            if (a.EndsWith(ProjectService.Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(a))
                return Path.GetFullPath(a);
        }
        return null;
    }
}
