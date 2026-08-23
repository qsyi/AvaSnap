using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AvaSnap.Views;

// ---- Recent avatars / recent photos: small thumbnail quick-pick rows next
//      to the usual file-dialog buttons, so switching back to an avatar or
//      background photo used a moment ago doesn't need a fresh Explorer
//      round-trip every time. Both share the same thumbnail-button builder
//      below. Split into its own file from the rest of ControlPanelWindow --
//      a self-contained concern (recent-file bookkeeping + thumbnail
//      rendering) that doesn't need to sit alongside window navigation,
//      the composite render pipeline, or the color-wheel math. ----
public partial class ControlPanelWindow
{
    private const int RecentThumbnailSize = 34;
    private const int RecentThumbnailSpacing = 6; // matches CreateThumbnailButton's own right Margin

    /// <summary>How many paths are actually kept in memory/persisted --
    /// deliberately more than any row could ever display (see
    /// CalculateRecentThumbnailFitCount), so widening the window later can
    /// reveal more thumbnails without needing to have remembered more than
    /// this at the time.</summary>
    private const int MaxRecentAvatarHistory = 20;
    private const int MaxRecentPhotoScan = 20;
    private List<string> _recentAvatarPaths = new();

    /// <summary>Read by App.xaml.cs at exit to persist alongside the other
    /// settings, and set once at startup via <see cref="SetRecentAvatarPaths"/>.</summary>
    public IReadOnlyList<string> RecentAvatarPaths => _recentAvatarPaths;

    public void SetRecentAvatarPaths(IEnumerable<string> paths)
    {
        _recentAvatarPaths = paths.Where(File.Exists).Take(MaxRecentAvatarHistory).ToList();
        RefreshRecentAvatarsUI();
    }

    private void AddRecentAvatarPath(string path)
    {
        _recentAvatarPaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _recentAvatarPaths.Insert(0, path);
        if (_recentAvatarPaths.Count > MaxRecentAvatarHistory)
            _recentAvatarPaths.RemoveRange(MaxRecentAvatarHistory, _recentAvatarPaths.Count - MaxRecentAvatarHistory);
        RefreshRecentAvatarsUI();
    }

    private void RefreshRecentAvatarsUI()
    {
        PopulateThumbnailRow(AlignRecentAvatarsPanel, _recentAvatarPaths, LoadImageFile, AlignRecentAvatarsPanel.ActualWidth);
        PopulateThumbnailRow(CompositeRecentAvatarsPanel, _recentAvatarPaths, LoadImageFile, CompositeRecentAvatarsPanel.ActualWidth);
    }

    /// <summary>Re-populates just one recent-thumbnail row at its current
    /// width -- wired to each row's own SizeChanged in XAML, so widening the
    /// window (or switching between Align's single narrow column and
    /// Composite's wider one) reveals or hides thumbnails to fill however
    /// much space is actually available, instead of a fixed count.</summary>
    private void AlignRecentAvatarsPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width == e.PreviousSize.Width) return;
        PopulateThumbnailRow(AlignRecentAvatarsPanel, _recentAvatarPaths, LoadImageFile, e.NewSize.Width);
    }

    private void CompositeRecentAvatarsPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width == e.PreviousSize.Width) return;
        PopulateThumbnailRow(CompositeRecentAvatarsPanel, _recentAvatarPaths, LoadImageFile, e.NewSize.Width);
    }

    private void RecentPhotosPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width == e.PreviousSize.Width) return;
        RefreshRecentPhotosUI();
    }

    /// <summary>Scans the screenshot watch folder directly (not a persisted
    /// list) -- "recent background photos" should always reflect what's
    /// actually sitting in that folder right now, not a history that could
    /// point at files the user has since moved or deleted. The scan itself
    /// (recursive EnumerateFiles + a GetLastWriteTimeUtc stat per file) runs
    /// on a background thread: a VRChat screenshot folder can accumulate
    /// thousands of files over time, and doing this synchronously on the UI
    /// thread every time Composite mode opens (or this row resizes) was
    /// blocking the whole window, before ShowComposite's own loading spinner
    /// even had a chance to paint. _recentPhotosScanToken discards a result
    /// that arrives after a newer scan has already been kicked off (e.g. two
    /// resizes in quick succession), so a stale, slower scan can't stomp on
    /// a faster, newer one's result.</summary>
    private int _recentPhotosScanToken;

    private async void RefreshRecentPhotosUI()
    {
        int token = ++_recentPhotosScanToken;
        var folder = _screenshotWatcher.ActiveFolder;
        double availableWidth = RecentPhotosPanel.ActualWidth;

        var recent = await Task.Run(() =>
        {
            try
            {
                return Directory.Exists(folder)
                    ? Directory.EnumerateFiles(folder, "*.png", SearchOption.AllDirectories)
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .Take(MaxRecentPhotoScan)
                        .ToList()
                    : new List<string>();
            }
            catch (IOException)
            {
                return new List<string>();
            }
            catch (UnauthorizedAccessException)
            {
                return new List<string>();
            }
        });

        if (token != _recentPhotosScanToken) return;
        PopulateThumbnailRow(RecentPhotosPanel, recent, LoadPhotoForComposite, availableWidth);
    }

    /// <summary>How many RecentThumbnailSize-wide buttons (plus their own
    /// trailing RecentThumbnailSpacing) fit within <paramref name="availableWidth"/>,
    /// at least 1 so a not-yet-laid-out row (ActualWidth still 0) doesn't
    /// render completely empty.</summary>
    private static int CalculateRecentThumbnailFitCount(double availableWidth)
    {
        int slot = RecentThumbnailSize + RecentThumbnailSpacing;
        return Math.Max(1, (int)((availableWidth + RecentThumbnailSpacing) / slot));
    }

    private static void PopulateThumbnailRow(Panel host, IReadOnlyList<string> paths, Action<string> onPick, double availableWidth)
    {
        host.Children.Clear();
        int fitCount = CalculateRecentThumbnailFitCount(availableWidth);
        foreach (var path in paths.Take(fitCount))
        {
            host.Children.Add(CreateThumbnailButton(path, () => onPick(path)));
        }
    }

    /// <summary>Decoded thumbnails keyed by path, so widening/narrowing the
    /// window (which re-populates every visible recent-thumbnail row via
    /// PopulateThumbnailRow -- see the *_SizeChanged handlers, which fire
    /// repeatedly during a live resize drag) reuses the already-decoded
    /// BitmapImage instead of re-reading and re-decoding the same PNG/JPEG
    /// from disk on every tick. Capped and FIFO-evicted rather than left
    /// unbounded -- RecentPhotosPanel's own source (the screenshot watch
    /// folder) can hold thousands of files over a long session, even though
    /// only ~20 of them are ever "recent" at once, so the cache shouldn't
    /// grow to match the whole folder's history over time.</summary>
    private static readonly Dictionary<string, BitmapImage> ThumbnailCache = new();
    private static readonly Queue<string> ThumbnailCacheOrder = new();
    private const int MaxThumbnailCacheEntries = 300;

    private static BitmapImage? GetOrDecodeThumbnail(string path)
    {
        if (ThumbnailCache.TryGetValue(path, out var cached)) return cached;
        try
        {
            var thumb = new BitmapImage();
            thumb.BeginInit();
            thumb.CacheOption = BitmapCacheOption.OnLoad;
            thumb.DecodePixelWidth = RecentThumbnailSize * 2;
            thumb.UriSource = new Uri(path);
            thumb.EndInit();
            thumb.Freeze();

            ThumbnailCache[path] = thumb;
            ThumbnailCacheOrder.Enqueue(path);
            if (ThumbnailCacheOrder.Count > MaxThumbnailCacheEntries)
            {
                ThumbnailCache.Remove(ThumbnailCacheOrder.Dequeue());
            }
            return thumb;
        }
        catch (NotSupportedException) { return null; }
        catch (IOException) { return null; }
    }

    private static Button CreateThumbnailButton(string path, Action onClick)
    {
        var border = new Border
        {
            Width = RecentThumbnailSize, Height = RecentThumbnailSize,
            CornerRadius = new CornerRadius(6), ClipToBounds = true,
            Background = Brushes.White,
        };
        if (GetOrDecodeThumbnail(path) is { } thumb)
        {
            border.Child = new Image { Source = thumb, Stretch = Stretch.UniformToFill };
        }

        var button = new Button
        {
            Content = border,
            Width = RecentThumbnailSize, Height = RecentThumbnailSize,
            Padding = new Thickness(0), BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = Path.GetFileName(path),
        };
        button.Click += (_, _) => onClick();
        return button;
    }
}
