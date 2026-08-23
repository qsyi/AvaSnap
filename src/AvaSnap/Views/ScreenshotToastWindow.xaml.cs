using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AvaSnap.Views;

/// <summary>A single non-intrusive screenshot notification: shows a thumbnail
/// and lets the user open it in Composite mode, or dismiss it. Never steals
/// keyboard focus (WS_EX_NOACTIVATE) and never shows in the taskbar/Alt-Tab
/// (WS_EX_TOOLWINDOW) -- clicking it doesn't interrupt whatever the user is
/// doing in VRChat, unlike a normal window popping up would.</summary>
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
            // Not a decodable image yet somehow; show the blank placeholder.
        }
        catch (IOException)
        {
            // File vanished/locked between the ready-check and now; harmless.
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

    /// <summary>Doesn't dismiss the toast (no DismissRequested/OpenRequested
    /// call) -- opening the folder is a side action, not a resolution of
    /// the notification, so "合成する" should still be there afterward if
    /// the user comes back to it. The auto-dismiss timer is already stopped
    /// by Window_MouseEnter for as long as the mouse is over the toast to
    /// click this, so nothing needs to be done with it here either.</summary>
    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_path}\"") { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // Explorer failed to launch; nothing more to do.
        }
    }

    // ---- WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW: a toast that would otherwise
    //      steal keyboard focus from VRChat the instant it appears, and show
    //      up as its own Alt-Tab/taskbar entry, is not "non-intrusive". ----

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
}
