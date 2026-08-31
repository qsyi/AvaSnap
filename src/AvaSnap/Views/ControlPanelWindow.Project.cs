using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AvaSnap.Services;

namespace AvaSnap.Views;

// ---- プロジェクト保存: レタッチモードの作業状態を1つの .avasnap(JSON)へ書き出す。
//      画像はパス参照のみ。手動「保存」は無く、編集のたびに現在のプロジェクトへ
//      デバウンス自動保存し、モードを離れる時・終了時にも確定する。「新規プロジェクト」で
//      日時名の新ファイルへ切り替え(過去ファイルはディスクに残る)。起動時は常に新規。 ----
public partial class ControlPanelWindow
{
    private string _currentProjectPath = ProjectService.NewProjectPath();
    private bool _projectDirty;
    private DispatcherTimer? _projectSaveTimer;
    private CompositeSnapshot? _pristineComposite; // 起動直後の既定値。新規プロジェクトのリセットに使う

    private void InitProjectAutoSave()
    {
        _pristineComposite = CaptureCompositeSnapshot();
        _projectSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _projectSaveTimer.Tick += (_, _) => SaveCurrentProject();
        UpdateProjectNameUi();
    }

    /// <summary>編集が起きたことを記録し、自動保存を(デバウンスして)予約する。
    /// レタッチモード表示中のみ ── ScheduleCompositeRender 等から呼ばれる。</summary>
    private void MarkProjectDirty()
    {
        _projectDirty = true;
        _projectSaveTimer?.Stop();
        _projectSaveTimer?.Start();
    }

    /// <summary>保留中の変更を現在のプロジェクトファイルへ確定する。</summary>
    public void SaveCurrentProject()
    {
        _projectSaveTimer?.Stop();
        if (!_projectDirty) return;
        _projectDirty = false;
        ProjectService.Save(BuildProjectDto(), _currentProjectPath);
    }

    private void NewProjectButton_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentProject();       // 現在のを確定してから(確認ダイアログは無し)
        _currentProjectPath = ProjectService.NewProjectPath();
        ClearRetouchState();
        _projectDirty = true;
        SaveCurrentProject();       // 空プロジェクトを即ファイル化
        UpdateProjectNameUi();
    }

    private void UpdateProjectNameUi() =>
        ProjectNameText.Text = Path.GetFileNameWithoutExtension(_currentProjectPath);

    /// <summary>新規プロジェクト用のリセット: 背景写真・アバター画像・デカール・マスク・
    /// レタッチパラメータを全て既定へ戻し、まっさらな状態にする。アバターはモード間で
    /// 共有の1枚なので位置合わせモードも空になる。</summary>
    private void ClearRetouchState()
    {
        _photoPixelBuffer = null;
        _photoPath = null;
        _photoRotationQuarters = 0;
        PhotoPathText.Text = "(背景写真未選択)";

        _overlayWindow.ClearImage();
        _state.BeginBatch();
        ResetAvatarLookFields();
        _state.RotationDegrees = 0;
        _state.EndBatch();
        RefreshFromState();

        _isBlankCanvasActive = false;
        BlankCanvasColorPanel.Visibility = Visibility.Collapsed;
        RefreshBlankCanvasActiveUI();

        _compositeSkipAvatar = false;
        RefreshSkipAvatarUI();

        _decalLayerOrder.RemoveAll(l => l is not null);
        ExitDecalPlacementMode();
        RebuildDecalStrip();
        ClearMasks();

        if (_pristineComposite is { } d) ApplyCompositeSnapshot(d with { PhotoBuffer = null });
        _compositePlacementInitialized = false;
        ClearCompositeSaveStatus();
        _undo.Clear();
        _cropModeEntrySnapshot = null;
        _avatarPlacementModeEntrySnapshot = null;
        _ = RenderCompositePreview();
    }

    private ProjectDto BuildProjectDto()
    {
        var s = CaptureCompositeSnapshot();
        var dto = new ProjectDto
        {
            PhotoPath = _photoPath,
            PhotoRotationQuarters = _photoRotationQuarters,
            SkipAvatar = _compositeSkipAvatar,
            BlankCanvas = s.BlankCanvas,
            AvatarPath = _state.ImagePath,
            AvatarLook = new AvatarLookDto(
                _state.EdgeBlurRadius,
                _state.Brightness, _state.Contrast, _state.Saturation, _state.Vibrance,
                _state.Temperature, _state.Tint, _state.Hue,
                _state.Highlights, _state.Shadows, _state.Whites, _state.Blacks,
                _state.ColorTintStrength, _state.ColorTintR, _state.ColorTintG, _state.ColorTintB,
                _state.RotationDegrees),
            PhotoLook = s.PhotoLook,
            Finish = s.Finish,
            DropShadow = s.DropShadow,
            CanvasCrop = s.CanvasCrop,
            Placement = s.Placement,
            Masks = s.Masks,
            SplitCount = _splitCount,
            SplitGapPx = _splitGapPx,
        };
        foreach (var l in _decalLayerOrder)
        {
            dto.Decals.Add(l is null
                ? new DecalDto(true, null, 0, 0, 0, 0, 0, false, 0, 0, 0, 0, 1)
                : new DecalDto(false, l.IsFrame ? null : l.SourcePath,
                    l.X, l.Y, l.Width, l.Height, l.Rotation,
                    l.IsFrame, l.ShapeColor.R, l.ShapeColor.G, l.ShapeColor.B, l.ShapeStrokePercent, l.Opacity));
        }
        return dto;
    }

    /// <summary>プロジェクトを現在の UI へ適用する。v1 では起動時=常に新規なので
    /// 直接は呼ばれないが、round-trip 検証と将来の「最近のプロジェクト」用に用意。</summary>
    private void ApplyProjectDto(ProjectDto dto)
    {
        bool missing = false;

        // 背景写真
        if (!string.IsNullOrEmpty(dto.PhotoPath) && File.Exists(dto.PhotoPath))
        {
            if (TryLoadPhotoPixels(dto.PhotoPath!))
            {
                int q = ((dto.PhotoRotationQuarters % 4) + 4) % 4;
                for (int i = 0; i < q && _photoPixelBuffer is { } p; i++)
                    _photoPixelBuffer = ImageAdjustment.RotateClockwise90(p);
                if (_photoPixelBuffer is { } rp)
                    ImageAdjustment.PrecomputeFilmGrainNoise(rp.Width, rp.Height);
                _photoRotationQuarters = q;
            }
            else { missing = true; }
        }
        else if (!string.IsNullOrEmpty(dto.PhotoPath)) { missing = true; }

        // アバター
        if (!string.IsNullOrEmpty(dto.AvatarPath) && File.Exists(dto.AvatarPath))
        {
            try { _overlayWindow.LoadImage(dto.AvatarPath!); }
            catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or UriFormatException or ArgumentException) { missing = true; }
        }
        else if (!string.IsNullOrEmpty(dto.AvatarPath)) { missing = true; }

        if (dto.AvatarLook is { } al)
        {
            _state.BeginBatch();
            _state.EdgeBlurRadius = al.EdgeBlurRadius;
            _state.Brightness = al.Brightness; _state.Contrast = al.Contrast;
            _state.Saturation = al.Saturation; _state.Vibrance = al.Vibrance;
            _state.Temperature = al.Temperature; _state.Tint = al.Tint; _state.Hue = al.Hue;
            _state.Highlights = al.Highlights; _state.Shadows = al.Shadows;
            _state.Whites = al.Whites; _state.Blacks = al.Blacks;
            _state.ColorTintStrength = al.ColorTintStrength;
            _state.ColorTintR = al.ColorTintR; _state.ColorTintG = al.ColorTintG; _state.ColorTintB = al.ColorTintB;
            _state.RotationDegrees = al.RotationDegrees;
            _state.EndBatch();
        }

        _compositeSkipAvatar = dto.SkipAvatar;
        RefreshSkipAvatarUI();

        var cur = CaptureCompositeSnapshot();
        var emptyMasks = new CompositeMasks(
            new EquatableArray<MaskLayerSnapshot>(Array.Empty<MaskLayerSnapshot>()),
            new EquatableArray<MaskAssignment>(Array.Empty<MaskAssignment>()));
        var cs = new CompositeSnapshot(
            dto.PhotoLook ?? cur.PhotoLook,
            dto.Finish ?? cur.Finish,
            dto.DropShadow ?? cur.DropShadow,
            dto.CanvasCrop ?? cur.CanvasCrop,
            dto.Placement ?? cur.Placement,
            RebuildDecalArray(dto.Decals),
            dto.BlankCanvas ?? cur.BlankCanvas,
            dto.Masks ?? emptyMasks,
            _photoPixelBuffer);
        ApplyCompositeSnapshot(cs);

        _splitCount = Math.Clamp(dto.SplitCount, 1, MaxSplitCount);
        _splitGapPx = Math.Clamp(dto.SplitGapPx, 0, MaxSplitGapPx);
        _suppressEventsDepth++;
        (_splitCount switch { 2 => Split2, 3 => Split3, 4 => Split4, _ => Split1 }).IsChecked = true;
        SplitGapBox.Text = _splitGapPx.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _suppressEventsDepth = Math.Max(0, _suppressEventsDepth - 1);
        RefreshSplitGapRowEnabled();
        UpdateSplitGuides();

        _compositePlacementInitialized = true;
        _undo.Clear();
        _cropModeEntrySnapshot = null;
        _avatarPlacementModeEntrySnapshot = null;
        RefreshFromState();

        if (missing)
            ShowCompositeSaveStatus("プロジェクトの一部画像が見つかりませんでした。", success: false);
    }

    private EquatableArray<DecalEntrySnapshot> RebuildDecalArray(List<DecalDto> decals)
    {
        var list = new List<DecalEntrySnapshot>();
        foreach (var d in decals)
        {
            if (d.IsAvatarMarker)
            {
                list.Add(new DecalEntrySnapshot(true, null, null, 0, 0, 0, 0, 0, false, 0, 0, 0, 0, 1));
                continue;
            }
            ImageAdjustment.PixelBuffer? px = null;
            BitmapSource? thumb = null;
            if (!d.IsFrame)
            {
                if (string.IsNullOrEmpty(d.SourcePath) || !File.Exists(d.SourcePath)) continue; // 画像が無い → 捨てる
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(d.SourcePath);
                    bmp.EndInit();
                    bmp.Freeze();
                    px = ImageAdjustment.PrepareBuffer(bmp);
                    thumb = bmp;
                }
                catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or UriFormatException or ArgumentException)
                {
                    continue;
                }
            }
            list.Add(new DecalEntrySnapshot(false, px, thumb,
                d.X, d.Y, d.Width, d.Height, d.Rotation,
                d.IsFrame, d.ColorR, d.ColorG, d.ColorB, d.StrokePercent, d.Opacity));
        }
        return new EquatableArray<DecalEntrySnapshot>(list.ToArray());
    }
}
