using System;
using System.Windows;
using System.Windows.Controls;
using Explorer.App.ViewModels;

namespace Explorer.App.Views.Panes;

/// <summary>
/// 패널 본문입니다. 왼쪽이 파일 목록, 오른쪽이 미리보기입니다.
/// DataContext는 PaneViewModel입니다.
/// </summary>
public partial class PaneBody : UserControl
{
    /// <summary>A4 세로비율입니다. 210mm : 297mm.</summary>
    private const double A4Ratio = 210.0 / 297.0;

    /// <summary>미리보기가 패널의 이 비율을 넘지 않게 합니다. 목록이 너무 좁아지면 쓸 수 없습니다.</summary>
    private const double MaxWidthShare = 0.5;

    /// <summary>탐색 창이 이보다 좁아지면 폴더 이름이 잘려 못 읽습니다.</summary>
    private const double NarrowestTree = 120;

    /// <summary>목록이 이보다 좁아지면 파일 이름이 남지 않습니다.</summary>
    private const double NarrowestList = 200;

    /// <summary>미리보기가 이보다 좁아지면 쓸모가 없습니다.</summary>
    /// <remarks>이름을 MinWidth로 두면 UserControl이 이미 가진 것을 가려 경고가 납니다.</remarks>
    private const double NarrowestPreview = 140;

    public PaneBody()
    {
        InitializeComponent();
        DataContextChanged += OnPaneChanged;
    }

    private PaneViewModel? Pane => DataContext as PaneViewModel;

    private void OnPaneChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is PaneViewModel previous)
        {
            previous.PropertyChanged -= OnPanePropertyChanged;
        }

        if (e.NewValue is PaneViewModel current)
        {
            current.PropertyChanged += OnPanePropertyChanged;
        }

        ApplyTreeWidth();
    }

    private void OnPanePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PaneViewModel.ShowTree)
            or nameof(PaneViewModel.TreeOnRight)
            or nameof(PaneViewModel.TreeSplit))
        {
            ApplyTreeWidth();
        }
    }

    /// <summary>
    /// 탐색 창과 목록을 설정대로 놓습니다. 너비뿐 아니라 어느 쪽에 설지도 여기서 정합니다.
    ///
    /// 접어 둔 때에는 칸을 0으로 누르고 최소 너비까지 풀어 줍니다.
    /// 최소 너비를 남겨 두면 너비가 0이어도 그만큼 빈 자리가 남아, 분할 방식을 바꾼 뒤
    /// 탐색 창을 배정한 것처럼 보입니다.
    /// </summary>
    private void ApplyTreeWidth()
    {
        if (Pane is not { } pane || !pane.ShowTree)
        {
            Grid.SetColumn(ListBox, 0);
            FrontColumn.Width = new GridLength(1, GridUnitType.Star);
            FrontColumn.MinWidth = 0;
            TreeSplitColumn.Width = new GridLength(0);
            BackColumn.Width = new GridLength(0);
            BackColumn.MinWidth = 0;
            return;
        }

        var share = Math.Clamp(pane.TreeSplit, 0.15, 0.7);
        var onRight = pane.TreeOnRight;

        Grid.SetColumn(TreeBox, onRight ? 2 : 0);
        Grid.SetColumn(ListBox, onRight ? 0 : 2);

        // 가룻대 쪽으로만 틈을 둡니다.
        TreeBox.Padding = onRight ? new Thickness(6, 0, 0, 0) : new Thickness(0, 0, 6, 0);

        var treeColumn = onRight ? BackColumn : FrontColumn;
        var listColumn = onRight ? FrontColumn : BackColumn;

        treeColumn.Width = new GridLength(share, GridUnitType.Star);
        treeColumn.MinWidth = NarrowestTree;
        listColumn.Width = new GridLength(1 - share, GridUnitType.Star);
        listColumn.MinWidth = NarrowestList;
        TreeSplitColumn.Width = GridLength.Auto;
    }

    /// <summary>가룻대를 놓았을 때의 너비를 설정에 적어 둡니다.</summary>
    private void OnTreeSplitterMoved(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (Pane is not { } pane || !pane.ShowTree)
        {
            return;
        }

        var onRight = pane.TreeOnRight;
        var tree = onRight ? BackColumn.ActualWidth : FrontColumn.ActualWidth;
        var list = onRight ? FrontColumn.ActualWidth : BackColumn.ActualWidth;
        var total = tree + list;

        if (total <= 0)
        {
            return;
        }

        pane.TreeSplit = tree / total;
    }

    /// <summary>나무에서 폴더를 고르면 그 칸의 지금 탭을 그 폴더로 옮깁니다.</summary>
    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not FolderNodeViewModel node
            || node.FullPath.Length == 0
            || DataContext is not PaneViewModel pane)
        {
            return;
        }

        pane.NavigateSelectedTo(node.FullPath);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 높이에 A4 비율을 곱해 폭을 잡습니다.
        // XAML 바인딩으로 자기 높이를 참조하면 배치가 한 박자 늦게 따라와 깜빡입니다.
        var byRatio = ActualHeight * A4Ratio;
        var byShare = ActualWidth * MaxWidthShare;
        var width = Math.Min(byRatio, byShare);

        PreviewBox.Width = Math.Max(width, NarrowestPreview);
    }
}
