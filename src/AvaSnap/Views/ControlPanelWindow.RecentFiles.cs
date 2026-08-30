using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AvaSnap.Views;

// ---- 最近のアバター / 最近の写真: ファイルダイアログボタンの横の小さなサムネイル
//      クイックピック行。少し前に使ったアバターや背景写真へ、毎回エクスプローラーを
//      開かずに戻れる。両者は下の同じサムネボタンビルダーを共有。ウィンドウ遷移や
//      合成レンダーとは独立した関心事なので別ファイルにしてある。 ----
public partial class ControlPanelWindow
{
    private const int RecentThumbnailSize = 34;
    private const int RecentThumbnailSpacing = 6; // CreateThumbnailButton の右 Margin と一致

    /// <summary>実際にメモリ保持/永続化するパス数。どの行が表示できる数よりわざと
    /// 多くしてある(あとでウィンドウを広げれば、追加で覚え直さずにサムネが増える)。</summary>
    private const int MaxRecentAvatarHistory = 20;
    private const int MaxRecentPhotoScan = 20;
    private List<string> _recentAvatarPaths = new();

    /// <summary>終了時に App.xaml.cs が読んで他の設定と一緒に永続化する。起動時に
    /// <see cref="SetRecentAvatarPaths"/> で1回セット。</summary>
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

    /// <summary>1つの最近サムネ行を現在の幅で再構築する。各行の SizeChanged に配線
    /// してあり、ウィンドウ幅に応じて固定数ではなく空きぶんだけサムネを出す。</summary>
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

    /// <summary>スクショ監視フォルダを直接スキャンする(永続リストではない)── 「最近の
    /// 背景写真」は今そのフォルダに実在するものを常に映すべきで、移動/削除済みかも
    /// しれない履歴ではない。スキャン(再帰 EnumerateFiles + ファイルごとの
    /// GetLastWriteTimeUtc)はバックグラウンドスレッドで走る(VRChat のスクショ
    /// フォルダは数千ファイルに膨れ得るので、UI スレッドで同期に走らせると窓ごと固まる)。
    /// _recentPhotosScanToken で、新しいスキャン開始後に届いた古い結果を捨てる。</summary>
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

    /// <summary><paramref name="availableWidth"/> に収まる RecentThumbnailSize 幅
    /// (+ 末尾の RecentThumbnailSpacing)のボタン数。最低 1(未レイアウトで
    /// ActualWidth が 0 でも空にならないように)。</summary>
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

    /// <summary>デコード済みサムネのパス別キャッシュ。ウィンドウのリサイズ中に行が
    /// 何度も再構築されても、同じ PNG/JPEG を毎回読み直さず既デコードの BitmapImage を
    /// 使い回す。上限つき FIFO 破棄(監視フォルダは長時間で数千ファイルになり得るが
    /// 「最近」は常時 ~20 なので、キャッシュがフォルダ全履歴まで育たないように)。</summary>
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
