using System.Windows;
using AvaSnap.Views;

namespace AvaSnap.Services;

/// <summary>Turns <see cref="ScreenshotWatcherService.ScreenshotDetected"/>
/// events into a stack of non-intrusive toast windows in the bottom-right
/// corner of the primary screen's work area, newest on top. Multiple
/// screenshots taken in quick succession queue up as separate stacked toasts
/// instead of replacing each other.</summary>
public sealed class ScreenshotNotificationManager
{
    /// <summary>Raised when the user clicks "合成する" on a toast, with the
    /// screenshot's full path.</summary>
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

    /// <summary>Stacks every currently-open toast upward from the bottom-right
    /// corner of the work area, most-recently-added at the bottom.</summary>
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
