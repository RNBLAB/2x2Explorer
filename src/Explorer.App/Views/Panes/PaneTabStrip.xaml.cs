using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Explorer.App.ViewModels;
using Explorer.App.Views.Dialogs;
using Explorer.Core.Services;

namespace Explorer.App.Views.Panes;

/// <summary>패널 상단 바의 탭 스트립입니다. DataContext는 PaneViewModel입니다.</summary>
public partial class PaneTabStrip : UserControl
{
    /// <summary>탭 줄 밖으로 이만큼 벗어나면 다른 패널로 옮기려는 뜻으로 봅니다.</summary>
    private const double OutsideMargin = 10;

    private FileListViewModel? _pressedTab;
    private Point _pressPoint;
    private bool _reordering;

    /// <summary>
    /// 오른쪽 단추로 끄는 중인지입니다. 오른쪽으로 끌어 다른 칸에 놓으면 복사·이동을 묻는 메뉴를 띄웁니다.
    /// 윈도우 탐색기에서 파일을 오른쪽 단추로 끌 때와 같은 방식입니다.
    /// </summary>
    private bool _rightDrag;

    /// <summary>탭 안에서 집은 자리입니다. 그림자가 손끝에 붙어 따라오도록 이만큼 빼 줍니다.</summary>
    private Point _grabOffset;

    public PaneTabStrip()
    {
        InitializeComponent();
    }

    private PaneViewModel? ViewModel => DataContext as PaneViewModel;

    private void OnTabMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FileListViewModel tab } || ViewModel is null)
        {
            return;
        }

        switch (e.ChangedButton)
        {
            case MouseButton.Left:
                ViewModel.SelectedTab = tab;

                // 끌어서 순서를 바꿀 수도 있으니 눌린 지점을 기억해 둡니다.
                _pressedTab = tab;
                _pressPoint = e.GetPosition(Scroller);
                _grabOffset = e.GetPosition((FrameworkElement)sender);
                break;

            case MouseButton.Middle:
                // 가운데 클릭으로 닫는 것은 브라우저와 같은 관습입니다.
                e.Handled = true;
                ViewModel.CloseTab(tab);
                break;

            case MouseButton.Right:
                // 오른쪽 클릭한 탭을 먼저 고릅니다.
                // 그래야 이어서 열리는 메뉴의 "탭 닫기"가 그 탭에 적용됩니다.
                ViewModel.SelectedTab = tab;

                // 오른쪽 단추로 끌어 다른 칸에 옮길 수도 있으니 눌린 지점을 기억해 둡니다.
                // 끌지 않고 떼면 평소처럼 탭 줄 메뉴가 열립니다.
                _pressedTab = tab;
                _rightDrag = true;
                _pressPoint = e.GetPosition(Scroller);
                _grabOffset = e.GetPosition((FrameworkElement)sender);
                break;
        }
    }

    private void OnStripMouseMove(object sender, MouseEventArgs e)
    {
        var held = _rightDrag ? e.RightButton : e.LeftButton;

        if (_pressedTab is null || ViewModel is null || held != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(Scroller);

        if (!_reordering)
        {
            // 손이 조금 떨린 것과 끌기를 가릅니다. 윈도우가 정한 기준을 씁니다.
            // 아래위로 끌어 다른 패널로 옮기는 길도 있으므로 세로도 함께 봅니다.
            if (Math.Abs(position.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(position.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _reordering = true;
            Scroller.CaptureMouse();
            ShowGhost(_pressedTab);
        }

        MoveGhost(position);

        // 탭 줄 밖으로 끌고 나갔으면 다른 패널로 옮기려는 뜻입니다.
        // 어느 패널인지는 손을 놓는 자리에서 정하므로, 여기서는 모양만 바꿔 알려 줍니다.
        if (IsOutside(position))
        {
            Scroller.Cursor = Cursors.Hand;
            return;
        }

        // 오른쪽 끌기와 Ctrl+왼쪽 끌기는 손을 뗄 때 한 번에 처리합니다(메뉴 또는 복사).
        // 끄는 동안 탭을 옮겨 버리면 복사인데도 원래 탭 자리가 바뀌므로 여기서는 순서를 건드리지 않습니다.
        if (_rightDrag || CopyKeyHeld)
        {
            Scroller.Cursor = null;
            return;
        }

        Scroller.Cursor = Cursors.SizeWE;

        var target = IndexAt(position);

        if (target >= 0)
        {
            ViewModel.MoveTab(_pressedTab, target);
        }
    }

    /// <summary>
    /// 왼쪽 단추로 끌다가 뗐을 때입니다. 윈도우 탐색기처럼 기본은 이동, Ctrl 을 누르고 있으면 복사입니다.
    /// - 탭 줄 안: 이동은 끄는 동안 이미 자리를 바꿨습니다. 복사는 놓은 자리에 같은 폴더의 탭을 새로 끼웁니다.
    /// - 다른 탐색기 칸: 이동은 탭을 넘기고, 복사는 그 칸에 같은 폴더의 탭을 새로 엽니다.
    /// Ctrl 은 뗄 때 기준으로 봅니다. 끄는 도중에 눌러도 복사가 됩니다.
    /// </summary>
    private void OnStripMouseUp(object sender, MouseButtonEventArgs e)
    {
        var tab = _pressedTab;
        var dragged = _reordering && !_rightDrag;
        var position = e.GetPosition(Scroller);
        var copy = CopyKeyHeld;

        StopReordering();

        if (!dragged || tab is null || ViewModel is not { } pane)
        {
            return;
        }

        if (!IsOutside(position))
        {
            if (copy)
            {
                pane.CopyTabAt(tab, InsertionIndexAt(position));
            }

            return;
        }

        if (PaneUnder(position) is { } target && !ReferenceEquals(target, pane))
        {
            if (copy)
            {
                pane.CopyTabTo(target, tab);
            }
            else
            {
                pane.MoveTabTo(target, tab);
            }
        }
    }

    /// <summary>복사를 뜻하는 글쇠(Ctrl)를 누르고 있는지입니다.</summary>
    private static bool CopyKeyHeld => (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

    /// <summary>
    /// 그 자리에 놓으면 탭이 들어갈 차례(0~탭 수)입니다. 탭의 왼쪽 반이면 그 앞, 오른쪽 반이면 그 뒤입니다.
    /// 마지막 탭보다 오른쪽이면 맨 뒤입니다.
    /// </summary>
    private int InsertionIndexAt(Point position)
    {
        var count = TabItems.Items.Count;

        for (var i = 0; i < count; i++)
        {
            if (TabItems.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container
                || !container.IsVisible)
            {
                continue;
            }

            var left = container.TranslatePoint(new Point(0, 0), Scroller).X;

            if (position.X < left + container.ActualWidth / 2)
            {
                return i;
            }
        }

        return count;
    }

    /// <summary>
    /// 끌고 있는 탭을 그대로 본뜬 그림자를 띄웁니다.
    ///
    /// 살아 있는 VisualBrush 가 아니라 한 장 찍어 둔 그림을 씁니다.
    /// 끄는 동안 탭 차례가 바뀌면서 원본 껍데기가 다른 탭의 것으로 넘어가기 때문입니다.
    /// </summary>
    private void ShowGhost(FileListViewModel tab)
    {
        var index = ViewModel?.Tabs.IndexOf(tab) ?? -1;

        if (index < 0
            || TabItems.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container
            || container.ActualWidth <= 0)
        {
            return;
        }

        GhostFill.Width = container.ActualWidth;
        GhostFill.Height = container.ActualHeight;

        var dpi = VisualTreeHelper.GetDpi(container);
        var width = (int)Math.Ceiling(container.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(container.ActualHeight * dpi.DpiScaleY);

        if (width <= 0 || height <= 0)
        {
            return;
        }

        var shot = new RenderTargetBitmap(
            width,
            height,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);

        shot.Render(container);
        shot.Freeze();

        GhostFill.Fill = new ImageBrush(shot) { Stretch = Stretch.Fill };

        DragGhost.IsOpen = true;
    }

    /// <summary>그림자를 마우스 자리로 옮깁니다.</summary>
    private void MoveGhost(Point position)
    {
        if (!DragGhost.IsOpen)
        {
            return;
        }

        Point screen;

        try
        {
            screen = Scroller.PointToScreen(position);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        // PointToScreen 은 실제 화면 픽셀을 주고 Popup 은 WPF 단위를 씁니다.
        // 화면 배율이 100%가 아니면 이 변환을 빠뜨렸을 때 그림자가 엉뚱한 데로 갑니다.
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
        {
            screen = target.TransformFromDevice.Transform(screen);
        }

        DragGhost.HorizontalOffset = screen.X - _grabOffset.X;
        DragGhost.VerticalOffset = screen.Y - _grabOffset.Y;
    }

    private void HideGhost()
    {
        DragGhost.IsOpen = false;
        GhostFill.Fill = null;
    }

    private bool IsOutside(Point position) =>
        position.X < -OutsideMargin
        || position.Y < -OutsideMargin
        || position.X > Scroller.ActualWidth + OutsideMargin
        || position.Y > Scroller.ActualHeight + OutsideMargin;

    /// <summary>그 자리에 놓인 패널입니다. 파일 패널이 아니면 없는 것으로 봅니다.</summary>
    private PaneViewModel? PaneUnder(Point position)
    {
        if (Window.GetWindow(this) is not { } window)
        {
            return null;
        }

        Point local;

        try
        {
            local = window.PointFromScreen(Scroller.PointToScreen(position));
        }
        catch (InvalidOperationException)
        {
            // 창에 아직 붙지 않은 경우입니다.
            return null;
        }

        foreach (var host in Descendants<PaneHost>(window))
        {
            if (!host.IsVisible || host.Pane is not { } pane)
            {
                continue;
            }

            var origin = host.TranslatePoint(new Point(0, 0), window);

            if (new Rect(origin, new Size(host.ActualWidth, host.ActualHeight)).Contains(local))
            {
                return pane;
            }
        }

        return null;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T match)
            {
                yield return match;
                continue;
            }

            foreach (var deeper in Descendants<T>(child))
            {
                yield return deeper;
            }
        }
    }

    /// <summary>
    /// 오른쪽 단추로 끌다가 뗐을 때입니다. 다른 탐색기 칸 위면 "여기로 복사 / 여기로 이동" 메뉴를 띄웁니다.
    /// 끌었으면 탭 줄 메뉴가 뒤따라 열리지 않게 이 뗌을 여기서 삼킵니다.
    /// </summary>
    private void OnStripRightMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_rightDrag)
        {
            return;
        }

        var tab = _pressedTab;
        var dragged = _reordering;
        var position = e.GetPosition(Scroller);

        StopReordering();

        if (!dragged)
        {
            // 끌지 않았습니다. 평소의 오른쪽 클릭이므로 메뉴가 열리게 둡니다.
            return;
        }

        e.Handled = true;

        if (tab is null || ViewModel is not { } source)
        {
            return;
        }

        // 같은 탭 줄 안: 놓은 자리에 복사하거나 그 자리로 옮깁니다.
        if (!IsOutside(position))
        {
            var index = InsertionIndexAt(position);
            ShowCopyMoveMenu(
                () => source.CopyTabAt(tab, index),
                () => source.PlaceTab(tab, index));
            return;
        }

        // 다른 탐색기 칸. 같은 칸의 목록이나 미리보기 칸 위에 놓았으면 아무 일도 하지 않습니다.
        if (PaneUnder(position) is not { } target || ReferenceEquals(target, source))
        {
            return;
        }

        ShowCopyMoveMenu(
            () => source.CopyTabTo(target, tab),
            () => source.MoveTabTo(target, tab));
    }

    /// <summary>탭을 복사할지 옮길지 묻습니다. 마우스를 뗀 자리에 뜹니다.</summary>
    private void ShowCopyMoveMenu(Action copy, Action move)
    {
        var menu = new ContextMenu
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
            PlacementTarget = this
        };

        if (TryFindResource("DarkContextMenu") is Style menuStyle)
        {
            menu.Style = menuStyle;
        }

        var itemStyle = TryFindResource("DarkMenuItem") as Style;

        MenuItem Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };

            if (itemStyle is not null)
            {
                item.Style = itemStyle;
            }

            item.Click += (_, _) => action();
            return item;
        }

        menu.Items.Add(Item("여기로 탭 복사", copy));
        menu.Items.Add(Item("여기로 탭 이동", move));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("취소", () => { }));

        menu.IsOpen = true;
    }

    private void OnStripLostCapture(object sender, MouseEventArgs e) => StopReordering();

    private void StopReordering()
    {
        _pressedTab = null;
        _rightDrag = false;

        if (!_reordering)
        {
            return;
        }

        _reordering = false;
        HideGhost();

        if (Scroller.IsMouseCaptured)
        {
            Scroller.ReleaseMouseCapture();
        }

        Scroller.Cursor = null;
    }

    /// <summary>가로 좌표에 걸리는 탭의 번호입니다. 없으면 -1입니다.</summary>
    private int IndexAt(Point position)
    {
        for (var i = 0; i < TabItems.Items.Count; i++)
        {
            if (TabItems.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container
                || !container.IsVisible)
            {
                continue;
            }

            var left = container.TranslatePoint(new Point(0, 0), Scroller).X;

            if (position.X >= left && position.X <= left + container.ActualWidth)
            {
                return i;
            }
        }

        return -1;
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // 메뉴는 시각 트리 밖에 있어 DataContext가 자동으로 흘러들지 않는 경우가 있습니다.
        if (Root.ContextMenu is null)
        {
            return;
        }

        Root.ContextMenu.DataContext = DataContext;
        FillFavorites(Root.ContextMenu);
        UpdateAddFavoriteState(Root.ContextMenu);
    }

    /// <summary>이 탭 줄을 담은 칸을 미리보기 칸으로 바꿔 달라고 합니다.</summary>
    private void OnSwitchToPreview(object sender, RoutedEventArgs e) =>
        PaneHost.FindOwner(this)?.RequestRole(Explorer.Core.Models.PaneRole.Preview);

    private void UpdateAddFavoriteState(ContextMenu menu)
    {
        foreach (var entry in menu.Items)
        {
            if (entry is MenuItem { Tag: "addfavorite" } item)
            {
                item.IsEnabled = ViewModel is not null && ViewModel.CurrentFolderPath.Length > 0;
                return;
            }
        }
    }

    /// <summary>
    /// 지금 보고 있는 폴더를 즐겨찾기에 넣습니다.
    /// 바로 넣지 않고 편집 창을 띄웁니다. 열 칸이 다 찼을 때 어느 자리를 내줄지
    /// 사용자가 골라야 하기 때문입니다.
    /// </summary>
    private void OnAddFavorite(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var path = ViewModel.CurrentFolderPath;

        if (path.Length == 0)
        {
            return;
        }

        var dialog = new FavoritesWindow(ViewModel.AppSettings, path)
        {
            Owner = Window.GetWindow(this)
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        ViewModel.AppSettings.SetFavorites(dialog.Result, dialog.TitleBar);
        ViewModel.SaveSettings();
        ViewModel.NotifyFavoritesChanged();
    }

    /// <summary>
    /// 즐겨찾기 하위 메뉴를 채웁니다.
    /// 스타일의 Setter.Value에는 Binding을 넣을 수 없어, ItemsSource로는 명령을 걸 수 없습니다.
    /// 그래서 열릴 때마다 코드에서 항목을 만듭니다.
    /// </summary>
    private void FillFavorites(ContextMenu menu)
    {
        MenuItem? host = null;

        foreach (var entry in menu.Items)
        {
            if (entry is MenuItem { Tag: "favorites" } candidate)
            {
                host = candidate;
                break;
            }
        }

        if (host is null || ViewModel is null)
        {
            return;
        }

        host.Items.Clear();

        var favorites = ViewModel.Favorites;

        if (favorites.Count == 0)
        {
            host.Items.Add(new MenuItem { Header = "등록된 즐겨찾기가 없습니다", IsEnabled = false });
            host.IsEnabled = false;
            return;
        }

        host.IsEnabled = true;

        foreach (var path in favorites)
        {
            var item = new MenuItem
            {
                Header = DescribeFavorite(path),
                ToolTip = SpecialPaths.IsVirtual(path) ? SpecialPaths.ToDisplay(path) : PathHelper.Expand(path)
            };
            var target = path;
            item.Click += (_, _) => ViewModel?.NavigateSelectedTo(target);
            host.Items.Add(item);
        }
    }

    private static string DescribeFavorite(string path)
    {
        // 내 PC · 바탕 화면 · 다운로드 · 문서 표시는 그 이름으로 보입니다.
        if (SpecialPaths.IsVirtual(path) || SpecialPaths.IsKnownFolder(path))
        {
            return SpecialPaths.ToDisplay(path);
        }

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : path;
    }

    /// <summary>탭을 모두 닫고 시작 위치 하나만 엽니다.</summary>
    private void OnResetTabs(object sender, RoutedEventArgs e) => ViewModel?.ResetTabs();

    /// <summary>한 번 누를 때 미는 거리입니다. 탭 하나 너비쯤입니다.</summary>
    private const double ScrollStep = 100;

    /// <summary>탭이 넘칠 때만 좌우 단추를 내놓고, 끝에 닿으면 그쪽 단추를 흐립니다.</summary>
    private void OnStripScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var overflow = Scroller.ScrollableWidth > 0.5;

        ScrollLeftButton.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        ScrollRightButton.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;

        if (!overflow)
        {
            return;
        }

        ScrollLeftButton.IsEnabled = Scroller.HorizontalOffset > 0.5;
        ScrollRightButton.IsEnabled = Scroller.HorizontalOffset < Scroller.ScrollableWidth - 0.5;
    }

    private void OnScrollLeft(object sender, RoutedEventArgs e) =>
        Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset - ScrollStep);

    private void OnScrollRight(object sender, RoutedEventArgs e) =>
        Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset + ScrollStep);

    private void OnStripMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 스크롤 막대를 숨겨 두었으므로 휠로 좌우로 굴립니다.
        Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    // ── 폴더를 끌어다 놓아 새 탭 열기 ──

    /// <summary>
    /// 끌고 있는 것 가운데 폴더만 골라 냅니다. 파일은 뺍니다(여기서 열 방법이 없습니다).
    /// 목록에서 끌든 윈도우 탐색기에서 끌든 FileDrop 으로 옵니다.
    /// </summary>
    private static List<string> DroppedFolders(System.Windows.IDataObject data)
    {
        var folders = new List<string>();

        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop)
                && data.GetData(DataFormats.FileDrop) is string[] paths)
            {
                foreach (var path in paths)
                {
                    if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                    {
                        folders.Add(path);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
        {
            // 다른 프로그램이 넘긴 자료를 못 읽는 일이 있습니다. 받지 않는 것으로 칩니다.
        }

        return folders;
    }

    /// <summary>
    /// 받을 수 있으면 "바로가기"(없으면 복사) 모양을 보여 줍니다.
    /// 이동을 고르면 끌어 온 쪽이 원본을 지울 수 있으므로 절대 이동으로 답하지 않습니다.
    /// </summary>
    private void OnStripDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (ViewModel is null || DroppedFolders(e.Data).Count == 0)
        {
            e.Effects = DragDropEffects.None;
            ShowDropHint(false);
            return;
        }

        e.Effects = (e.AllowedEffects & DragDropEffects.Link) != 0
            ? DragDropEffects.Link
            : (e.AllowedEffects & DragDropEffects.Copy) != 0
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        ShowDropHint(e.Effects != DragDropEffects.None);
    }

    private void OnStripDragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ShowDropHint(false);
    }

    /// <summary>
    /// 놓은 폴더마다 새 탭을 뒤에 붙입니다. 여러 개면 첫 폴더의 탭을 고릅니다.
    /// 끌어 온 쪽에는 "바로가기"로 답하므로 원본은 그대로입니다.
    /// </summary>
    private void OnStripDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ShowDropHint(false);

        if (ViewModel is not { } pane)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        var folders = DroppedFolders(e.Data);

        if (folders.Count == 0)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = (e.AllowedEffects & DragDropEffects.Link) != 0
            ? DragDropEffects.Link
            : DragDropEffects.Copy;

        for (var i = 0; i < folders.Count; i++)
        {
            pane.AddTab(folders[i], select: i == 0, defer: i > 0);
        }
    }

    /// <summary>끌어 온 폴더를 받을 수 있을 때 탭 줄 바탕을 옅게 칠해 알려 줍니다.</summary>
    private void ShowDropHint(bool on)
    {
        Root.Background = on && TryFindResource("Row.Hover") is System.Windows.Media.Brush hover
            ? hover
            : System.Windows.Media.Brushes.Transparent;
    }
}
