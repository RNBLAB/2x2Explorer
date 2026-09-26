using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Line = System.Windows.Shapes.Line;
using Rectangle = System.Windows.Shapes.Rectangle;
using System.Windows.Threading;
using Explorer.App.Services.Imaging;
using Explorer.Core.Services;

namespace Explorer.App.Views.Dialogs;

/// <summary>
/// 그림 한 장을 고치는 창입니다.
///
/// 파일·클립보드·끌어다 놓기로 불러오고, 자르기·크기 조정·회전·반전을 한 뒤 다른 이름으로 저장합니다.
/// 원본 파일은 이 창이 덮어쓰지 않습니다. 저장은 늘 "다른 이름으로"입니다.
/// 더 손볼 것이 있으면 지금 상태를 그림판으로 넘기고, 그림판에서 저장하면 이 창으로 다시 받아옵니다.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class ImageEditorWindow : Window
{
    /// <summary>되돌리기에 쥐고 있을 최대 메모리입니다. 넘으면 오래된 단계부터 버립니다.</summary>
    private const long HistoryBudgetBytes = 1024L * 1024 * 1024;

    private const int HistoryMaxSteps = 30;

    /// <summary>비율 칸의 맨 앞 두 자리입니다. 그 뒤는 ImageMath.CropRatios 차례 그대로입니다.</summary>
    private const int FreeRatioIndex = 0;
    private const int OriginalRatioIndex = 1;
    private const int FixedRatioOffset = 2;

    /// <summary>기울기 슬라이더의 끝값입니다. 이보다 크게 기운 사진은 90도 회전과 함께 씁니다.</summary>
    private const double MaxTilt = 180;

    private readonly List<BitmapSource> _undo = new();
    private readonly List<BitmapSource> _redo = new();
    private readonly string? _initialFolder;

    /// <summary>불러온 원본입니다. 메타데이터와 원래 형식, 저장 대화상자의 기본 위치를 여기서 얻습니다.</summary>
    private LoadedImage? _origin;

    private BitmapSource? _current;
    private bool _dirty;

    // 자르기 영역 (픽셀 좌표). 없으면 Empty.
    private Rect _selection = Rect.Empty;
    private DragMode _drag;
    private Point _anchor;
    private Point _dragStart;
    private Rect _selectionAtDragStart;
    private readonly Rectangle[] _handles = new Rectangle[4];
    private double _scale = 1;

    // ── 확대·축소 ──
    // 맞춤(기본): Viewbox 가 칸에 맞춰 줄입니다. 손 배율: 그림 픽셀 × _zoom 크기로 못 박고 스크롤합니다.
    private const double MinZoom = 0.05;
    private const double MaxZoom = 16.0;
    private const double ZoomStep = 1.15;
    private bool _manualZoom;
    private double _zoom = 1.0;

    // 크기 칸끼리 서로 고칠 때 되울림을 막습니다.
    private bool _syncingSize;

    // 비율을 가로세로 뒤집어 쓰는지입니다.
    private bool _ratioSwapped;

    // 아직 픽셀에 반영하지 않은 기울기(시계 방향 양수)입니다. 화면에서만 돌려 보입니다.
    private double _tilt;
    private bool _syncingTilt;

    // 수평맞추기
    private bool _straightenMode;
    private Point _lineStart;
    private readonly Line _straightenLine = new()
    {
        Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0x3F)),
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed
    };

    // 안내선: (수평인지, 그림 크기에 대한 자리 0~1). 그림이 바뀌어도 같은 자리에 남습니다.
    private readonly List<(bool Horizontal, double Position)> _guides = new();
    private readonly List<Line> _guideLines = new();
    private int _draggingGuide = -1;

    // 그림판 연동
    private FileSystemWatcher? _paintWatcher;
    private string? _paintFile;
    private DispatcherTimer? _paintDebounce;

    private enum DragMode
    {
        None,
        New,
        Move,
        Corner,
        Guide,
        Straighten
    }

    public ImageEditorWindow(string? initialFolder)
    {
        InitializeComponent();
        PopupTitleBar.Apply(this, fillScreen: true);

        _initialFolder = initialFolder is not null && Directory.Exists(initialFolder) ? initialFolder : null;

        RatioBox.Items.Add("자유");
        RatioBox.Items.Add("원본 비율");

        foreach (var ratio in ImageMath.CropRatios)
        {
            RatioBox.Items.Add(ratio.Label);
        }

        RatioBox.SelectedIndex = 0;

        for (var i = 0; i < _handles.Length; i++)
        {
            _handles[i] = new Rectangle
            {
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF)),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };

            Overlay.Children.Add(_handles[i]);
        }

        Overlay.Children.Add(_straightenLine);

        RefreshState();
        CleanOldPaintFiles();

        // 오른쪽 탐색기: 그림만 보이고, 두 번 누르면 이 창에 엽니다.
        SideExplorer.FileFilter = ImageMath.IsEditable;
        SideExplorer.FilesActivated += paths =>
        {
            if (paths.Count > 0)
            {
                OpenFile(paths[0]);
            }
        };
        SideExplorer.StatusMessage += Status;
        SideExplorer.Start(_initialFolder);
        TitleBar.EnableFavorites(SideExplorer.OpenFavorite);
    }

    /// <summary>
    /// 편집 창을 엽니다. 고른 것 가운데 첫 그림 파일을 열고, 없으면 클립보드의 그림을, 그것도 없으면 빈 창을 엽니다.
    /// 창은 따로 떠 있으므로 편집하는 동안에도 탐색기를 쓸 수 있습니다.
    /// </summary>
    public static void ShowFor(Window? owner, IReadOnlyList<string> selected, string? currentFolder)
    {
        var folder = !string.IsNullOrEmpty(currentFolder) && !SpecialPaths.IsVirtual(currentFolder) ? currentFolder : null;
        var window = new ImageEditorWindow(folder) { Owner = owner };

        window.Show();

        string? file = null;

        foreach (var path in selected)
        {
            if (ImageMath.IsEditable(path) && File.Exists(path))
            {
                file = path;
                break;
            }
        }

        if (file is not null)
        {
            window.OpenFile(file);
        }
        else if (ClipboardImage.HasImage())
        {
            window.PasteFromClipboard();
        }
    }

    // ── 불러오기 ──

    private void OpenFile(string path)
    {
        if (!ConfirmDiscard())
        {
            return;
        }

        try
        {
            using var _ = new WaitCursor();
            Load(ImageCodec.Load(path), Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException || ImageCodec.IsCodecError(ex))
        {
            MessageBox.Show(this, $"그림을 열지 못했습니다.{Environment.NewLine}{path}{Environment.NewLine}{ex.Message}",
                "그림 편집", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PasteFromClipboard()
    {
        if (!ConfirmDiscard())
        {
            return;
        }

        try
        {
            using var _ = new WaitCursor();
            var image = ClipboardImage.Read(out var how);

            if (image is null)
            {
                Status(how);
                return;
            }

            Load(image, how);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException || ImageCodec.IsCodecError(ex))
        {
            MessageBox.Show(this, $"클립보드의 그림을 읽지 못했습니다.{Environment.NewLine}{ex.Message}",
                "그림 편집", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Load(LoadedImage image, string from)
    {
        StopWatchingPaint();

        _origin = image;
        _undo.Clear();
        ResetZoom(apply: false);
        _redo.Clear();
        _dirty = false;

        Display(image.Pixels);

        var note = image.Orientation != 1 ? " · 사진 방향을 바로잡아 열었습니다" : string.Empty;
        Status($"{from} — {ImageCodec.Describe(image.Pixels)}{note}");
    }

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "편집할 그림 고르기",
            Filter = ImageCodec.OpenFilter,
            InitialDirectory = SourceFolder() ?? string.Empty
        };

        if (dialog.ShowDialog(this) == true)
        {
            OpenFile(dialog.FileName);
        }
    }

    private void OnPaste(object sender, RoutedEventArgs e) => PasteFromClipboard();

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        // 오른쪽 탐색기 위에 놓는 것은 받지 않습니다. 탐색기에서 끌어 온 것을 제자리에 놓는 경우입니다.
        if (e.OriginalSource is DependencyObject over && SideExplorer.IsAncestorOf(over))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFileDropped(object sender, DragEventArgs e)
    {
        // 오른쪽 탐색기 위에 놓는 것은 받지 않습니다. 탐색기에서 끌어 온 것을 제자리에 놓는 경우입니다.
        if (e.OriginalSource is DependencyObject over && SideExplorer.IsAncestorOf(over))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Handled = true;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return;
        }

        foreach (var file in files)
        {
            if (ImageMath.IsEditable(file))
            {
                OpenFile(file);
                return;
            }
        }

        Status("그림 파일이 아닙니다.");
    }

    // ── 편집 ──

    /// <summary>
    /// 편집 한 단계를 적용합니다. 지금 그림을 되돌리기 목록에 넣고 새 그림을 보입니다.
    /// 슬라이더로 기울여 두고 아직 적용하지 않은 각도가 있으면 그것부터 픽셀에 반영합니다.
    /// </summary>
    private void Apply(Func<BitmapSource, BitmapSource> edit, string what)
    {
        CommitTilt();
        ApplyNow(edit, what);
    }

    private void ApplyNow(Func<BitmapSource, BitmapSource> edit, string what)
    {
        if (_current is null)
        {
            return;
        }

        try
        {
            using var _ = new WaitCursor();
            var before = ImageCodec.Describe(_current);
            var result = edit(_current);

            Push(_undo, _current);
            _redo.Clear();
            _dirty = true;

            Display(result);
            Status($"{what}: {before} → {ImageCodec.Describe(result)}");
        }
        catch (Exception ex) when (ImageCodec.IsCodecError(ex) || ex is OutOfMemoryException)
        {
            MessageBox.Show(this, $"{what}에 실패했습니다.{Environment.NewLine}{ex.Message}", "그림 편집",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnRotateLeft(object sender, RoutedEventArgs e) => RotateBy(-ReadRotateStep());

    private void OnRotateRight(object sender, RoutedEventArgs e) => RotateBy(ReadRotateStep());

    /// <summary>↺/↻ 단추가 한 번에 돌릴 각도입니다(1~360, 기본 90). 잘못 넣었으면 90으로 되돌립니다.</summary>
    private double ReadRotateStep()
    {
        if (double.TryParse(RotateStepBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var step)
            && step > 0
            && step <= 360)
        {
            return Math.Round(step, 2);
        }

        RotateStepBox.Text = "90";
        return 90;
    }

    /// <summary>
    /// 시계 방향으로 그만큼 돌립니다(음수는 왼쪽).
    /// 90의 배수이고 걸려 있는 기울기가 없으면 곧바로 무손실로 돌립니다.
    /// 그 밖의 각도는 기울기 미리보기에 더하고, [적용]·Enter 로 확정합니다.
    /// </summary>
    private void RotateBy(double degrees)
    {
        if (_current is null)
        {
            return;
        }

        var quarter = Math.Abs(degrees % 90) < 0.005;

        if (quarter && Math.Abs(_tilt) < 0.005)
        {
            var turns = (int)Math.Round(degrees);
            var what = turns < 0 ? $"왼쪽으로 {-turns}° 돌림" : $"오른쪽으로 {turns}° 돌림";
            Apply(p => ImageOps.Rotate(p, turns), what);
            return;
        }

        SetTilt(WrapAngle(_tilt + degrees));
        Status($"기울기 {_tilt.ToString("0.##", CultureInfo.InvariantCulture)}° 미리보기입니다. [적용]이나 Enter 로 확정합니다.");
    }

    /// <summary>각도를 -180 초과 ~ 180 이하로 되감습니다.</summary>
    private static double WrapAngle(double degrees)
    {
        var wrapped = ((degrees + 180) % 360 + 360) % 360 - 180;
        return wrapped <= -180 + 1e-9 ? 180 : wrapped;
    }

    /// <summary>
    /// 기울기를 90° 단위 회전(무손실)과 나머지(-45~45, 빈 귀퉁이를 잘라 냄)로 나눕니다.
    /// 예: 100° = 90° + 10°, -170° = -180° + 10°.
    /// </summary>
    private static (int QuarterDegrees, double Residual) SplitTilt(double tilt)
    {
        var quarters = (int)Math.Round(tilt / 90.0, MidpointRounding.AwayFromZero);
        var residual = Math.Round(tilt - (quarters * 90.0), 2);
        return ((((quarters % 4) + 4) % 4) * 90, residual);
    }

    /// <summary>90° 단위 회전 때문에 가로세로가 바뀌어 보이는 중인지입니다.</summary>
    private bool ViewSwapped => _current is not null && SplitTilt(_tilt).QuarterDegrees % 180 != 0;

    /// <summary>지금 화면에 보이는 그림의 크기입니다. 90°·270° 미리보기 중에는 가로세로가 바뀝니다.</summary>
    private int ViewWidth => _current is null ? 0 : ViewSwapped ? _current.PixelHeight : _current.PixelWidth;

    private int ViewHeight => _current is null ? 0 : ViewSwapped ? _current.PixelWidth : _current.PixelHeight;

    private void OnFlipHorizontal(object sender, RoutedEventArgs e) => Apply(p => ImageOps.Flip(p, horizontal: true), "좌우 반전");

    private void OnFlipVertical(object sender, RoutedEventArgs e) => Apply(p => ImageOps.Flip(p, horizontal: false), "상하 반전");

    private void OnCrop(object sender, RoutedEventArgs e) => CropToSelection();

    private void CropToSelection()
    {
        if (_current is null || _selection.IsEmpty || _selection.Width < 1 || _selection.Height < 1)
        {
            Status("그림 위를 끌어 자를 영역을 먼저 잡으십시오.");
            return;
        }

        var rect = new Int32Rect(
            (int)Math.Round(_selection.X),
            (int)Math.Round(_selection.Y),
            Math.Max(1, (int)Math.Round(_selection.Width)),
            Math.Max(1, (int)Math.Round(_selection.Height)));

        Apply(p => ImageOps.Crop(p, rect), "자르기");
    }

    private void OnResize(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            return;
        }

        if (!TryReadPixels(WidthBox.Text, out var width) || !TryReadPixels(HeightBox.Text, out var height))
        {
            Status("가로와 세로에 1~30000 사이의 수를 넣으십시오.");
            return;
        }

        if (width == _current.PixelWidth && height == _current.PixelHeight)
        {
            Status("지금 크기와 같습니다.");
            return;
        }

        Apply(p => ImageOps.Resize(p, width, height), "크기 조정");
    }

    private void OnWidthChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingSize || _current is null || !TryReadPixels(WidthBox.Text, out var width))
        {
            return;
        }

        var percent = width * 100.0 / _current.PixelWidth;

        if (KeepRatioBox.IsChecked == true)
        {
            SetSizeBoxes(null, Math.Max(1, (int)Math.Round(width * (double)_current.PixelHeight / _current.PixelWidth)), percent);
        }
        else
        {
            SetSizeBoxes(null, null, double.NaN);
        }
    }

    private void OnHeightChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingSize || _current is null || !TryReadPixels(HeightBox.Text, out var height))
        {
            return;
        }

        if (KeepRatioBox.IsChecked == true)
        {
            var width = Math.Max(1, (int)Math.Round(height * (double)_current.PixelWidth / _current.PixelHeight));
            SetSizeBoxes(width, null, height * 100.0 / _current.PixelHeight);
        }
        else
        {
            SetSizeBoxes(null, null, double.NaN);
        }
    }

    /// <summary>퍼센트를 넣으면 지금 크기에 곱해 가로세로를 채웁니다. 비율 유지와 관계없이 두 변을 같은 배율로 바꿉니다.</summary>
    private void OnPercentChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingSize || _current is null)
        {
            return;
        }

        if (!double.TryParse(PercentBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
            || percent is < 0.1 or > 1000)
        {
            return;
        }

        var width = Math.Clamp((int)Math.Round(_current.PixelWidth * percent / 100), 1, 30000);
        var height = Math.Clamp((int)Math.Round(_current.PixelHeight * percent / 100), 1, 30000);
        SetSizeBoxes(width, height, null);
    }

    /// <summary>크기 칸을 채웁니다. 퍼센트가 null 이면 퍼센트 칸은 두고, NaN 이면 비웁니다.</summary>
    private void SetSizeBoxes(int? width, int? height, double? percent)
    {
        _syncingSize = true;

        try
        {
            if (width is { } w)
            {
                WidthBox.Text = w.ToString(CultureInfo.InvariantCulture);
            }

            if (height is { } h)
            {
                HeightBox.Text = h.ToString(CultureInfo.InvariantCulture);
            }

            if (percent is { } p)
            {
                PercentBox.Text = double.IsNaN(p) ? string.Empty : p.ToString("0.#", CultureInfo.InvariantCulture);
            }
        }
        finally
        {
            _syncingSize = false;
        }
    }

    private static bool TryReadPixels(string text, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value is >= 1 and <= 30000;

    // ── 되돌리기 ──

    private void OnUndo(object sender, RoutedEventArgs e) => Undo();

    private void OnRedo(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        // 적용하지 않은 기울기는 되돌리기 한 번에 취소합니다.
        if (Math.Abs(_tilt) >= 0.005)
        {
            SetTilt(0);
            Status("기울기를 취소했습니다.");
            return;
        }

        if (_undo.Count == 0 || _current is null)
        {
            return;
        }

        Push(_redo, _current);
        var previous = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _dirty = true;

        Display(previous);
        Status($"되돌렸습니다 — {ImageCodec.Describe(previous)}");
    }

    private void Redo()
    {
        CommitTilt();

        if (_redo.Count == 0 || _current is null)
        {
            return;
        }

        Push(_undo, _current);
        var next = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _dirty = true;

        Display(next);
        Status($"다시 했습니다 — {ImageCodec.Describe(next)}");
    }

    /// <summary>목록에 넣고, 단계 수나 메모리가 넘치면 오래된 것부터 버립니다.</summary>
    private void Push(List<BitmapSource> stack, BitmapSource bitmap)
    {
        stack.Add(bitmap);

        long total = 0;

        foreach (var item in _undo)
        {
            total += Bytes(item);
        }

        foreach (var item in _redo)
        {
            total += Bytes(item);
        }

        while (stack.Count > 1 && (stack.Count > HistoryMaxSteps || total > HistoryBudgetBytes))
        {
            total -= Bytes(stack[0]);
            stack.RemoveAt(0);
        }
    }

    private static long Bytes(BitmapSource bitmap) =>
        (long)bitmap.PixelWidth * bitmap.PixelHeight * Math.Max(1, bitmap.Format.BitsPerPixel / 8);

    // ── 저장 ──

    private void OnSaveAs(object sender, RoutedEventArgs e) => SaveAs();

    private void SaveAs()
    {
        CommitTilt();

        if (_current is null)
        {
            return;
        }

        if (!int.TryParse(QualityBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var quality)
            || quality is < 1 or > 100)
        {
            Status("JPEG 품질은 1~100 사이로 넣으십시오.");
            QualityBox.Focus();
            return;
        }

        var sourcePath = _origin?.SourcePath;
        var baseName = sourcePath is not null
            ? Path.GetFileNameWithoutExtension(sourcePath) + "_편집"
            : "클립보드_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

        // 원래 형식을 먼저 권합니다. 클립보드 그림은 손실 없는 PNG를 권합니다.
        var filterIndex = (_origin?.Container ?? ImageContainer.Other) switch
        {
            ImageContainer.Jpeg => 1,
            ImageContainer.Bmp => 3,
            ImageContainer.Tiff => 4,
            ImageContainer.Gif => 5,
            _ => 2
        };

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "다른 이름으로 저장",
            Filter = ImageCodec.SaveFilter,
            FilterIndex = filterIndex,
            FileName = baseName,
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = SourceFolder() ?? string.Empty
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var path = dialog.FileName;

        if (!ImageCodec.CanWrite(ImageCodec.ContainerOf(path)))
        {
            path += dialog.FilterIndex switch
            {
                1 => ".jpg",
                3 => ".bmp",
                4 => ".tif",
                5 => ".gif",
                _ => ".png"
            };
        }

        if (sourcePath is not null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase)
            && MessageBox.Show(this, "원본 파일에 덮어씁니다. 되돌릴 수 없습니다. 계속할까요?", "다른 이름으로 저장",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            using var _ = new WaitCursor();

            var notes = ImageCodec.Save(_current, path, new ImageSaveOptions
            {
                JpegQuality = quality,
                SourceMetadata = _origin?.Metadata,
                SourceContainer = _origin?.Container ?? ImageContainer.Other,
                Policy = MetadataPolicy.Keep
            });

            _dirty = false;

            var message = $"저장했습니다: {path}";

            if (notes.Count > 0)
            {
                message += " — " + string.Join(" ", notes);
            }

            Status(message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException || ImageCodec.IsCodecError(ex))
        {
            MessageBox.Show(this, $"저장하지 못했습니다.{Environment.NewLine}{path}{Environment.NewLine}{ex.Message}",
                "그림 편집", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ── 그림판 ──

    /// <summary>
    /// 지금 상태를 임시 PNG로 저장하고 그림판으로 엽니다.
    /// 그 파일을 지켜보다가 그림판에서 저장하면 편집 한 단계로 받아옵니다.
    /// </summary>
    private void OnOpenInPaint(object sender, RoutedEventArgs e)
    {
        CommitTilt();

        if (_current is null)
        {
            return;
        }

        try
        {
            StopWatchingPaint();

            var folder = PaintFolder();
            Directory.CreateDirectory(folder);

            var file = Path.Combine(folder, "편집-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".png");
            ImageCodec.Save(_current, file, new ImageSaveOptions());

            // 그림판을 여는 이름은 윈도우 10·11 모두 mspaint 입니다. 11의 새 그림판도 이 이름으로 열립니다.
            Process.Start(new ProcessStartInfo("mspaint.exe", "\"" + file + "\"") { UseShellExecute = true });

            WatchPaint(file);
            Status("그림판으로 열었습니다. 그림판에서 Ctrl+S 로 저장하면 이 창으로 돌아옵니다.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception || ImageCodec.IsCodecError(ex))
        {
            MessageBox.Show(this, $"그림판을 열지 못했습니다.{Environment.NewLine}{ex.Message}", "그림 편집",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void WatchPaint(string file)
    {
        _paintFile = file;

        _paintDebounce = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(700)
        };

        _paintDebounce.Tick += (_, _) =>
        {
            _paintDebounce?.Stop();
            TakeBackFromPaint();
        };

        _paintWatcher = new FileSystemWatcher(Path.GetDirectoryName(file)!, Path.GetFileName(file))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };

        // 그림판은 임시 파일에 쓴 뒤 이름을 바꿔 저장하기도 합니다. 바뀜·만듦·이름 바꿈을 모두 받습니다.
        FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _paintDebounce?.Stop();
            _paintDebounce?.Start();
        });

        _paintWatcher.Changed += changed;
        _paintWatcher.Created += changed;
        _paintWatcher.Renamed += (source, args) => changed(source, args);
    }

    private void TakeBackFromPaint()
    {
        if (_paintFile is null || !File.Exists(_paintFile))
        {
            return;
        }

        // 그림판이 아직 쓰는 중이면 열리지 않습니다. 잠깐씩 기다리며 몇 번 더 봅니다.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var image = ImageCodec.Load(_paintFile);

                if (_current is not null && SamePixels(_current, image.Pixels))
                {
                    return;
                }

                Apply(_ => image.Pixels, "그림판에서 받아옴");
                return;
            }
            catch (IOException)
            {
                System.Threading.Thread.Sleep(200);
            }
            catch (Exception ex) when (ImageCodec.IsCodecError(ex))
            {
                System.Threading.Thread.Sleep(200);
            }
        }

        Status("그림판에서 저장한 파일을 읽지 못했습니다. 그림판에서 한 번 더 저장해 보십시오.");
    }

    /// <summary>크기와 픽셀이 같은지 봅니다. 그림판이 저장만 하고 고친 것이 없으면 단계를 늘리지 않으려는 것입니다.</summary>
    private static bool SamePixels(BitmapSource a, BitmapSource b)
    {
        if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight || a.Format != b.Format)
        {
            return false;
        }

        var stride = ((a.PixelWidth * a.Format.BitsPerPixel) + 31) / 32 * 4;
        var left = new byte[stride * a.PixelHeight];
        var right = new byte[stride * b.PixelHeight];
        a.CopyPixels(left, stride, 0);
        b.CopyPixels(right, stride, 0);

        return left.AsSpan().SequenceEqual(right);
    }

    private void StopWatchingPaint()
    {
        _paintDebounce?.Stop();
        _paintDebounce = null;

        if (_paintWatcher is not null)
        {
            _paintWatcher.EnableRaisingEvents = false;
            _paintWatcher.Dispose();
            _paintWatcher = null;
        }

        _paintFile = null;
    }

    private static string PaintFolder() => Path.Combine(Path.GetTempPath(), "2x2Explorer", "paint");

    /// <summary>하루 넘은 그림판 임시 파일을 치웁니다. 지우지 못해도 넘어갑니다.</summary>
    private static void CleanOldPaintFiles()
    {
        try
        {
            var folder = PaintFolder();

            if (!Directory.Exists(folder))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "편집-*.png"))
            {
                if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-1))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write($"그림판 임시 파일을 치우지 못했습니다: {ex.Message}");
        }
    }

    // ── 화면 ──

    private void Display(BitmapSource bitmap)
    {
        _current = bitmap;

        Picture.Source = bitmap;
        Surface.Width = bitmap.PixelWidth;
        Surface.Height = bitmap.PixelHeight;
        Overlay.Width = bitmap.PixelWidth;
        Overlay.Height = bitmap.PixelHeight;
        ApplyZoomLayout();

        ClearSelection();
        SetTilt(0);
        SetSizeBoxes(bitmap.PixelWidth, bitmap.PixelHeight, 100);

        // 비율을 골라 둔 채 새 그림이 오면 그 비율로 다시 잡아 줍니다.
        if (RatioBox.SelectedIndex > FreeRatioIndex)
        {
            SelectRatio();
        }

        RefreshState();
        UpdateScale();
        DrawGrid();
        DrawGuides();
    }

    private void RefreshState()
    {
        var has = _current is not null;

        Placeholder.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        Stage.Visibility = has ? Visibility.Visible : Visibility.Collapsed;

        UndoButton.IsEnabled = _undo.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;

        foreach (var button in new[] { RotateLeftButton, RotateRightButton, FlipHorizontalButton, FlipVerticalButton, PaintButton, SaveButton, ResizeButton, SwapRatioButton, StraightenButton, AddHorizontalGuideButton, AddVerticalGuideButton, ResetTiltButton })
        {
            button.IsEnabled = has;
        }

        CropButton.IsEnabled = has && !_selection.IsEmpty;
        ClearSelectionButton.IsEnabled = !_selection.IsEmpty;
        TiltSlider.IsEnabled = has;
        TiltBox.IsEnabled = has;
        ApplyTiltButton.IsEnabled = has && Math.Abs(_tilt) >= 0.005;
        ClearGuidesButton.IsEnabled = _guides.Count > 0;

        var name = _origin?.SourcePath is { } path ? Path.GetFileName(path) : has ? "클립보드" : null;
        Title = name is null
            ? "그림 편집"
            : $"그림 편집 — {name}{(_dirty ? " *" : string.Empty)} ({ImageCodec.Describe(_current!)})";
    }

    private void Status(string message)
    {
        StatusLabel.Text = message;
        RefreshState();
    }

    private string? SourceFolder()
    {
        if (_origin?.SourcePath is { } path && Path.GetDirectoryName(path) is { } folder && Directory.Exists(folder)
            && !folder.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
        {
            return folder;
        }

        return _initialFolder;
    }

    /// <summary>Viewbox 가 줄인 배율입니다. 선과 손잡이를 이만큼 키워 늘 같은 굵기로 보이게 합니다.</summary>
    private void OnStageSizeChanged(object sender, SizeChangedEventArgs e) => UpdateScale();

    private void UpdateScale()
    {
        if (_current is null || Stage.ActualWidth <= 0)
        {
            return;
        }

        _scale = _manualZoom
            ? _zoom
            : Math.Min(1.0, Math.Min(Stage.ActualWidth / ViewWidth, Stage.ActualHeight / ViewHeight));

        if (_scale <= 0 || double.IsNaN(_scale))
        {
            _scale = 1;
        }

        DrawSelection();
        DrawGrid();
        DrawGuides();
        _straightenLine.StrokeThickness = 2 / _scale;
    }

    /// <summary>Ctrl+휠: 마우스가 가리키는 점을 붙든 채 확대·축소합니다. 그냥 휠은 스크롤입니다.</summary>
    private void OnStageWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || _current is null)
        {
            return;
        }

        e.Handled = true;

        // 지금 보이는 배율에서 출발해야 맞춤 상태에서 굴려도 튀지 않습니다.
        var factor = e.Delta > 0 ? ZoomStep : 1 / ZoomStep;
        var next = Math.Clamp(_scale * factor, MinZoom, MaxZoom);

        SetZoom(next, e.GetPosition(Surface), e.GetPosition(StageScroller));
    }

    /// <summary>손 배율로 바꿉니다. 붙들 점이 있으면 그 점이 마우스 아래에 남도록 스크롤을 맞춥니다.</summary>
    private void SetZoom(double zoom, Point? anchorPixel = null, Point? anchorView = null)
    {
        if (_current is null)
        {
            return;
        }

        _manualZoom = true;
        _zoom = zoom;
        ApplyZoomLayout();

        if (anchorPixel is { } pixel && anchorView is { } view)
        {
            StageScroller.UpdateLayout();
            StageScroller.ScrollToHorizontalOffset(Stage.Margin.Left + pixel.X * zoom - view.X);
            StageScroller.ScrollToVerticalOffset(Stage.Margin.Top + pixel.Y * zoom - view.Y);
        }

        UpdateScale();
    }

    /// <summary>창에 맞춤으로 돌아갑니다(Ctrl+0).</summary>
    private void ResetZoom(bool apply = true)
    {
        _manualZoom = false;
        _zoom = 1.0;

        if (apply)
        {
            ApplyZoomLayout();
            UpdateScale();
        }
    }

    /// <summary>지금 배율 방식을 Viewbox 와 스크롤 막대에 반영합니다. 그림 크기가 바뀌어도 부릅니다.</summary>
    private void ApplyZoomLayout()
    {
        if (_manualZoom && _current is not null)
        {
            Stage.Stretch = Stretch.Fill;
            Stage.StretchDirection = StretchDirection.Both;
            Stage.Width = Math.Max(1, ViewWidth * _zoom);
            Stage.Height = Math.Max(1, ViewHeight * _zoom);
            StageScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            StageScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            ZoomLabel.Text = string.Format(CultureInfo.CurrentCulture, "{0:0}%", _zoom * 100);
        }
        else
        {
            Stage.Stretch = Stretch.Uniform;
            Stage.StretchDirection = StretchDirection.DownOnly;
            Stage.Width = double.NaN;
            Stage.Height = double.NaN;
            StageScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            StageScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            StageScroller.ScrollToHome();
            ZoomLabel.Text = _current is null ? string.Empty : "창에 맞춤";
        }
    }

    // ── 자르기 영역 ──

    private void OnRatioChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_current is null)
        {
            return;
        }

        if (RatioBox.SelectedIndex > FreeRatioIndex)
        {
            // 기울여 둔 그림에 영역을 잡으면 좌표가 어긋납니다. 기울기부터 반영합니다.
            CommitTilt();
            SelectRatio();
        }
    }

    /// <summary>비율을 가로세로 뒤집습니다. 사진 규격은 세로형으로 적혀 있어 가로 사진에 쓸 때 누릅니다.</summary>
    private void OnSwapRatio(object sender, RoutedEventArgs e)
    {
        CommitTilt();
        _ratioSwapped = !_ratioSwapped;
        SwapRatioButton.Content = _ratioSwapped ? "↔ 가로세로 (바뀜)" : "↔ 가로세로";

        if (RatioBox.SelectedIndex > FreeRatioIndex)
        {
            SelectRatio();
        }

        RefreshState();
    }

    /// <summary>고른 비율로 가장 큰 영역을 가운데에 잡습니다.</summary>
    private void SelectRatio()
    {
        if (_current is null || !TryCurrentRatio(out var ratio))
        {
            return;
        }

        var (x, y, w, h) = ImageMath.CenterCrop(_current.PixelWidth, _current.PixelHeight, ratio, 1, followOrientation: false);
        _selection = new Rect(x, y, w, h);
        DrawSelection();
    }

    /// <summary>가로 ÷ 세로 비율입니다. 자유면 false 입니다.</summary>
    private bool TryCurrentRatio(out double ratio)
    {
        ratio = 0;
        var index = RatioBox.SelectedIndex;

        if (index <= FreeRatioIndex || _current is null)
        {
            return false;
        }

        if (index == OriginalRatioIndex)
        {
            ratio = OriginalRatio();
        }
        else if (index - FixedRatioOffset < ImageMath.CropRatios.Count)
        {
            var chosen = ImageMath.CropRatios[index - FixedRatioOffset];
            ratio = chosen.Width / chosen.Height;
        }

        if (_ratioSwapped && ratio > 0)
        {
            ratio = 1 / ratio;
        }

        return ratio > 0;
    }

    /// <summary>처음 불러온 그림의 비율입니다. 자르거나 돌린 뒤에도 처음 모양을 기준으로 삼습니다.</summary>
    private double OriginalRatio()
    {
        var first = _origin?.Pixels ?? _current!;
        return (double)first.PixelWidth / first.PixelHeight;
    }

    private void OnOverlayDown(object sender, MouseButtonEventArgs e)
    {
        if (_current is null)
        {
            return;
        }

        var raw = e.GetPosition(Surface);
        var guide = HitGuide(raw);

        if (guide >= 0)
        {
            _draggingGuide = guide;
            _drag = DragMode.Guide;
            Overlay.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (_straightenMode)
        {
            _lineStart = raw;
            _straightenLine.X1 = raw.X;
            _straightenLine.Y1 = raw.Y;
            _straightenLine.X2 = raw.X;
            _straightenLine.Y2 = raw.Y;
            _straightenLine.Visibility = Visibility.Visible;
            _drag = DragMode.Straighten;
            Overlay.CaptureMouse();
            e.Handled = true;
            return;
        }

        // 기울여 둔 채 자르기를 시작하면 기울기부터 픽셀에 반영합니다. 자를 영역은 반영된 그림 기준입니다.
        if (Math.Abs(_tilt) >= 0.005)
        {
            CommitTilt();

            if (_current is null)
            {
                return;
            }
        }

        var point = Clamp(e.GetPosition(Surface));
        var corner = HitCorner(point);

        if (corner >= 0)
        {
            // 누른 모서리의 맞은편을 고정점으로 삼습니다.
            _anchor = CornerPoint(_selection, 3 - corner);
            _drag = DragMode.Corner;
        }
        else if (!_selection.IsEmpty && _selection.Contains(point))
        {
            _drag = DragMode.Move;
            _dragStart = point;
            _selectionAtDragStart = _selection;
        }
        else
        {
            _anchor = point;
            _selection = new Rect(point, new Size(0, 0));
            _drag = DragMode.New;
        }

        Overlay.CaptureMouse();
        e.Handled = true;
    }

    private void OnOverlayMove(object sender, MouseEventArgs e)
    {
        if (_current is null)
        {
            return;
        }

        var raw = e.GetPosition(Surface);
        var point = Clamp(raw);

        if (_drag == DragMode.Guide && _draggingGuide >= 0 && _draggingGuide < _guides.Count)
        {
            var (horizontal, _) = _guides[_draggingGuide];
            var position = horizontal ? raw.Y / ViewHeight : raw.X / ViewWidth;
            _guides[_draggingGuide] = (horizontal, Math.Clamp(position, 0, 1));
            DrawGuides();
            return;
        }

        if (_drag == DragMode.Straighten)
        {
            _straightenLine.X2 = raw.X;
            _straightenLine.Y2 = raw.Y;

            var preview = ImageMath.StraightenAngle(_lineStart.X, _lineStart.Y, raw.X, raw.Y);
            SelectionLabel.Text = preview is { } p
                ? $"놓으면 {p.ToString("+0.00;-0.00;0", CultureInfo.InvariantCulture)}° 더 돌립니다"
                : "수평이어야 할 선을 따라 끄십시오";
            return;
        }

        if (_drag == DragMode.None)
        {
            Overlay.Cursor = HitGuide(raw) >= 0 ? Cursors.SizeAll
                : _straightenMode ? Cursors.Pen
                : HitCorner(point) >= 0 ? Cursors.SizeAll
                : !_selection.IsEmpty && _selection.Contains(point) ? Cursors.SizeAll
                : Cursors.Cross;
            return;
        }

        if (_drag == DragMode.Move)
        {
            var dx = point.X - _dragStart.X;
            var dy = point.Y - _dragStart.Y;
            var x = Math.Clamp(_selectionAtDragStart.X + dx, 0, _current.PixelWidth - _selectionAtDragStart.Width);
            var y = Math.Clamp(_selectionAtDragStart.Y + dy, 0, _current.PixelHeight - _selectionAtDragStart.Height);
            _selection = new Rect(Math.Round(x), Math.Round(y), _selectionAtDragStart.Width, _selectionAtDragStart.Height);
        }
        else
        {
            _selection = Span(_anchor, point);
        }

        DrawSelection();
    }

    private void OnOverlayUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag == DragMode.None)
        {
            return;
        }

        var finished = _drag;
        _drag = DragMode.None;
        Overlay.ReleaseMouseCapture();

        if (finished == DragMode.Guide)
        {
            _draggingGuide = -1;
            return;
        }

        if (finished == DragMode.Straighten)
        {
            _straightenLine.Visibility = Visibility.Collapsed;
            var end = e.GetPosition(Surface);
            var correction = ImageMath.StraightenAngle(_lineStart.X, _lineStart.Y, end.X, end.Y);

            if (correction is { } c)
            {
                // 화면은 이미 지금 기울기만큼 돌아가 있습니다. 그 위에서 잰 만큼 더 돌립니다.
                var target = WrapAngle(_tilt + c);
                SetTilt(target);
                Status($"그은 선이 수평·수직이 되도록 {target.ToString("0.00", CultureInfo.InvariantCulture)}° 로 맞췄습니다. 다시 그어 고치거나 슬라이더로 다듬은 뒤 [적용]을 누르십시오.");
            }
            else
            {
                DrawSelection();
            }

            return;
        }

        // 그냥 한 번 누른 것이면 선택을 지웁니다.
        if (_selection.Width < 2 || _selection.Height < 2)
        {
            ClearSelection();
        }

        RefreshState();
    }

    private void OnOverlayLostCapture(object sender, MouseEventArgs e)
    {
        _drag = DragMode.None;
        _draggingGuide = -1;
        _straightenLine.Visibility = Visibility.Collapsed;
    }

    /// <summary>안내선 위에서 오른쪽 단추를 누르면 그 안내선을 지웁니다.</summary>
    private void OnOverlayRightDown(object sender, MouseButtonEventArgs e)
    {
        var index = HitGuide(e.GetPosition(Surface));

        if (index < 0)
        {
            return;
        }

        _guides.RemoveAt(index);
        DrawGuides();
        RefreshState();
        e.Handled = true;
    }

    /// <summary>고정점에서 마우스까지의 영역입니다. 비율을 골라 두었으면 그 비율을 지키고, 그림 밖으로 나가지 않게 줄입니다.</summary>
    private Rect Span(Point anchor, Point point)
    {
        var signX = point.X >= anchor.X ? 1 : -1;
        var signY = point.Y >= anchor.Y ? 1 : -1;

        var width = Math.Abs(point.X - anchor.X);
        var height = Math.Abs(point.Y - anchor.Y);

        var maxWidth = signX > 0 ? _current!.PixelWidth - anchor.X : anchor.X;
        var maxHeight = signY > 0 ? _current!.PixelHeight - anchor.Y : anchor.Y;

        if (TryCurrentRatio(out var ratio))
        {
            // 마우스가 더 멀리 간 쪽에 맞춥니다.
            if (width / ratio > height)
            {
                height = width / ratio;
            }
            else
            {
                width = height * ratio;
            }

            if (width > maxWidth)
            {
                width = maxWidth;
                height = width / ratio;
            }

            if (height > maxHeight)
            {
                height = maxHeight;
                width = height * ratio;
            }
        }
        else
        {
            width = Math.Min(width, maxWidth);
            height = Math.Min(height, maxHeight);
        }

        width = Math.Round(width);
        height = Math.Round(height);

        var x = signX > 0 ? anchor.X : anchor.X - width;
        var y = signY > 0 ? anchor.Y : anchor.Y - height;

        return new Rect(Math.Round(x), Math.Round(y), width, height);
    }

    private Point Clamp(Point point) => _current is null
        ? point
        : new Point(Math.Clamp(Math.Round(point.X), 0, ViewWidth), Math.Clamp(Math.Round(point.Y), 0, ViewHeight));

    /// <summary>모서리 차례: 0 왼쪽 위, 1 오른쪽 위, 2 왼쪽 아래, 3 오른쪽 아래. 맞은편은 3 - 번호입니다.</summary>
    private static Point CornerPoint(Rect rect, int corner) => corner switch
    {
        0 => rect.TopLeft,
        1 => rect.TopRight,
        2 => rect.BottomLeft,
        _ => rect.BottomRight
    };

    private int HitCorner(Point point)
    {
        if (_selection.IsEmpty)
        {
            return -1;
        }

        var reach = 10 / _scale;

        for (var i = 0; i < 4; i++)
        {
            var corner = CornerPoint(_selection, i);

            if (Math.Abs(point.X - corner.X) <= reach && Math.Abs(point.Y - corner.Y) <= reach)
            {
                return i;
            }
        }

        return -1;
    }

    private void OnClearSelection(object sender, RoutedEventArgs e)
    {
        ClearSelection();
        RefreshState();
    }

    private void ClearSelection()
    {
        _selection = Rect.Empty;
        DrawSelection();
    }

    private void DrawSelection()
    {
        if (_current is null || _selection.IsEmpty)
        {
            Shade.Visibility = Visibility.Collapsed;
            SelectionBox.Visibility = Visibility.Collapsed;
            SelectionDash.Visibility = Visibility.Collapsed;

            foreach (var handle in _handles)
            {
                handle.Visibility = Visibility.Collapsed;
            }

            SelectionLabel.Text = _current is null ? string.Empty : "그림 위를 끌어 자를 영역을 잡습니다";
            RefreshButtonsOnly();
            return;
        }

        var line = 1 / _scale;

        // 영역 밖을 어둡게 덮습니다. 바깥 사각형과 선택 사각형을 겹쳐 가운데만 뚫습니다.
        var shade = new GeometryGroup { FillRule = FillRule.EvenOdd };
        shade.Children.Add(new RectangleGeometry(new Rect(0, 0, _current.PixelWidth, _current.PixelHeight)));
        shade.Children.Add(new RectangleGeometry(_selection));
        Shade.Data = shade;
        Shade.Visibility = Visibility.Visible;

        Place(SelectionBox, _selection);
        SelectionBox.StrokeThickness = line;
        SelectionBox.Visibility = Visibility.Visible;

        Place(SelectionDash, _selection);
        SelectionDash.StrokeThickness = line;
        SelectionDash.StrokeDashArray = new DoubleCollection { 4, 4 };
        SelectionDash.Visibility = Visibility.Visible;

        var size = 8 / _scale;

        for (var i = 0; i < 4; i++)
        {
            var corner = CornerPoint(_selection, i);
            var handle = _handles[i];

            handle.Width = size;
            handle.Height = size;
            handle.StrokeThickness = line;
            Canvas.SetLeft(handle, corner.X - (size / 2));
            Canvas.SetTop(handle, corner.Y - (size / 2));
            handle.Visibility = Visibility.Visible;
        }

        SelectionLabel.Text = string.Format(
            CultureInfo.InvariantCulture,
            "선택 {0}, {1} 에서 {2} × {3}",
            (int)_selection.X,
            (int)_selection.Y,
            (int)_selection.Width,
            (int)_selection.Height);

        RefreshButtonsOnly();
    }

    private void RefreshButtonsOnly()
    {
        CropButton.IsEnabled = _current is not null && !_selection.IsEmpty;
        ClearSelectionButton.IsEnabled = !_selection.IsEmpty;
    }

    private static void Place(FrameworkElement element, Rect rect)
    {
        Canvas.SetLeft(element, rect.X);
        Canvas.SetTop(element, rect.Y);
        element.Width = Math.Max(0, rect.Width);
        element.Height = Math.Max(0, rect.Height);
    }

    // ── 기울기 (임의 각도 회전) ──

    /// <summary>
    /// 기울기를 정합니다. 화면의 그림만 돌려 보이고 픽셀은 [적용] 때 계산합니다.
    /// 미리보기는 적용 결과와 같게, 돌린 뒤 빈 귀퉁이가 없도록 남길 부분을 칸에 꽉 차게 키워 보입니다.
    /// </summary>
    private void SetTilt(double degrees)
    {
        _tilt = Math.Round(Math.Abs(degrees) <= MaxTilt ? degrees : WrapAngle(degrees), 2);

        if (Math.Abs(_tilt) >= 0.005 && !_selection.IsEmpty)
        {
            ClearSelection();
        }

        _syncingTilt = true;

        try
        {
            TiltSlider.Value = _tilt;
            TiltBox.Text = _tilt.ToString("0.##", CultureInfo.InvariantCulture);
        }
        finally
        {
            _syncingTilt = false;
        }

        UpdatePictureTransform();

        // 90°·270° 미리보기는 칸의 가로세로가 바뀌므로 배율과 칸 크기를 다시 맞춥니다.
        ApplyZoomLayout();
        UpdateScale();
        DrawGrid();
        RefreshState();
    }

    /// <summary>
    /// 기울기 미리보기입니다. 적용 결과와 같은 모양이 되게 합니다.
    /// 90° 단위 부분은 가로세로를 바꾼 칸에서 그림을 통째로 돌리고,
    /// 나머지 각도는 돌린 뒤 빈 귀퉁이가 없도록 남길 부분을 칸에 꽉 차게 키워 보입니다.
    /// 그림은 원래 크기 그대로 칸 가운데에 두고(넘치면 음수 여백) 가운데를 축으로 돌립니다.
    /// </summary>
    private void UpdatePictureTransform()
    {
        if (_current is null)
        {
            Picture.RenderTransform = Transform.Identity;
            return;
        }

        var width = _current.PixelWidth;
        var height = _current.PixelHeight;
        var viewWidth = ViewWidth;
        var viewHeight = ViewHeight;

        Surface.Width = viewWidth;
        Surface.Height = viewHeight;
        Overlay.Width = viewWidth;
        Overlay.Height = viewHeight;

        Picture.Width = width;
        Picture.Height = height;
        var marginX = (viewWidth - width) / 2.0;
        var marginY = (viewHeight - height) / 2.0;
        Picture.Margin = new Thickness(marginX, marginY, marginX, marginY);

        if (Math.Abs(_tilt) < 0.005)
        {
            Picture.RenderTransform = Transform.Identity;
            return;
        }

        var (quarter, residual) = SplitTilt(_tilt);
        var fill = ImageMath.RotateToFill(viewWidth, viewHeight, residual);
        var zoom = fill.Scale > 0 && Math.Abs(residual) >= 0.005 ? 1 / fill.Scale : 1;

        var group = new TransformGroup();
        group.Children.Add(new RotateTransform(quarter + residual));
        group.Children.Add(new ScaleTransform(zoom, zoom));
        Picture.RenderTransform = group;
    }

    /// <summary>기울기를 픽셀에 반영합니다. 되돌리기 한 단계가 됩니다.</summary>
    private void CommitTilt()
    {
        if (_current is null || Math.Abs(_tilt) < 0.005)
        {
            return;
        }

        var degrees = _tilt;
        var (quarter, residual) = SplitTilt(degrees);

        // 90° 단위는 무손실로 먼저 돌리고, 남은 각도만 빈 귀퉁이를 잘라 내며 돌립니다.
        ApplyNow(
            p =>
            {
                var turned = quarter == 0 ? p : ImageOps.Rotate(p, quarter);
                return Math.Abs(residual) < 0.005 ? turned : ImageOps.RotateToFill(turned, residual);
            },
            $"회전 {degrees.ToString("0.##", CultureInfo.InvariantCulture)}°");
    }

    private void OnTiltSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingTilt || _current is null)
        {
            return;
        }

        SetTilt(Math.Round(e.NewValue, 1));
    }

    private void OnTiltBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        ReadTiltBox();
        e.Handled = true;
    }

    private void OnTiltBoxLostFocus(object sender, RoutedEventArgs e) => ReadTiltBox();

    private void ReadTiltBox()
    {
        if (_current is null)
        {
            return;
        }

        if (double.TryParse(TiltBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var degrees))
        {
            SetTilt(degrees);
        }
        else
        {
            SetTilt(_tilt);
        }
    }

    private void OnApplyTilt(object sender, RoutedEventArgs e) => CommitTilt();

    private void OnResetTilt(object sender, RoutedEventArgs e)
    {
        SetTilt(0);
        Status("기울기를 0°로 되돌렸습니다.");
    }

    // ── 수평맞추기 ──

    private void OnStraighten(object sender, RoutedEventArgs e) => SetStraightenMode(!_straightenMode);

    private void SetStraightenMode(bool on)
    {
        _straightenMode = on && _current is not null;
        StraightenButton.Content = _straightenMode ? "수평맞추기 끝" : "수평맞추기";
        _straightenLine.Visibility = Visibility.Collapsed;

        if (_straightenMode)
        {
            ClearSelection();
            Status("수평이어야 할 선(수평선·바닥·건물 윤곽)을 따라 끄십시오. 세로에 가까운 선을 그으면 수직으로 맞춥니다. Esc 로 끝냅니다.");
        }
        else
        {
            Status(Math.Abs(_tilt) >= 0.005 ? "[적용]을 누르면 기울기가 픽셀에 반영됩니다." : "수평맞추기를 끝냈습니다.");
        }

        DrawGrid();
    }

    private void OnGridToggled(object sender, RoutedEventArgs e) => DrawGrid();

    /// <summary>
    /// 격자 참조선을 그립니다. 수평맞추기 중이거나 기울이는 중이면 저절로 보이고, [격자]를 켜 두면 늘 보입니다.
    /// 격자는 그림과 함께 돌지 않습니다. 돌아가는 그림을 곧은 기준선에 대어 보라는 것입니다.
    /// </summary>
    private void DrawGrid()
    {
        GridLayer.Children.Clear();

        if (_current is null)
        {
            return;
        }

        var show = _straightenMode || Math.Abs(_tilt) >= 0.005 || GridCheck.IsChecked == true;

        if (!show)
        {
            return;
        }

        var width = (double)ViewWidth;
        var height = (double)ViewHeight;
        var step = Math.Max(width, height) / 16;
        var thickness = 1 / _scale;
        var brush = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF));
        var shadow = new SolidColorBrush(Color.FromArgb(0x50, 0x00, 0x00, 0x00));
        brush.Freeze();
        shadow.Freeze();

        for (var x = step; x < width; x += step)
        {
            AddGridLine(x, 0, x, height, thickness, shadow, brush);
        }

        for (var y = step; y < height; y += step)
        {
            AddGridLine(0, y, width, y, thickness, shadow, brush);
        }
    }

    /// <summary>밝은 그림에서도 보이도록 어두운 선을 한 픽셀 옆에 깔고 그 위에 밝은 선을 긋습니다.</summary>
    private void AddGridLine(double x1, double y1, double x2, double y2, double thickness, Brush shadow, Brush brush)
    {
        var offset = thickness;
        var vertical = Math.Abs(x1 - x2) < 0.5;

        GridLayer.Children.Add(new Line
        {
            X1 = x1 + (vertical ? offset : 0),
            Y1 = y1 + (vertical ? 0 : offset),
            X2 = x2 + (vertical ? offset : 0),
            Y2 = y2 + (vertical ? 0 : offset),
            Stroke = shadow,
            StrokeThickness = thickness
        });

        GridLayer.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = thickness });
    }

    // ── 안내선 ──

    private void OnAddHorizontalGuide(object sender, RoutedEventArgs e) => AddGuide(horizontal: true);

    private void OnAddVerticalGuide(object sender, RoutedEventArgs e) => AddGuide(horizontal: false);

    private void AddGuide(bool horizontal)
    {
        if (_current is null)
        {
            return;
        }

        // 이미 가운데에 같은 방향 안내선이 있으면 조금 비켜 놓아 겹치지 않게 합니다.
        var position = 0.5;

        while (_guides.Exists(g => g.Horizontal == horizontal && Math.Abs(g.Position - position) < 0.01) && position < 0.95)
        {
            position += 0.05;
        }

        _guides.Add((horizontal, position));
        DrawGuides();
        RefreshState();
        Status("안내선을 끌어 옮기십시오. 안내선에서 오른쪽 단추를 누르면 지웁니다.");
    }

    private void OnClearGuides(object sender, RoutedEventArgs e)
    {
        _guides.Clear();
        DrawGuides();
        RefreshState();
    }

    private void DrawGuides()
    {
        foreach (var line in _guideLines)
        {
            Overlay.Children.Remove(line);
        }

        _guideLines.Clear();

        if (_current is null)
        {
            return;
        }

        var width = (double)ViewWidth;
        var height = (double)ViewHeight;

        foreach (var (horizontal, position) in _guides)
        {
            var line = horizontal
                ? new Line { X1 = 0, X2 = width, Y1 = position * height, Y2 = position * height }
                : new Line { X1 = position * width, X2 = position * width, Y1 = 0, Y2 = height };

            line.Stroke = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF));
            line.StrokeThickness = 1 / _scale;
            line.IsHitTestVisible = false;

            _guideLines.Add(line);
            Overlay.Children.Add(line);
        }
    }

    /// <summary>마우스 가까이에 있는 안내선의 번호입니다. 없으면 -1 입니다.</summary>
    private int HitGuide(Point point)
    {
        if (_current is null)
        {
            return -1;
        }

        var reach = 6 / _scale;

        for (var i = _guides.Count - 1; i >= 0; i--)
        {
            var (horizontal, position) = _guides[i];
            var distance = horizontal
                ? Math.Abs(point.Y - (position * ViewHeight))
                : Math.Abs(point.X - (position * ViewWidth));

            if (distance <= reach)
            {
                return i;
            }
        }

        return -1;
    }

    // ── 키보드 ──

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var inTextBox = e.OriginalSource is TextBox;

        // 오른쪽 탐색기에 초점이 있으면 저장·열기 말고는 탐색기의 몫입니다 (Enter 로 자르기가 되지 않게).
        if (SideExplorer.IsKeyboardFocusWithin && !(ctrl && e.Key is Key.S or Key.O))
        {
            return;
        }

        switch (e.Key)
        {
            // 글 칸에서의 Ctrl+V·Ctrl+Z 는 글 칸의 몫입니다.
            case Key.V when ctrl && !inTextBox:
                PasteFromClipboard();
                break;
            case Key.Z when ctrl && !inTextBox:
                Undo();
                break;
            case Key.Y when ctrl && !inTextBox:
                Redo();
                break;
            case Key.O when ctrl:
                OnOpenFile(this, e);
                break;
            case Key.S when ctrl:
                SaveAs();
                break;
            case Key.E when ctrl:
                OnOpenInPaint(this, e);
                break;
            case Key.L when ctrl:
                OnRotateLeft(this, e);
                break;
            case Key.D0 when ctrl && !inTextBox:
            case Key.NumPad0 when ctrl && !inTextBox:
                ResetZoom();
                break;
            case Key.R when ctrl:
                OnRotateRight(this, e);
                break;
            case Key.Enter when !inTextBox && !_selection.IsEmpty:
                CropToSelection();
                break;
            case Key.Enter when !inTextBox && Math.Abs(_tilt) >= 0.005:
                CommitTilt();
                break;
            case Key.Escape when _straightenMode:
                SetStraightenMode(false);
                break;
            case Key.Escape when Math.Abs(_tilt) >= 0.005:
                SetTilt(0);
                Status("기울기를 취소했습니다.");
                break;
            case Key.Escape when !_selection.IsEmpty:
                ClearSelection();
                RefreshState();
                break;
            case Key.Escape:
                Close();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // ── 닫기 ──

    /// <summary>저장하지 않은 변경이 있으면 버릴지 묻습니다. 버려도 되면 true 입니다.</summary>
    private bool ConfirmDiscard()
    {
        if (!_dirty || _current is null)
        {
            return true;
        }

        return MessageBox.Show(this, "저장하지 않은 편집이 있습니다. 버리고 계속할까요?", "그림 편집",
            MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmDiscard())
        {
            e.Cancel = true;
        }
    }

    private void OnClosed(object? sender, EventArgs e) => StopWatchingPaint();

    /// <summary>오래 걸리는 동안 모래시계를 보입니다.</summary>
    private sealed class WaitCursor : IDisposable
    {
        private readonly Cursor? _previous = Mouse.OverrideCursor;

        public WaitCursor() => Mouse.OverrideCursor = Cursors.Wait;

        public void Dispose() => Mouse.OverrideCursor = _previous;
    }
}
