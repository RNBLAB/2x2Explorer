using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Explorer.App.ViewModels;
using Explorer.Core.Models;

namespace Explorer.App.Views.Panes;

/// <summary>
/// 미리보기 패널의 화면입니다. DataContext는 PreviewViewModel입니다.
/// 끌어서 스크롤하기, 맞춤 방식(자동·너비·높이), Ctrl+휠 확대축소를 여기서 다룹니다.
/// </summary>
public partial class PreviewPane : UserControl
{
    private const double MinZoom = 0.2;
    private const double MaxZoom = 8.0;
    private const double ZoomStep = 1.15;

    /// <summary>
    /// 그림을 칸에 맞추는 방식입니다.
    /// 자동은 PDF 편집기와 같은 규칙입니다. 가로가 긴 그림은 너비, 세로가 긴 그림은 높이에 맞춥니다.
    /// 손은 Ctrl+휠로 배율을 정한 상태입니다.
    /// </summary>
    private enum Fit
    {
        Auto,
        Width,
        Height,
        Manual
    }

    /// <summary>고른 맞춤 방식입니다. 손 배율은 다른 파일로 넘어가면 이 방식으로 돌아갑니다.</summary>
    private Fit _chosenFit = Fit.Auto;

    private Fit _fit = Fit.Auto;

    /// <summary>손 배율일 때 칸 너비에 견준 그림 폭입니다. 1.0이면 칸 너비와 같습니다.</summary>
    private double _zoom = 1.0;

    private Point _dragOrigin;
    private double _dragStartVertical;
    private double _dragStartHorizontal;
    private bool _dragging;

    public PreviewPane()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        // 접혔다 펴질 때 핸들러를 내리고 다시 올립니다.
        IsVisibleChanged += (_, _) => ApplyHandlerPath();

        // 미리보기 프로그램이 한참 답하지 않으면 칸 위쪽 안내 줄에 알립니다.
        // 문서가 그려질 자리에는 WPF 글자를 겹쳐 올릴 수 없어 안내 줄을 씁니다.
        HandlerHost.Stalled += (_, _) => (DataContext as PreviewViewModel)?.ReportHandlerStalled();
        HandlerHost.Finished += (_, shown) => (DataContext as PreviewViewModel)?.ReportHandlerFinished(shown);

        // 한글 문서 위의 Ctrl+휠은 핸들러 창이 먼저 받으므로, 휠 훅이 가로채 여기로 보내 줍니다.
        HandlerHost.ZoomWheel += OnHandlerZoomWheel;
        HandlerHost.LayoutChanged += (_, _) => UpdateHandlerBars();
    }

    /// <summary>핸들러가 그리는 문서를 이 칸이 확대·맞춤하는 중인지입니다(쪽 모양을 아는 한글 문서).</summary>
    private bool HandlerMode =>
        DataContext is PreviewViewModel { ShowHandler: true, HandlerPageAspect: not null };

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is PreviewViewModel previous)
        {
            previous.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is PreviewViewModel current)
        {
            current.PropertyChanged += OnViewModelPropertyChanged;
        }

        ResetZoom();
        ApplyHandlerPath();
        ApplyCode();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 다른 파일을 보여 줄 때는 확대 배율을 처음으로 되돌립니다.
        if (e.PropertyName == nameof(PreviewViewModel.Image))
        {
            ResetZoom();
        }

        if (e.PropertyName == nameof(PreviewViewModel.HandlerPath))
        {
            ApplyHandlerPath();
        }

        // 핸들러 칸이 보이거나 사라지면 스크롤 막대 자리를 다시 정합니다.
        if (e.PropertyName is nameof(PreviewViewModel.ShowHandler) or nameof(PreviewViewModel.ShowFit))
        {
            UpdateHandlerBars();
            UpdateFitLabels();
        }

        // 구간은 글보다 나중에 정해지므로 이쪽만 들으면 됩니다.
        if (e.PropertyName == nameof(PreviewViewModel.CodeSpans))
        {
            ApplyCode();
        }
    }

    /// <summary>
    /// 색을 입힌 코드를 만들어 올립니다.
    ///
    /// 칠할 구간만 색을 주고 사이는 그대로 둡니다.
    /// 색은 이름으로 걸어 두어 테마를 바꾸면 따라 바뀝니다.
    /// </summary>
    private void ApplyCode()
    {
        if (DataContext is not PreviewViewModel viewModel || viewModel.CodeSpans.Count == 0)
        {
            CodeView.Document = new FlowDocument();
            return;
        }

        var text = viewModel.Text;
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        var index = 0;

        foreach (var span in viewModel.CodeSpans)
        {
            if (span.Start < index || span.Start + span.Length > text.Length || span.Length <= 0)
            {
                continue;
            }

            if (span.Start > index)
            {
                paragraph.Inlines.Add(new Run(text[index..span.Start]));
            }

            var piece = new Run(text.Substring(span.Start, span.Length));
            piece.SetResourceReference(TextElement.ForegroundProperty, ResourceFor(span.Kind));
            paragraph.Inlines.Add(piece);

            index = span.Start + span.Length;
        }

        if (index < text.Length)
        {
            paragraph.Inlines.Add(new Run(text[index..]));
        }

        // 줄을 접지 않으려고 쪽 너비를 넉넉히 잡습니다. 넘치는 만큼 가로로 굴러갑니다.
        CodeView.Document = new FlowDocument(paragraph)
        {
            PageWidth = 3000,
            PagePadding = new Thickness(6),
            FontFamily = CodeView.FontFamily,
            FontSize = CodeView.FontSize
        };
    }

    private static string ResourceFor(CodeSpanKind kind) => kind switch
    {
        CodeSpanKind.Comment => "Code.Comment",
        CodeSpanKind.Quoted => "Code.Quoted",
        CodeSpanKind.Number => "Code.Number",
        _ => "Code.Keyword"
    };

    /// <summary>
    /// 윈도우 미리보기 핸들러에게 보여 줄 파일을 건네줍니다.
    /// 보여 줄 것이 없으면 내려서 문서를 붙들고 있지 않게 합니다.
    /// </summary>
    private void ApplyHandlerPath()
    {
        var path = (DataContext as PreviewViewModel)?.HandlerPath;

        // 보이지 않는 자리에서는 핸들러를 띄우지 않습니다.
        // 띄우는 것만으로 만든 회사의 미리보기 프로그램이 뜨고, 그동안 화면이 멈춥니다.
        if (string.IsNullOrEmpty(path) || !IsVisible)
        {
            HandlerHost.Close();
            HandlerHost.PageAspect = null;
            return;
        }

        // 새 문서는 고른 맞춤 방식으로 시작합니다. 판 크기를 먼저 정해 두어야 핸들러가 처음부터 그 크기로 그립니다.
        HandlerHost.PageAspect = (DataContext as PreviewViewModel)?.HandlerPageAspect;
        // 이 순간에는 칸이 아직 핸들러 쪽으로 바뀌기 전일 수 있어 판에 직접 알립니다.
        _fit = _chosenFit;
        _zoom = 1.0;
        HandlerHost.SetFit(ToHandlerFit(_fit), _zoom);
        UpdateFitLabels();

        HandlerHost.Show(path);
    }

    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e) => ApplyZoom();

    private void OnScrollerMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Ctrl 없이 굴리면 평소처럼 스크롤합니다.
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        e.Handled = true;

        var viewport = ViewportWidth();

        if (viewport <= 0)
        {
            return;
        }

        // 지금 보이는 폭에서 출발해야 맞춤 상태에서 굴려도 튀지 않습니다.
        var current = PreviewImage.Width is > 0 and var width ? width / viewport : 1.0;
        var factor = e.Delta > 0 ? ZoomStep : 1 / ZoomStep;

        _fit = Fit.Manual;
        _zoom = Math.Clamp(current * factor, MinZoom, MaxZoom);
        ApplyZoom();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        // Ctrl+0 은 자동 맞춤으로 되돌립니다. PDF 편집기와 같은 키입니다.
        if (e.Key is Key.D0 or Key.NumPad0 && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && (PreviewImage.Source is not null || HandlerMode))
        {
            SetFit(Fit.Auto);
            e.Handled = true;
        }
    }

    private void OnFitButton(object sender, RoutedEventArgs e)
    {
        FitMenu.PlacementTarget = FitButton;
        FitMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        FitMenu.IsOpen = true;
    }

    private void OnFitAuto(object sender, RoutedEventArgs e) => SetFit(Fit.Auto);

    private void OnFitWidth(object sender, RoutedEventArgs e) => SetFit(Fit.Width);

    private void OnFitHeight(object sender, RoutedEventArgs e) => SetFit(Fit.Height);

    private void SetFit(Fit fit)
    {
        _chosenFit = fit;
        _fit = fit;
        _zoom = 1.0;
        ApplyZoom();
    }

    /// <summary>새 파일을 보여 줄 때입니다. 손 배율은 버리고 고른 맞춤 방식으로 돌아갑니다.</summary>
    private void ResetZoom()
    {
        _fit = _chosenFit;
        _zoom = 1.0;
        Scroller.ScrollToHome();
        ApplyZoom();
    }

    /// <summary>자동이면 그림 모양으로 정합니다. 가로가 더 긴 그림은 너비, 아니면 높이에 맞춥니다.</summary>
    private Fit EffectiveFit()
    {
        if (_fit != Fit.Auto)
        {
            return _fit;
        }

        return PreviewImage.Source is { Width: > 0, Height: > 0 } source && source.Width > source.Height
            ? Fit.Width
            : Fit.Height;
    }

    /// <summary>세로 스크롤 막대가 생겨도 흔들리지 않게 막대 폭을 뺀 너비를 씁니다.</summary>
    private double ViewportWidth() =>
        Scroller.ActualWidth > 0 ? Math.Max(1, Scroller.ActualWidth - SystemParameters.VerticalScrollBarWidth) : 0;

    /// <summary>
    /// 맞춤 방식을 그림 폭에 반영합니다. 높이는 Stretch=Uniform 이 비율대로 따라옵니다.
    ///
    /// 너비 맞춤과 높이 맞춤은 스크롤 막대 자리를 미리 비워 두고 셉니다.
    /// 막대가 생겼다 사라지며 폭이 바뀌고, 그 때문에 다시 막대가 생기는 되풀이를 막기 위해서입니다.
    /// </summary>
    private void ApplyZoom()
    {
        // 한글 문서: 판 크기를 바꾸는 것으로 맞춤·확대를 대신합니다.
        if (HandlerMode)
        {
            HandlerHost.SetFit(ToHandlerFit(_fit), _zoom);
            UpdateFitLabels();
            return;
        }

        var viewport = ViewportWidth();

        if (viewport <= 0 || PreviewImage.Source is not { Width: > 0, Height: > 0 } source)
        {
            UpdateFitLabels();
            return;
        }

        var height = Math.Max(1, Scroller.ActualHeight - SystemParameters.HorizontalScrollBarHeight);

        PreviewImage.Width = EffectiveFit() switch
        {
            Fit.Width => viewport,
            Fit.Height => Math.Max(1, height * source.Width / source.Height),
            _ => Math.Max(1, viewport * _zoom)
        };

        UpdateFitLabels();
    }

    private static HandlerFit ToHandlerFit(Fit fit) => fit switch
    {
        Fit.Width => HandlerFit.Width,
        Fit.Height => HandlerFit.Height,
        Fit.Manual => HandlerFit.Manual,
        _ => HandlerFit.Auto
    };

    /// <summary>한글 문서 위에서 Ctrl+휠: 마우스가 가리키는 자리를 붙든 채 판을 키우거나 줄입니다.</summary>
    private void OnHandlerZoomWheel(object? sender, Explorer.Shell.Preview.WheelInput input)
    {
        if (!HandlerMode)
        {
            return;
        }

        // 지금 보이는 크기에서 출발해야 맞춤 상태에서 굴려도 튀지 않습니다.
        var current = _fit == Fit.Manual ? _zoom : HandlerHost.WidthRatio;
        var factor = input.Delta > 0 ? ZoomStep : 1 / ZoomStep;

        _fit = Fit.Manual;
        _zoom = Math.Clamp(current * factor, MinZoom, MaxZoom);
        HandlerHost.SetFit(HandlerFit.Manual, _zoom, input);
        UpdateFitLabels();
    }

    /// <summary>판이 칸보다 크면 스크롤 막대를 보이고 위치를 맞춥니다. 작으면 자리만 남겨 두어 칸 크기가 출렁이지 않게 합니다.</summary>
    private void UpdateHandlerBars()
    {
        if (!HandlerMode)
        {
            HandlerVerticalBar.Visibility = Visibility.Collapsed;
            HandlerHorizontalBar.Visibility = Visibility.Collapsed;
            return;
        }

        SetBar(HandlerVerticalBar, HandlerHost.ExtentHeight, HandlerHost.ViewportHeight, HandlerHost.OffsetY);
        SetBar(HandlerHorizontalBar, HandlerHost.ExtentWidth, HandlerHost.ViewportWidth, HandlerHost.OffsetX);
    }

    private static void SetBar(System.Windows.Controls.Primitives.ScrollBar bar, int extent, int viewport, int offset)
    {
        var range = Math.Max(0, extent - viewport);

        bar.Visibility = range > 0 ? Visibility.Visible : Visibility.Hidden;
        bar.Minimum = 0;
        bar.Maximum = range;
        bar.ViewportSize = Math.Max(1, viewport);
        bar.LargeChange = Math.Max(1, viewport * 0.9);
        bar.SmallChange = 60;
        bar.Value = Math.Clamp(offset, 0, range);
    }

    private void OnHandlerScroll(object sender, System.Windows.Controls.Primitives.ScrollEventArgs e) =>
        HandlerHost.ScrollTo((int)HandlerHorizontalBar.Value, (int)HandlerVerticalBar.Value);

    private void UpdateFitLabels()
    {
        FitButton.Content = _fit switch
        {
            Fit.Width => "너비 맞춤 ▾",
            Fit.Height => "높이 맞춤 ▾",
            Fit.Manual => "손 배율 ▾",
            _ => "자동 맞춤 ▾"
        };

        FitAutoItem.IsChecked = _fit == Fit.Auto;
        FitWidthItem.IsChecked = _fit == Fit.Width;
        FitHeightItem.IsChecked = _fit == Fit.Height;

        if (_fit != Fit.Manual)
        {
            ZoomLabel.Visibility = Visibility.Collapsed;
            return;
        }

        ZoomLabel.Text = string.Format(CultureInfo.CurrentCulture, "{0:0}%", _zoom * 100);
        ZoomLabel.Visibility = Visibility.Visible;
    }

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        // 다 보이는 그림은 끌 이유가 없습니다.
        if (Scroller.ScrollableHeight <= 0 && Scroller.ScrollableWidth <= 0)
        {
            return;
        }

        _dragOrigin = e.GetPosition(Scroller);
        _dragStartVertical = Scroller.VerticalOffset;
        _dragStartHorizontal = Scroller.HorizontalOffset;
        _dragging = true;

        Scroller.CaptureMouse();
        Scroller.Cursor = Cursors.SizeAll;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        // 잡은 지점이 손끝을 따라오도록, 움직인 만큼 반대로 굴립니다.
        var position = e.GetPosition(Scroller);
        Scroller.ScrollToVerticalOffset(_dragStartVertical - (position.Y - _dragOrigin.Y));
        Scroller.ScrollToHorizontalOffset(_dragStartHorizontal - (position.X - _dragOrigin.X));
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e) => StopDragging();

    private void OnDragCancel(object sender, MouseEventArgs e) => StopDragging();

    private void StopDragging()
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;

        if (Scroller.IsMouseCaptured)
        {
            Scroller.ReleaseMouseCapture();
        }

        Scroller.Cursor = null;
    }
}
