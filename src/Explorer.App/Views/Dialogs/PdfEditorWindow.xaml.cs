using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Explorer.App.Services.Imaging;
using Explorer.App.Services.Pdf;
using Explorer.App.ViewModels;
using Explorer.Core.Services;
using Explorer.Shell.Pdf;

namespace Explorer.App.Views.Dialogs;

/// <summary>
/// PDF 편집 창입니다. 세로 3분할로 왼쪽은 쪽 썸네일(3열), 가운데는 고른 쪽 미리보기, 오른쪽은 파일 목록입니다.
///
/// 문서는 "어느 파일의 몇째 쪽"을 늘어놓은 목록입니다. 쪽을 넣고 빼는 동안 원본은 건드리지 않고,
/// [PDF로 저장]을 누를 때 그 목록대로 새 PDF 를 만듭니다. 그림을 넣으면 그림 한 장이 한 쪽이 되므로
/// 빈 문서에 그림을 넣어 저장하면 그림 → PDF 변환이고, [그림으로 내보내기]는 PDF → 그림 변환입니다.
/// 미리보기는 윈도우에 들어 있는 PDF 부품으로 그립니다. 다른 프로그램을 부르지 않습니다.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class PdfEditorWindow : Window
{
    /// <summary>썸네일을 그리는 가로 픽셀입니다. 칸 높이 190 에 맞춰 조금 넉넉히 잡았습니다.</summary>
    private const int ThumbnailWidth = 240;

    private const int MaxPreviewPixels = 4000;

    /// <summary>썸네일 한 칸입니다.</summary>
    public sealed class PageItem : ObservableObject
    {
        private int _number;
        private BitmapSource? _thumbnail;
        private bool _isLoading = true;

        public PageItem(PdfPageRef page) => Page = page;

        public PdfPageRef Page { get; }

        public string Source => Page.Describe();

        public int Number
        {
            get => _number;
            set => SetProperty(ref _number, value);
        }

        public BitmapSource? Thumbnail
        {
            get => _thumbnail;
            set => SetProperty(ref _thumbnail, value);
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }
    }

    private readonly PdfRenderWorker _renderer = new();
    private readonly ObservableCollection<PageItem> _pages = new();

    private readonly DispatcherTimer _previewDelay;

    private CancellationTokenSource? _thumbnails;
    private int _previewVersion;
    private double _zoom = 1;
    private PreviewFit _fit = PreviewFit.Auto;

    // 휠로 쪽을 넘길 때 씁니다. 터치패드처럼 잘게 오는 휠을 모아 한 칸(120)이 되면 넘깁니다.
    private int _wheelSum;
    private DateTime _lastPageTurn = DateTime.MinValue;

    /// <summary>쪽을 넘긴 뒤 미리보기를 어디에 둘지입니다. 앞쪽으로 넘기면 아래 끝, 뒤쪽이면 위 끝입니다.</summary>
    private bool? _scrollToEndAfterRender;

    /// <summary>지금 쪽의 세로 ÷ 가로입니다. 높이 맞춤에 씁니다. 모르면 A4(√2)로 봅니다.</summary>
    private double _aspect = Math.Sqrt(2);

    /// <summary>경계선을 손으로 끌었는지입니다. 끌었으면 칸 너비를 더 셈하지 않습니다.</summary>
    private bool _manualColumns;

    private enum PreviewFit
    {
        /// <summary>가로 쪽은 너비 맞춤, 세로 쪽은 높이 맞춤입니다. 처음 값입니다.</summary>
        Auto,
        Width,
        Height,
        Manual
    }
    private bool _dirty;
    private bool _busy;
    private string? _documentName;

    public PdfEditorWindow(string? initialFolder)
    {
        InitializeComponent();
        PopupTitleBar.Apply(this, fillScreen: true);

        Thumbs.ItemsSource = _pages;

        _previewDelay = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(120) };
        _previewDelay.Tick += async (_, _) =>
        {
            _previewDelay.Stop();
            await RenderPreviewAsync();
        };

        _pages.CollectionChanged += (_, _) => RefreshState();

        SideExplorer.FileFilter = path => PdfPageRef.IsPdf(path) || ImageMath.IsEditable(path);
        SideExplorer.FilesActivated += paths =>
        {
            if (!_busy)
            {
                _ = AddPathsAsync(paths, InsertIndex());
            }
        };
        SideExplorer.StatusMessage += Status;
        SideExplorer.Start(initialFolder);

        // 즐겨찾기를 누르면 오른쪽 탐색기에 새 탭으로 엽니다.
        TitleBar.EnableFavorites(SideExplorer.OpenFavorite);

        RefreshState();
    }

    /// <summary>
    /// PDF 편집 창을 엽니다. 칸에서 고른 PDF·그림이 있으면 고른 차례대로 쪽으로 넣습니다.
    /// 그림만 골라 열면 그대로 [PDF로 저장]해 그림 → PDF 로 쓸 수 있습니다.
    /// </summary>
    public static void ShowFor(Window? owner, IReadOnlyList<string> selected, string? currentFolder)
    {
        var folder = !string.IsNullOrEmpty(currentFolder) && !SpecialPaths.IsVirtual(currentFolder) ? currentFolder : null;
        var window = new PdfEditorWindow(folder) { Owner = owner };
        window.Show();

        var usable = new List<string>();

        foreach (var path in selected)
        {
            if (File.Exists(path) && (PdfPageRef.IsPdf(path) || ImageMath.IsEditable(path)))
            {
                usable.Add(path);
            }
        }

        if (usable.Count > 0)
        {
            _ = window.AddPathsAsync(usable, 0, markDirty: false);
        }
    }

    // ── 문서 ──

    /// <summary>파일들을 쪽으로 넣습니다. PDF 는 모든 쪽을, 그림은 한 장을 한 쪽으로 넣습니다.</summary>
    private async Task AddPathsAsync(IReadOnlyList<string> paths, int insertAt, bool markDirty = true)
    {
        insertAt = Math.Clamp(insertAt, 0, _pages.Count);
        var added = 0;
        var problems = new List<string>();

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                problems.Add($"{Path.GetFileName(path)}: 폴더는 넣지 않습니다");
                continue;
            }

            if (PdfPageRef.IsPdf(path))
            {
                int count;

                try
                {
                    count = await _renderer.PageCountAsync(path);
                }
                catch (Exception ex)
                {
                    problems.Add($"{Path.GetFileName(path)}: {ex.Message}");
                    continue;
                }

                for (var i = 0; i < count; i++)
                {
                    _pages.Insert(insertAt++, new PageItem(new PdfPageRef(path, i)));
                    added++;
                }
            }
            else if (ImageMath.IsEditable(path))
            {
                _pages.Insert(insertAt++, new PageItem(PdfPageRef.Image(path)));
                added++;
            }
            else
            {
                problems.Add($"{Path.GetFileName(path)}: PDF·그림이 아닙니다");
            }

            _documentName ??= Path.GetFileNameWithoutExtension(path);
        }

        Renumber();

        if (added > 0)
        {
            _dirty |= markDirty;
            StartThumbnails();

            // 새로 들어간 쪽들을 골라 두고 그 자리로 굴려 보입니다. 어디에 들어갔는지 바로 알 수 있습니다.
            var first = insertAt - added;
            Thumbs.SelectedItems.Clear();

            for (var i = first; i < insertAt; i++)
            {
                Thumbs.SelectedItems.Add(_pages[i]);
            }

            Thumbs.ScrollIntoView(_pages[first]);
        }

        var message = $"{added}쪽을 넣었습니다. 모두 {_pages.Count}쪽입니다.";

        if (problems.Count > 0)
        {
            message += " 넣지 못한 것: " + string.Join(" / ", problems);
        }

        Status(message);
    }

    /// <summary>고른 쪽 뒤의 자리입니다. 고른 것이 없으면 맨 뒤입니다.</summary>
    private int InsertIndex()
    {
        var last = -1;

        foreach (var item in Thumbs.SelectedItems)
        {
            if (item is PageItem page)
            {
                last = Math.Max(last, _pages.IndexOf(page));
            }
        }

        return last >= 0 ? last + 1 : _pages.Count;
    }

    private void Renumber()
    {
        for (var i = 0; i < _pages.Count; i++)
        {
            _pages[i].Number = i + 1;
        }
    }

    private void OnOpen(object sender, RoutedEventArgs e) => OpenDocument();

    private void OpenDocument()
    {
        if (_busy || !ConfirmDiscard())
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "PDF 열기",
            Filter = "PDF (*.pdf)|*.pdf",
            InitialDirectory = SideExplorer.CurrentFolder ?? string.Empty
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        ClearDocument();
        _documentName = Path.GetFileNameWithoutExtension(dialog.FileName);
        _ = AddPathsAsync(new[] { dialog.FileName }, 0, markDirty: false);
    }

    /// <summary>
    /// 지금 작업을 버리고 빈 화면으로 돌아갑니다. 쪽이 하나라도 있으면 폐기할지 먼저 묻습니다.
    /// 원본 파일은 건드리지 않습니다. 버리는 것은 이 창에서 넣고 뺀 목록뿐입니다.
    /// </summary>
    private void OnReset(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (_pages.Count == 0)
        {
            Status("이미 비어 있습니다.");
            return;
        }

        var message = $"지금 작업({_pages.Count}쪽)을 폐기하고 빈 화면으로 돌아갈까요?"
            + (_dirty ? $"{Environment.NewLine}저장하지 않은 편집은 사라집니다." : string.Empty)
            + $"{Environment.NewLine}원본 파일은 그대로 남습니다.";

        if (MessageBox.Show(this, message, "초기화", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        ClearDocument();
        Status("초기화했습니다. PDF 나 그림을 끌어 놓거나 [열기…]를 누르십시오.");
    }

    /// <summary>
    /// 작업 중에 미리보기 칸으로 다른 파일을 끌어 놓았을 때입니다. 새 작업으로 엽니다.
    /// 저장하지 않은 편집이 있으면 저장할지(예), 폐기할지(아니요), 그만둘지(취소) 묻습니다.
    /// </summary>
    private async Task ReplaceDocumentAsync(IReadOnlyList<string> files)
    {
        if (_busy)
        {
            return;
        }

        if (_dirty && _pages.Count > 0)
        {
            var answer = MessageBox.Show(
                this,
                $"새 파일을 새 작업으로 엽니다. 지금 작업({_pages.Count}쪽)을 저장할까요?{Environment.NewLine}{Environment.NewLine}"
                    + "예: 저장한 뒤 새로 엽니다" + Environment.NewLine
                    + "아니요: 저장하지 않고 폐기한 뒤 새로 엽니다" + Environment.NewLine
                    + "취소: 지금 작업을 그대로 둡니다",
                "새 작업",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel)
            {
                return;
            }

            if (answer == MessageBoxResult.Yes && !await SaveAsync())
            {
                Status("저장하지 않아 새 파일을 열지 않았습니다.");
                return;
            }
        }

        ClearDocument();
        await AddPathsAsync(files, 0, markDirty: false);
    }

    private void ClearDocument()
    {
        _thumbnails?.Cancel();
        _pages.Clear();
        _documentName = null;
        _dirty = false;
        PreviewImage.Source = null;
        RefreshState();
    }

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "넣을 PDF·그림 고르기",
            Multiselect = true,
            Filter = "PDF·그림|*.pdf;*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.bmp;*.gif;*.tif;*.tiff;*.heic;*.webp|모든 파일|*.*",
            InitialDirectory = SideExplorer.CurrentFolder ?? string.Empty
        };

        if (dialog.ShowDialog(this) == true)
        {
            _ = AddPathsAsync(dialog.FileNames, InsertIndex());
        }
    }

    private void OnDeletePages(object sender, RoutedEventArgs e) => DeleteSelected();

    private void DeleteSelected()
    {
        if (_busy || Thumbs.SelectedItems.Count == 0)
        {
            return;
        }

        var chosen = new List<PageItem>();

        foreach (var item in Thumbs.SelectedItems)
        {
            if (item is PageItem page)
            {
                chosen.Add(page);
            }
        }

        var first = _pages.Count;

        foreach (var page in chosen)
        {
            first = Math.Min(first, _pages.IndexOf(page));
            _pages.Remove(page);
        }

        Renumber();
        _dirty = true;

        if (_pages.Count > 0)
        {
            Thumbs.SelectedIndex = Math.Min(first, _pages.Count - 1);
        }
        else
        {
            PreviewImage.Source = null;
        }

        Status($"{chosen.Count}쪽을 뺐습니다. 모두 {_pages.Count}쪽입니다. 원본 파일은 그대로입니다.");
    }

    // ── 썸네일 ──

    /// <summary>아직 그리지 않은 썸네일을 앞에서부터 차례로 그립니다. 새로 부르면 앞의 작업은 멈춥니다.</summary>
    private async void StartThumbnails()
    {
        _thumbnails?.Cancel();
        var cancellation = new CancellationTokenSource();
        _thumbnails = cancellation;

        var pending = new List<PageItem>();

        foreach (var page in _pages)
        {
            if (page.Thumbnail is null)
            {
                pending.Add(page);
            }
        }

        foreach (var item in pending)
        {
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            try
            {
                item.Thumbnail = item.Page.IsImage
                    ? await Task.Run(() => PdfImages.LoadScaled(item.Page.Path, ThumbnailWidth), cancellation.Token)
                    : await _renderer.RenderAsync(item.Page.Path, item.Page.PageIndex, ThumbnailWidth, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"썸네일을 그리지 못했습니다 {item.Source}: {ex.Message}");
            }
            finally
            {
                item.IsLoading = false;
            }
        }
    }

    private void OnThumbSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshState();
        _previewDelay.Stop();
        _previewDelay.Start();
    }

    // ── 미리보기 ──

    /// <summary>마지막으로 고른 쪽입니다.</summary>
    private PageItem? CurrentPage()
    {
        var items = Thumbs.SelectedItems;
        return items.Count > 0 ? items[items.Count - 1] as PageItem : null;
    }

    private async Task RenderPreviewAsync()
    {
        var item = CurrentPage();
        var version = ++_previewVersion;

        if (item is null)
        {
            PreviewImage.Source = null;
            PreviewPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        // 높이 맞춤은 쪽 모양을 알아야 폭을 셀 수 있습니다. 썸네일이 있으면 그 모양을, 없으면 PDF 쪽 크기를 씁니다.
        try
        {
            if (item.Thumbnail is { PixelWidth: > 0 } thumb)
            {
                _aspect = (double)thumb.PixelHeight / thumb.PixelWidth;
            }
            else if (!item.Page.IsImage)
            {
                var (w, h) = await _renderer.PageSizeAsync(item.Page.Path, item.Page.PageIndex);
                _aspect = w > 0 ? h / w : Math.Sqrt(2);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"쪽 모양을 읽지 못했습니다 {item.Source}: {ex.Message}");
        }

        if (version != _previewVersion)
        {
            return;
        }

        var widthDip = PreviewWidth();
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var pixels = Math.Clamp((int)Math.Round(widthDip * scale), 64, MaxPreviewPixels);

        PreviewImage.Width = widthDip;

        try
        {
            var bitmap = item.Page.IsImage
                ? await Task.Run(() => PdfImages.LoadScaled(item.Page.Path, pixels))
                : await _renderer.RenderAsync(item.Page.Path, item.Page.PageIndex, pixels);

            if (version != _previewVersion)
            {
                return;
            }

            PreviewImage.Source = bitmap;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            PageLabelUpdate();

            if (_scrollToEndAfterRender is { } toEnd)
            {
                _scrollToEndAfterRender = null;
                PreviewScroller.UpdateLayout();

                if (toEnd)
                {
                    PreviewScroller.ScrollToBottom();
                }
                else
                {
                    PreviewScroller.ScrollToTop();
                }
            }
        }
        catch (Exception ex)
        {
            if (version == _previewVersion)
            {
                PreviewImage.Source = null;
                PreviewPlaceholder.Visibility = Visibility.Visible;
                Status($"{item.Source} 을(를) 그리지 못했습니다: {ex.Message}");
            }
        }
    }

    /// <summary>미리보기 폭입니다. 너비 맞춤·높이 맞춤·손으로 정한 배율 가운데 지금 방식을 따릅니다.</summary>
    private double PreviewWidth()
    {
        var viewWidth = PreviewScroller.ViewportWidth > 0 ? PreviewScroller.ViewportWidth : PreviewScroller.ActualWidth - 2;
        var viewHeight = PreviewScroller.ViewportHeight > 0 ? PreviewScroller.ViewportHeight : PreviewScroller.ActualHeight - 2;
        var fitWidth = Math.Max(120, viewWidth - 32);

        return EffectiveFit() switch
        {
            PreviewFit.Width => fitWidth,
            PreviewFit.Height => Math.Max(120, (viewHeight - 32) / _aspect),
            _ => Math.Max(60, fitWidth * _zoom)
        };
    }

    /// <summary>자동이면 쪽 모양으로 정합니다. 가로가 더 긴 쪽은 너비, 아니면 높이에 맞춥니다.</summary>
    private PreviewFit EffectiveFit() => _fit == PreviewFit.Auto
        ? (_aspect < 1 ? PreviewFit.Width : PreviewFit.Height)
        : _fit;

    /// <summary>맞춤 드롭다운을 단추 바로 아래에 펼칩니다.</summary>
    private void OnFitButton(object sender, RoutedEventArgs e)
    {
        FitMenu.PlacementTarget = FitButton;
        FitMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        FitMenu.IsOpen = true;
    }

    private void OnFitAuto(object sender, RoutedEventArgs e) => SetFit(PreviewFit.Auto);

    private void OnFitWidth(object sender, RoutedEventArgs e) => SetFit(PreviewFit.Width);

    private void OnFitHeight(object sender, RoutedEventArgs e) => SetFit(PreviewFit.Height);

    private void SetFit(PreviewFit fit)
    {
        _fit = fit;
        _zoom = 1;
        PreviewImage.Width = PreviewWidth();
        PageLabelUpdate();
        _previewDelay.Stop();
        _previewDelay.Start();
    }

    // ── 칸 너비 ──

    private void OnBodySizeChanged(object sender, SizeChangedEventArgs e) => UpdateColumns();

    private void OnSplitterDragged(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) => _manualColumns = true;

    /// <summary>
    /// 칸 너비를 셉니다. 썸네일은 A4 세로 3장이 줄지 않는 너비(478),
    /// 미리보기는 A4 세로 한 쪽이 위아래 여백만 두고 높이에 꽉 차는 너비, 탐색기는 나머지입니다.
    /// 창이 좁아 탐색기가 260 보다 좁아지면 미리보기부터 줄입니다.
    /// </summary>
    private void UpdateColumns()
    {
        if (_manualColumns || Body.ActualWidth <= 0 || PreviewFrame.ActualHeight <= 0)
        {
            return;
        }

        const double thumbs = 478;
        const double explorerMin = 260;
        const double splitters = 12;

        var total = Body.ActualWidth;
        var preview = ((PreviewFrame.ActualHeight - 2 - 32) / Math.Sqrt(2)) + 32 + 2;
        var thumbWidth = Math.Min(thumbs, Math.Max(200, total - splitters - 240 - explorerMin));
        var room = total - splitters - thumbWidth - explorerMin;

        preview = Math.Clamp(preview, 240, Math.Max(240, room));

        ThumbColumn.Width = new GridLength(thumbWidth);
        PreviewColumn.Width = new GridLength(Math.Round(preview));
    }

    // ── 미리보기 칸에 끌어 놓기: 비었으면 열기, 작업 중이면 새 작업으로 열기 ──

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_busy && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnPreviewDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (_busy || e.Data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return;
        }

        if (_pages.Count == 0)
        {
            _ = AddPathsAsync(files, 0, markDirty: false);
            return;
        }

        // 끌어 온 쪽 프로그램이 놓기가 끝나기를 기다리지 않도록, 묻는 창은 놓기가 끝난 뒤에 띄웁니다.
        Dispatcher.BeginInvoke(async () => await ReplaceDocumentAsync(files));
    }

    private void OnPreviewWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            TurnPageByWheel(e);
            return;
        }

        // 맞춤 상태에서 굴리면 지금 크기를 배율로 바꿔 거기서부터 키우고 줄입니다.
        if (_fit != PreviewFit.Manual)
        {
            var fitWidth = Math.Max(120, (PreviewScroller.ViewportWidth > 0 ? PreviewScroller.ViewportWidth : PreviewScroller.ActualWidth) - 32);
            _zoom = PreviewWidth() / fitWidth;
            _fit = PreviewFit.Manual;
        }

        _zoom = Math.Clamp(e.Delta > 0 ? _zoom * 1.15 : _zoom / 1.15, 0.2, 6);

        // 바로 늘려 보이고, 선명한 그림은 잠시 뒤 다시 그립니다.
        PreviewImage.Width = PreviewWidth();
        PageLabelUpdate();
        _previewDelay.Stop();
        _previewDelay.Start();
        e.Handled = true;
    }

    /// <summary>
    /// 휠로 쪽을 넘깁니다. 쪽이 미리보기보다 커서 굴릴 자리가 남아 있으면 먼저 굴리고,
    /// 위·아래 끝에 닿은 뒤 더 굴리면 앞·뒤 쪽으로 넘어갑니다. 한눈에 다 보이는 쪽은 바로 넘어갑니다.
    /// </summary>
    private void TurnPageByWheel(MouseWheelEventArgs e)
    {
        var down = e.Delta < 0;
        var canScroll = down
            ? PreviewScroller.VerticalOffset < PreviewScroller.ScrollableHeight - 0.5
            : PreviewScroller.VerticalOffset > 0.5;

        if (canScroll)
        {
            _wheelSum = 0;
            return; // 보통의 굴림은 ScrollViewer 에 맡깁니다.
        }

        e.Handled = true;

        if (_pages.Count == 0)
        {
            return;
        }

        _wheelSum += e.Delta;

        if (Math.Abs(_wheelSum) < 120 || (DateTime.Now - _lastPageTurn).TotalMilliseconds < 120)
        {
            return;
        }

        _wheelSum = 0;
        _lastPageTurn = DateTime.Now;

        var current = CurrentPage();
        var index = current is null ? 0 : _pages.IndexOf(current) + (down ? 1 : -1);

        if (index < 0 || index >= _pages.Count)
        {
            Status(down ? "마지막 쪽입니다." : "첫 쪽입니다.");
            return;
        }

        // 뒤로 넘기면 새 쪽의 위 끝부터, 앞으로 넘기면 아래 끝부터 보입니다. 이어 읽는 느낌을 살립니다.
        _scrollToEndAfterRender = !down;

        Thumbs.SelectedItems.Clear();
        Thumbs.SelectedItem = _pages[index];
        Thumbs.ScrollIntoView(_pages[index]);
    }

    private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (PreviewImage.Source is null)
        {
            return;
        }

        PreviewImage.Width = PreviewWidth();
        _previewDelay.Stop();
        _previewDelay.Start();
    }

    // ── 끌어 놓기 ──

    private void OnThumbsDragOver(object sender, DragEventArgs e)
    {
        var allowed = !_busy && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = allowed ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        if (!allowed)
        {
            DropMarker.Visibility = Visibility.Collapsed;
            return;
        }

        var position = e.GetPosition(Thumbs);
        var (_, marker) = DropTarget(position);
        ShowDropMarker(marker);
        AutoScroll(position);
    }

    private void OnThumbsDragLeave(object sender, DragEventArgs e) => DropMarker.Visibility = Visibility.Collapsed;

    private void OnThumbsDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        DropMarker.Visibility = Visibility.Collapsed;

        if (_busy || e.Data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return;
        }

        var (index, _) = DropTarget(e.GetPosition(Thumbs));
        _ = AddPathsAsync(files, index);
    }

    /// <summary>
    /// 마우스 자리에 놓으면 몇째 자리에 들어가는지와, 그 자리를 보일 세로선의 위치를 셉니다.
    /// 썸네일의 왼쪽 반이면 그 쪽 앞, 오른쪽 반이면 그 쪽 뒤입니다. 줄 끝의 빈 곳은 그 줄 마지막 쪽 뒤,
    /// 맨 아래 빈 곳은 맨 끝입니다.
    /// </summary>
    private (int Index, Rect Marker) DropTarget(Point position)
    {
        if (_pages.Count == 0)
        {
            return (0, Rect.Empty);
        }

        Rect? lastInRow = null;
        var lastInRowIndex = -1;
        Rect? last = null;

        for (var i = 0; i < _pages.Count; i++)
        {
            if (Thumbs.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container || !container.IsVisible)
            {
                continue;
            }

            var bounds = container.TransformToAncestor(Thumbs).TransformBounds(new Rect(container.RenderSize));
            last = bounds;

            if (position.Y < bounds.Top && i == 0)
            {
                return (0, BeforeMarker(bounds));
            }

            // 줄 사이 틈(여백)에 있어도 가까운 줄로 봅니다.
            if (position.Y < bounds.Top - 4 || position.Y > bounds.Bottom + 4)
            {
                continue;
            }

            if (position.X < bounds.Left + (bounds.Width / 2))
            {
                return (i, BeforeMarker(bounds));
            }

            if (position.X <= bounds.Right)
            {
                return (i + 1, AfterMarker(bounds));
            }

            lastInRow = bounds;
            lastInRowIndex = i;
        }

        if (lastInRow is { } rowEnd)
        {
            return (lastInRowIndex + 1, AfterMarker(rowEnd));
        }

        return (_pages.Count, last is { } end ? AfterMarker(end) : Rect.Empty);
    }

    private static Rect BeforeMarker(Rect item) => new(item.Left - 6, item.Top, 4, item.Height);

    private static Rect AfterMarker(Rect item) => new(item.Right + 2, item.Top, 4, item.Height);

    private void ShowDropMarker(Rect marker)
    {
        if (marker.IsEmpty)
        {
            DropMarker.Visibility = Visibility.Collapsed;
            return;
        }

        Canvas.SetLeft(DropMarker, Math.Max(0, marker.X));
        Canvas.SetTop(DropMarker, marker.Y);
        DropMarker.Height = marker.Height;
        DropMarker.Visibility = Visibility.Visible;
    }

    /// <summary>끌고 있는 마우스가 위·아래 끝에 가까우면 목록을 굴려 보이지 않던 자리에도 놓을 수 있게 합니다.</summary>
    private void AutoScroll(Point position)
    {
        if (FindChild<ScrollViewer>(Thumbs) is not { } scroller)
        {
            return;
        }

        if (position.Y < 40)
        {
            scroller.LineUp();
        }
        else if (position.Y > Thumbs.ActualHeight - 40)
        {
            scroller.LineDown();
        }
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);

            if (child is T found)
            {
                return found;
            }

            if (FindChild<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    // ── 저장 ──

    private void OnSave(object sender, RoutedEventArgs e) => _ = SaveAsync();

    /// <summary>PDF 로 저장합니다. 저장했으면 true, 취소했거나 실패했으면 false 입니다.</summary>
    private async Task<bool> SaveAsync()
    {
        if (_busy)
        {
            return false;
        }

        if (_pages.Count == 0)
        {
            MessageBox.Show(this, "저장할 쪽이 없습니다.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "PDF로 저장",
            Filter = "PDF (*.pdf)|*.pdf",
            FileName = (_documentName ?? "새 문서") + "_편집",
            DefaultExt = ".pdf",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = FirstSourceFolder() ?? SideExplorer.CurrentFolder ?? string.Empty
        };

        if (dialog.ShowDialog(this) != true)
        {
            return false;
        }

        var target = Path.GetFullPath(dialog.FileName);
        var refs = new List<PdfPageRef>();

        foreach (var page in _pages)
        {
            refs.Add(page.Page);

            if (string.Equals(Path.GetFullPath(page.Page.Path), target, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "지금 쪽을 가져오고 있는 원본 파일에는 덮어쓸 수 없습니다. 다른 이름을 고르십시오.",
                    Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
        }

        var fitA4 = FitA4Check.IsChecked == true;
        SetBusy(true);
        Status("저장하는 중입니다…");

        try
        {
            await RunOnStaAsync(() => PdfWriterService.Save(refs, target, fitA4, PdfImages.Prepare));
            _dirty = false;
            Status($"저장했습니다: {target} ({refs.Count}쪽)");

            if (SideExplorer.CurrentFolder is not null && string.Equals(Path.GetDirectoryName(target), SideExplorer.CurrentFolder, StringComparison.OrdinalIgnoreCase))
            {
                SideExplorer.Refresh();
            }

            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"PDF 저장 실패: {ex}");
            MessageBox.Show(this, $"저장하지 못했습니다.{Environment.NewLine}{ex.Message}", Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Status("저장하지 못했습니다.");
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ── 그림으로 내보내기 ──

    private void OnExportSubfolderFocus(object sender, RoutedEventArgs e) => ExportSubfolderRadio.IsChecked = true;

    private void OnExportFolderFocus(object sender, RoutedEventArgs e) => ExportFolderRadio.IsChecked = true;

    private void OnBrowseExportFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "그림을 저장할 폴더 고르기" };

        var start = Directory.Exists(ExportFolderBox.Text) ? ExportFolderBox.Text : FirstSourceFolder() ?? SideExplorer.CurrentFolder;

        if (start is not null)
        {
            dialog.InitialDirectory = start;
        }

        if (dialog.ShowDialog(this) == true)
        {
            ExportFolderBox.Text = dialog.FolderName;
            ExportFolderRadio.IsChecked = true;
        }
    }

    private void OnExport(object sender, RoutedEventArgs e) => _ = ExportAsync();

    private async Task ExportAsync()
    {
        if (_busy)
        {
            return;
        }

        if (!int.TryParse(DpiBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var dpi) || dpi is < 72 or > 600)
        {
            MessageBox.Show(this, "해상도는 72~600 DPI 사이로 넣으십시오.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var targets = new List<PageItem>();

        if (ExportRangeBox.SelectedIndex == 1)
        {
            foreach (var page in _pages)
            {
                if (Thumbs.SelectedItems.Contains(page))
                {
                    targets.Add(page);
                }
            }
        }
        else
        {
            targets.AddRange(_pages);
        }

        if (targets.Count == 0)
        {
            MessageBox.Show(this, "내보낼 쪽이 없습니다.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var name = _documentName ?? "문서";
        string folder;

        if (ExportFolderRadio.IsChecked == true)
        {
            folder = ExportFolderBox.Text.Trim();

            if (folder.Length == 0 || !Path.IsPathFullyQualified(folder))
            {
                MessageBox.Show(this, "저장할 폴더를 고르십시오. [찾아보기…]를 누르면 됩니다.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }
        else
        {
            var sub = ExportSubfolderBox.Text.Trim();

            if (sub.Length == 0)
            {
                sub = name;
            }

            if (sub.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                MessageBox.Show(this, "하위 폴더 이름에 쓸 수 없는 글자가 있습니다.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var parent = FirstSourceFolder() ?? SideExplorer.CurrentFolder;

            if (parent is null)
            {
                MessageBox.Show(this, "하위 폴더를 만들 자리를 알 수 없습니다. [지정 폴더]를 쓰십시오.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            folder = Path.Combine(parent, sub);
        }

        var extension = ExportFormatBox.SelectedIndex == 1 ? ".jpg" : ".png";
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var written = 0;
        var lowResolution = false;

        SetBusy(true);

        try
        {
            Directory.CreateDirectory(folder);

            for (var i = 0; i < targets.Count; i++)
            {
                var page = targets[i];
                Status($"그림으로 내보내는 중 {i + 1}/{targets.Count} — {page.Source}");

                BitmapSource bitmap;

                if (page.Page.IsImage)
                {
                    bitmap = ImageCodec.Load(page.Page.Path).Pixels;
                }
                else
                {
                    var (widthDip, _) = await _renderer.PageSizeAsync(page.Page.Path, page.Page.PageIndex);
                    var widthPixels = Math.Clamp((int)Math.Round(widthDip * dpi / 96.0), 16, 20000);
                    bitmap = await _renderer.RenderAsync(page.Page.Path, page.Page.PageIndex, widthPixels);
                    lowResolution |= bitmap.PixelWidth < widthPixels * 0.9;
                    bitmap = ImageOps.WithDpi(bitmap, dpi, dpi);
                }

                var path = ImageMath.UniquePath(folder, $"{name}_p{page.Number:000}", extension, File.Exists, taken);
                ImageCodec.Save(bitmap, path, new ImageSaveOptions { JpegQuality = 92 });
                written++;
            }

            var message = $"{written}장을 저장했습니다: {folder}";

            if (lowResolution)
            {
                message += " — 이 윈도우에서는 크기를 지정해 그리지 못해 기본 해상도(96 DPI)로 저장했습니다.";
            }

            Status(message);

            if (SideExplorer.CurrentFolder is not null && string.Equals(Path.GetDirectoryName(folder), SideExplorer.CurrentFolder, StringComparison.OrdinalIgnoreCase))
            {
                SideExplorer.Refresh();
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"그림 내보내기 실패: {ex}");
            MessageBox.Show(this, $"{written}장까지 저장하고 멈췄습니다.{Environment.NewLine}{ex.Message}", Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Status($"{written}장까지 저장하고 멈췄습니다.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ── 화면 ──

    private void RefreshState()
    {
        var has = _pages.Count > 0;

        ThumbsPlaceholder.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        PreviewPlaceholder.Visibility = PreviewImage.Source is null ? Visibility.Visible : Visibility.Collapsed;

        DeleteButton.IsEnabled = !_busy && Thumbs.SelectedItems.Count > 0;
        SaveButton.IsEnabled = !_busy && has;
        ExportButton.IsEnabled = !_busy && has;
        AddButton.IsEnabled = !_busy;

        Title = $"PDF 편집{(_documentName is null ? string.Empty : " — " + _documentName)}{(_dirty ? " *" : string.Empty)}";
        PageLabelUpdate();
    }

    private void PageLabelUpdate()
    {
        // 맞춤 드롭다운의 글자와 체크 표시를 지금 방식에 맞춥니다. 손으로 확대했으면 배율을 보입니다.
        FitButton.Content = _fit switch
        {
            PreviewFit.Auto => "자동 맞춤 ▾",
            PreviewFit.Width => "너비 맞춤 ▾",
            PreviewFit.Height => "높이 맞춤 ▾",
            _ => $"{_zoom * 100:0}% ▾"
        };
        FitAutoItem.IsChecked = _fit == PreviewFit.Auto;
        FitWidthItem.IsChecked = _fit == PreviewFit.Width;
        FitHeightItem.IsChecked = _fit == PreviewFit.Height;

        var current = CurrentPage();
        var zoom = _fit switch
        {
            PreviewFit.Auto => EffectiveFit() == PreviewFit.Width ? " · 자동(너비 맞춤)" : " · 자동(높이 맞춤)",
            PreviewFit.Width => " · 너비 맞춤",
            PreviewFit.Height => " · 높이 맞춤",
            _ => $" · {_zoom * 100:0}%"
        };

        PageLabel.Text = current is null
            ? $"모두 {_pages.Count}쪽"
            : $"{current.Number} / {_pages.Count}쪽 · {current.Source}{(Thumbs.SelectedItems.Count > 1 ? $" · {Thumbs.SelectedItems.Count}쪽 고름" : string.Empty)}{zoom}";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Mouse.OverrideCursor = busy ? Cursors.AppStarting : null;
        RefreshState();
    }

    private void Status(string message)
    {
        StatusLabel.Text = message;
        RefreshState();
    }

    /// <summary>문서에 든 첫 PDF(없으면 첫 파일)의 폴더입니다. 저장·내보내기 자리의 기본값입니다.</summary>
    private string? FirstSourceFolder()
    {
        foreach (var page in _pages)
        {
            if (!page.Page.IsImage)
            {
                return Path.GetDirectoryName(page.Page.Path);
            }
        }

        return _pages.Count > 0 ? Path.GetDirectoryName(_pages[0].Page.Path) : null;
    }

    /// <summary>그림 코덱을 쓰는 일을 STA 스레드에서 돌립니다. 일괄 그림 편집과 같은 이유입니다.</summary>
    private static Task RunOnStaAsync(Action work)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                work();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "PdfSave"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    // ── 키보드 ──

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var inTextBox = e.OriginalSource is TextBox;

        switch (e.Key)
        {
            case Key.Delete when !inTextBox && Thumbs.IsKeyboardFocusWithin:
                DeleteSelected();
                break;
            case Key.O when ctrl:
                OpenDocument();
                break;

            case Key.S when ctrl:
                _ = SaveAsync();
                break;
            case Key.D0 when ctrl:
            case Key.NumPad0 when ctrl:
                SetFit(PreviewFit.Auto);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // ── 닫기 ──

    private bool ConfirmDiscard()
    {
        if (!_dirty || _pages.Count == 0)
        {
            return true;
        }

        return MessageBox.Show(this, "저장하지 않은 편집이 있습니다. 버리고 계속할까요?", Title,
            MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
            MessageBox.Show(this, "작업이 끝난 뒤에 닫아 주십시오.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!ConfirmDiscard())
        {
            e.Cancel = true;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _thumbnails?.Cancel();
        _previewDelay.Stop();
        _renderer.Dispose();
    }
}
