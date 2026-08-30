using System.Windows;
using AvaSnap.Views;

namespace AvaSnap.Services;

/// <summary><see cref="ScreenshotWatcherService.ScreenshotDetected"/> を、
/// プライマリ画面の作業領域右下に積み上がる控えめなトーストにする(新しいものが上)。
/// 連続して撮ると置き換えず別々のトーストとして積む。</summary>
public sealed class ScreenshotNotificationManager
{
    /// <summary>トーストの「合成する」が押されたときに、スクショのフルパスとともに発火。</summary>
    public event Action<string>? PhotoSelected;

    private const double Gap = 8;
    private readonly List<ScreenshotToastWindow> _toasts = new();

    public ScreenshotNotificationManager(ScreenshotWatcherService watcher)
    {
        watcher.ScreenshotDetected += OnScreenshotDetected;
    }

    private void OnScreenshotDetected(string path)
    {
        var toast = new ScreenshotToastWindow(path);
        toast.OpenRequested += Toast_OpenRequested;
        toast.DismissRequested += Toast_DismissRequested;
        _toasts.Add(toast);
        toast.Show();
        Reflow();
    }

    private void Toast_OpenRequested(ScreenshotToastWindow toast)
    {
        PhotoSelected?.Invoke(toast.Path);
        Remove(toast);
    }

    private void Toast_DismissRequested(ScreenshotToastWindow toast) => Remove(toast);

    private void Remove(ScreenshotToastWindow toast)
    {
        _toasts.Remove(toast);
        toast.Close();
        Reflow();
    }

    /// <summary>開いている全トーストを作業領域右下から上へ積む(最新が一番下)。</summary>
    private void Reflow()
    {
        double right = SystemParameters.WorkArea.Right;
        double bottom = SystemParameters.WorkArea.Bottom;
        double y = bottom;
        for (int i = _toasts.Count - 1; i >= 0; i--)
        {
            var toast = _toasts[i];
            y -= toast.Height;
            toast.Left = right - toast.Width;
            toast.Top = y;
            y -= Gap;
        }
    }
}
