using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AvaSnap.Views;

/// <summary>控えめなスクショ通知1枚: サムネイルを表示し、合成モードで開くか
/// 閉じるかを選ばせる。キーボードフォーカスを奪わず(WS_EX_NOACTIVATE)、
/// タスクバー/Alt-Tab にも出ない(WS_EX_TOOLWINDOW)ので、VRChat の操作を
/// 邪魔しない。</summary>
public partial class ScreenshotToastWindow : Window
{
    public event Action<ScreenshotToastWindow>? OpenRequested;
    public event Action<ScreenshotToastWindow>? DismissRequested;

    public string Path => _path;

    private readonly string _path;
    private static readonly TimeSpan AutoDismissAfter = TimeSpan.FromSeconds(8);
    private readonly DispatcherTimer _dismissTimer;

    public ScreenshotToastWindow(string path)
    {
        InitializeComponent();
        _path = path;
        FileNameText.Text = System.IO.Path.GetFileName(path);

        try
        {
            var thumb = new BitmapImage();
            thumb.BeginInit();
            thumb.CacheOption = BitmapCacheOption.OnLoad;
            thumb.DecodePixelWidth = 144;
            thumb.UriSource = new Uri(path);
            thumb.EndInit();
            thumb.Freeze();
            ThumbnailImage.Source = thumb;
        }
        catch (NotSupportedException)
        {
            // まだデコードできない画像。空のプレースホルダーを表示する。
        }
        catch (IOException)
        {
            // 準備チェックから今の間にファイルが消えた/ロックされた。無害。
        }

        _dismissTimer = new DispatcherTimer { Interval = AutoDismissAfter };
        _dismissTimer.Tick += (_, _) => DismissRequested?.Invoke(this);
        _dismissTimer.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    private void Window_MouseEnter(object sender, EventArgs e) => _dismissTimer.Stop();

    private void Window_MouseLeave(object sender, EventArgs e) => _dismissTimer.Start();

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        _dismissTimer.Stop();
        OpenRequested?.Invoke(this);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _dismissTimer.Stop();
        DismissRequested?.Invoke(this);
    }

    /// <summary>トーストは閉じない ── フォルダを開くのは副次的な操作で通知の解決では
    /// ないので、戻ってきたときに「合成する」が残っているべき。自動非表示タイマーは
    /// マウスがトースト上にある間 Window_MouseEnter で止まっているので、ここでは
    /// 何もしなくてよい。</summary>
    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_path}\"") { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // エクスプローラーの起動に失敗。ほかにできることはない。
        }
    }

    // ---- WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW: 出た瞬間に VRChat から
    //      キーボードフォーカスを奪い、Alt-Tab/タスクバーに独自エントリを出す
    //      トーストは「控えめ」とは言えない。 ----

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
}
