using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Explorer.App.Services;
using Explorer.App.ViewModels;
using Explorer.App.Views.Dialogs;
using Explorer.App.Views.Panes;
using Explorer.Core.Models;
using Explorer.Core.Services;
using Explorer.Core.Settings;
using Explorer.Shell.Interop;
using Explorer.Shell.Operations;

namespace Explorer.App.Views;

[SupportedOSPlatform("windows")]
public partial class MainWindow : Window
{
    private readonly AppHost _host;
    private PaneHost[] _panes = Array.Empty<PaneHost>();
    private PaneHost? _activePane;

    /// <summary>설정 저장 실패를 창으로 알린 적이 있는지입니다. 한 번이면 충분합니다.</summary>
    private bool _saveFailureShown;

    public MainWindow(AppHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));

        var watch = System.Diagnostics.Stopwatch.StartNew();

        InitializeComponent();

        var built = watch.ElapsedMilliseconds;

        WindowPlacementService.Restore(this, _host.Settings.Window);

        var placed = watch.ElapsedMilliseconds;

        InitializePanes();

        var paned = watch.ElapsedMilliseconds;

        BuildSettingsMenu();
        BuildFavoriteBar();
        ApplyBarMode();
        ApplyBatchRenameButton();

        DiagnosticLog.Write(
            $"창 만들기 모두 {watch.ElapsedMilliseconds}ms — 껍데기 읽기 {built}ms,"
                + $" 자리 잡기 {placed - built}ms, 칸 놓기 {paned - placed}ms,"
                + $" 메뉴 {watch.ElapsedMilliseconds - paned}ms");

        // 설정을 못 저장하면 바꾼 것이 다음 실행에서 사라집니다. 그때 알 수 있어야 합니다.
        _host.SettingsService.SaveFailed += OnSettingsSaveFailed;

        // 어느 칸에서 즐겨찾기를 고치든 네 칸의 탐색 창이 같이 바뀌어야 합니다.
        _host.FavoritesChanged += OnFavoritesChanged;

        // 검색 답신은 창 메시지로 옵니다. 창 손잡이가 생긴 뒤에야 귀를 달 수 있습니다.
        SourceInitialized += OnSourceInitialized;
    }

    /// <summary>
    /// 설정 저장이 실패했을 때입니다. 상태 줄에 한 줄 남기고, 처음 한 번만 창으로도 알립니다.
    /// 저장은 창을 닫을 때에도 일어나므로 매번 창을 띄우면 성가십니다.
    /// </summary>
    private void OnSettingsSaveFailed(string message)
    {
        var pane = _activePane?.Pane ?? FirstExplorerPane()?.Pane;
        pane?.SelectedTab?.ReportProblem(message);

        // 창이 이미 닫히는 중이면 창을 띄울 자리가 없습니다. 기록에는 이미 남았습니다.
        if (_saveFailureShown || !IsLoaded || !IsVisible)
        {
            return;
        }

        _saveFailureShown = true;

        MessageBox.Show(
            this,
            $"{message}{Environment.NewLine}{Environment.NewLine}"
                + $"설정 파일: {_host.SettingsService.SettingsFilePath}",
            "2x2 Explorer",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // 제목 줄을 직접 그리는 창은 최대화할 때 화면 전체를 차지해 작업 표시줄을 덮습니다.
        // 윈도우가 최대화 크기를 물어 올 때 작업 영역에 맞춰 돌려줍니다.
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(OnWindowMessage);
        }

        // 지난번에 쓰던 모니터와 크기로 되돌립니다. 손잡이가 생긴 지금이라야 제 모니터를 찾습니다.
        WindowPlacementService.ApplySavedPlacement(this, _host.Settings.Window);

        _host.Search.Attach(this);

        foreach (var pane in _panes)
        {
            pane.NotifySearchReady();
        }
    }

    /// <summary>마지막으로 클릭하거나 포커스를 받은 패널입니다. 즐겨찾기 명령이 여기에 적용됩니다.</summary>
    public PaneSettings? ActivePane => _activePane?.Settings;

    private void InitializePanes()
    {
        var settings = _host.Settings;
        _panes = new PaneHost[AppSettings.PaneCount];

        // AppSettings.Normalize()가 패널 4개를 인덱스 순서대로 보장합니다.
        for (var i = 0; i < _panes.Length; i++)
        {
            _panes[i] = CreatePane(settings.Panes[i]);
        }

        BuildLayout();
        SetActivePane(FirstExplorerPane());
    }

    private PaneHost CreatePane(PaneSettings settings)
    {
        var host = new PaneHost { Margin = new Thickness(3) };
        host.Initialize(_host, settings);
        host.Activated += OnPaneActivated;
        host.RoleChangeRequested += OnPaneRoleChangeRequested;

        if (host.Pane is { } pane)
        {
            pane.SelectionChanged += OnPaneSelectionChanged;
        }

        return host;
    }

    private PaneHost? FirstExplorerPane()
    {
        var visible = AppSettings.PaneCountFor(_host.Settings.Layout);

        for (var i = 0; i < visible && i < _panes.Length; i++)
        {
            if (!_panes[i].IsPreviewPane)
            {
                return _panes[i];
            }
        }

        return _panes.Length > 0 ? _panes[0] : null;
    }

    /// <summary>
    /// 고른 분할 방식대로 칸을 놓습니다.
    ///
    /// 어느 방식이든 3×3 격자 하나면 됩니다.
    /// 가운데 줄과 가운데 칸은 가룻대 자리이고, 칸을 옆이나 아래로 이어 붙이면 3분할과 2분할이 나옵니다.
    /// </summary>
    private void BuildLayout()
    {
        var settings = _host.Settings;

        PaneArea.Children.Clear();
        PaneArea.RowDefinitions.Clear();
        PaneArea.ColumnDefinitions.Clear();

        PaneArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(settings.RowSplit, GridUnitType.Star), MinHeight = 120 });
        PaneArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(SplitterThickness) });
        PaneArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1 - settings.RowSplit, GridUnitType.Star), MinHeight = 120 });

        PaneArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(settings.ColumnSplit, GridUnitType.Star), MinWidth = 200 });
        PaneArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SplitterThickness) });
        PaneArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - settings.ColumnSplit, GridUnitType.Star), MinWidth = 200 });

        var count = AppSettings.PaneCountFor(settings.Layout);

        for (var i = 0; i < _panes.Length; i++)
        {
            _panes[i].Visibility = i < count ? Visibility.Visible : Visibility.Collapsed;
            // 탐색 창은 세로로 긴 칸에서만 붙입니다. 납작한 칸에 붙이면 목록이 못 쓸 만큼 좁아집니다.
            _panes[i].SetTreeVisible(
                i < count
                && settings.Panes[i].ShowTree
                && AppSettings.CanShowTree(settings.Layout, i),
                AppSettings.TreeOnRight(settings.Layout, i));
        }

        switch (settings.Layout)
        {
            case LayoutMode.TopOneBottomTwo:
                Place(_panes[0], row: 0, column: 0, columnSpan: 3);
                Place(_panes[1], row: 2, column: 0);
                Place(_panes[2], row: 2, column: 2);
                AddRowSplitter(column: 0, columnSpan: 3);
                AddColumnSplitter(row: 2);
                break;

            case LayoutMode.TopTwoBottomOne:
                Place(_panes[0], row: 0, column: 0);
                Place(_panes[1], row: 0, column: 2);
                Place(_panes[2], row: 2, column: 0, columnSpan: 3);
                AddRowSplitter(column: 0, columnSpan: 3);
                AddColumnSplitter(row: 0);
                break;

            case LayoutMode.LeftOneRightTwo:
                Place(_panes[0], row: 0, column: 0, rowSpan: 3);
                Place(_panes[1], row: 0, column: 2);
                Place(_panes[2], row: 2, column: 2);
                AddColumnSplitter(row: 0, rowSpan: 3);
                AddRowSplitter(column: 2);
                break;

            case LayoutMode.LeftTwoRightOne:
                Place(_panes[0], row: 0, column: 0);
                Place(_panes[1], row: 2, column: 0);
                Place(_panes[2], row: 0, column: 2, rowSpan: 3);
                AddColumnSplitter(row: 0, rowSpan: 3);
                AddRowSplitter(column: 0);
                break;

            case LayoutMode.TwoRows:
                Place(_panes[0], row: 0, column: 0, columnSpan: 3);
                Place(_panes[1], row: 2, column: 0, columnSpan: 3);
                AddRowSplitter(column: 0, columnSpan: 3);
                break;

            case LayoutMode.TwoColumns:
                Place(_panes[0], row: 0, column: 0, rowSpan: 3);
                Place(_panes[1], row: 0, column: 2, rowSpan: 3);
                AddColumnSplitter(row: 0, rowSpan: 3);
                break;

            default:
                Place(_panes[0], row: 0, column: 0);
                Place(_panes[1], row: 0, column: 2);
                Place(_panes[2], row: 2, column: 0);
                Place(_panes[3], row: 2, column: 2);
                AddRowSplitter(column: 0, columnSpan: 3);
                AddColumnSplitter(row: 0, rowSpan: 3);
                break;
        }
    }

    private const double SplitterThickness = 6;

    private void Place(PaneHost pane, int row, int column, int rowSpan = 1, int columnSpan = 1)
    {
        Grid.SetRow(pane, row);
        Grid.SetColumn(pane, column);
        Grid.SetRowSpan(pane, rowSpan);
        Grid.SetColumnSpan(pane, columnSpan);
        PaneArea.Children.Add(pane);
    }

    private void AddRowSplitter(int column, int columnSpan = 1)
    {
        var bar = new GridSplitter
        {
            Background = System.Windows.Media.Brushes.Transparent,
            ShowsPreview = false,
            ResizeDirection = GridResizeDirection.Rows,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Cursor = Cursors.SizeNS
        };

        bar.DragCompleted += (_, _) => StoreSplit(rows: true);

        Grid.SetRow(bar, 1);
        Grid.SetColumn(bar, column);
        Grid.SetColumnSpan(bar, columnSpan);
        PaneArea.Children.Add(bar);
    }

    private void AddColumnSplitter(int row, int rowSpan = 1)
    {
        var bar = new GridSplitter
        {
            Background = System.Windows.Media.Brushes.Transparent,
            ShowsPreview = false,
            ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Cursor = Cursors.SizeWE
        };

        bar.DragCompleted += (_, _) => StoreSplit(rows: false);

        Grid.SetColumn(bar, 1);
        Grid.SetRow(bar, row);
        Grid.SetRowSpan(bar, rowSpan);
        PaneArea.Children.Add(bar);
    }

    /// <summary>가룻대를 놓은 뒤 바뀐 비율을 적어 둡니다.</summary>
    private void StoreSplit(bool rows)
    {
        double first;
        double second;

        if (rows)
        {
            first = PaneArea.RowDefinitions[0].Height.Value;
            second = PaneArea.RowDefinitions[2].Height.Value;
        }
        else
        {
            first = PaneArea.ColumnDefinitions[0].Width.Value;
            second = PaneArea.ColumnDefinitions[2].Width.Value;
        }

        var total = first + second;

        if (total <= 0)
        {
            return;
        }

        var share = Math.Clamp(first / total, 0.15, 0.85);

        if (rows)
        {
            _host.Settings.RowSplit = share;
        }
        else
        {
            _host.Settings.ColumnSplit = share;
        }

        _host.SettingsService.Save(_host.Settings);
    }

    /// <summary>나눔 비율을 반반으로 되돌립니다.</summary>
    private void ResetSplits()
    {
        _host.Settings.RowSplit = AppSettings.DefaultSplit;
        _host.Settings.ColumnSplit = AppSettings.DefaultSplit;
        _host.Settings.TreeSplit = AppSettings.DefaultTreeSplit;

        BuildLayout();

        foreach (var pane in _panes)
        {
            pane.NotifyTreeSplitChanged();
        }

        _host.SettingsService.Save(_host.Settings);
    }

    /// <summary>
    /// 창 설정 창에서 고친 것을 화면에 옮깁니다.
    /// 역할이 바뀐 칸은 속을 통째로 새로 만들어야 합니다. 탐색기와 미리보기는 담는 것이 다릅니다.
    /// </summary>
    private void ApplyPaneChanges(PaneRole[] before)
    {
        PaneArea.Children.Clear();

        for (var i = 0; i < _panes.Length; i++)
        {
            if (_panes[i].Settings is not { } settings || settings.Role == before[i])
            {
                continue;
            }

            _panes[i].Activated -= OnPaneActivated;
            _panes[i].RoleChangeRequested -= OnPaneRoleChangeRequested;

            if (_panes[i].Pane is { } old)
            {
                old.SelectionChanged -= OnPaneSelectionChanged;
            }

            _panes[i].PersistState();
            _panes[i].Dispose();
            _panes[i] = CreatePane(settings);
        }

        BuildLayout();

        foreach (var pane in _panes)
        {
            pane.NotifySearchReady();
            pane.NotifyPreviewChanged();
        }

        if (_activePane is null || _activePane.IsPreviewPane || _activePane.Visibility != Visibility.Visible)
        {
            SetActivePane(FirstExplorerPane());
        }

        NotifyPreviewPanes(_activePane?.Pane?.SelectedTab?.SelectedItem);
        _host.SettingsService.Save(_host.Settings);
    }

    /// <summary>
    /// 칸의 오른쪽 클릭 메뉴에서 미리보기와 탐색기를 맞바꿀 때입니다.
    /// 창 설정에서 역할을 고친 것과 똑같이 칸을 새로 만듭니다.
    /// 메뉴를 부른 칸이 스스로 없어지는 일이라, 메뉴가 닫힌 뒤로 미룹니다.
    /// </summary>
    private void OnPaneRoleChangeRequested(PaneHost host, PaneRole role)
    {
        if (host.Settings is not { } settings || settings.Role == role)
        {
            return;
        }

        if (role == PaneRole.Preview && !HasOtherExplorer(host))
        {
            MessageBox.Show(
                this,
                "보이는 탐색기 칸이 이 칸 하나뿐이라 미리보기로 바꿀 수 없습니다.",
                "2x2 Explorer",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var before = new PaneRole[_panes.Length];

        for (var i = 0; i < _panes.Length; i++)
        {
            before[i] = _host.Settings.Panes[i].Role;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            settings.Role = role;
            ApplyPaneChanges(before);
        }));
    }

    /// <summary>보이는 칸 가운데 이 칸 말고도 탐색기 칸이 있는지입니다.</summary>
    private bool HasOtherExplorer(PaneHost host)
    {
        var visible = AppSettings.PaneCountFor(_host.Settings.Layout);

        for (var i = 0; i < visible && i < _panes.Length; i++)
        {
            if (!ReferenceEquals(_panes[i], host) && _host.Settings.Panes[i].Role == PaneRole.Files)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>탐색기 칸에서 고른 파일을 미리보기 전용 칸들에 알려 줍니다.</summary>
    private void OnPaneSelectionChanged(object? sender, FileItemViewModel? item)
    {
        if (!ReferenceEquals(sender, _activePane?.Pane))
        {
            return;
        }

        NotifyPreviewPanes(item);
    }

    private void NotifyPreviewPanes(FileItemViewModel? item)
    {
        foreach (var pane in _panes)
        {
            if (pane.IsPreviewPane)
            {
                pane.ShowSelection(item);
            }
        }
    }

    private void OnPaneActivated(object? sender, PaneSettings settings)
    {
        if (sender is PaneHost pane)
        {
            SetActivePane(pane);
        }
    }

    /// <summary>
    /// 마지막으로 누른 탐색기 칸을 기억합니다.
    /// 미리보기 전용 칸을 눌렀을 때는 바꾸지 않습니다. 그 칸은 보여 주기만 하는 자리입니다.
    /// </summary>
    private void SetActivePane(PaneHost? pane)
    {
        if (pane is null || pane.IsPreviewPane || ReferenceEquals(_activePane, pane))
        {
            return;
        }

        foreach (var candidate in _panes)
        {
            candidate.IsActive = ReferenceEquals(candidate, pane);
        }

        _activePane = pane;

        // 활성 칸이 바뀌면 미리보기도 그 칸이 고른 것으로 따라갑니다.
        NotifyPreviewPanes(pane.Pane?.SelectedTab?.SelectedItem);
    }

    // ── 즐겨찾기 ──

    /// <summary>
    /// 즐겨찾기 메뉴를 만듭니다.
    /// 항목마다 명령을 바인딩으로 걸려면 Setter.Value에 Binding을 넣어야 하는데
    /// WPF 스타일은 그걸 허용하지 않습니다. 그래서 코드에서 직접 채웁니다.
    /// </summary>
    private MenuItem BuildFavoritesMenu()
    {
        var favoritesMenu = new MenuItem { Header = "즐겨찾기" };
        var favorites = _host.Settings.Favorites;

        var add = new MenuItem { Header = "현재 폴더 추가" };
        add.Click += (_, _) => AddCurrentFolderToFavorites();
        add.IsEnabled = _host.Settings.Favorites.Count < AppSettings.MaxFavorites;
        favoritesMenu.Items.Add(add);

        var edit = new MenuItem { Header = "즐겨찾기 편집..." };
        edit.Click += (_, _) => EditFavorites();
        favoritesMenu.Items.Add(edit);

        favoritesMenu.Items.Add(new Separator());

        if (favorites.Count == 0)
        {
            favoritesMenu.Items.Add(new MenuItem { Header = "등록된 즐겨찾기가 없습니다", IsEnabled = false });
            return favoritesMenu;
        }

        foreach (var path in favorites)
        {
            var open = new MenuItem
            {
                Header = DescribeFavorite(path),
                ToolTip = SpecialPaths.IsVirtual(path) ? SpecialPaths.ToDisplay(path) : PathHelper.Expand(path)
            };
            var target = path;
            open.Click += (_, _) => OpenFavorite(target);
            favoritesMenu.Items.Add(open);
        }

        favoritesMenu.Items.Add(new Separator());

        favoritesMenu.Items.Add(new MenuItem
        {
            Header = $"{favorites.Count} / {AppSettings.MaxFavorites}개 사용 중",
            IsEnabled = false
        });

        return favoritesMenu;
    }

    /// <summary>
    /// 메뉴를 펼칠 때마다 다시 만듭니다.
    /// 탭 오른쪽 클릭 메뉴에서도 즐겨찾기를 고칠 수 있어, 그때그때 읽는 편이 확실합니다.
    /// </summary>
    /// <summary>
    /// 설정 메뉴를 펼칠 때 항목을 다시 만듭니다. 즐겨찾기가 밖에서 바뀔 수 있기 때문입니다.
    ///
    /// 하위 메뉴가 열릴 때도 이 신호가 위로 올라옵니다.
    /// 그때까지 다시 만들면 방금 열린 하위 메뉴가 통째로 버려져 곧바로 닫힙니다.
    /// 그래서 설정 메뉴 자신이 열린 경우에만 손댑니다.
    /// </summary>
    private void OnSettingsMenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, SettingsMenu))
        {
            return;
        }

        BuildSettingsMenu();
    }

    private IntPtr OnWindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WindowMaximize.WindowMessageGetMinMaxInfo)
        {
            return IntPtr.Zero;
        }

        WindowMaximize.ClampToWorkArea(window, lParam);
        handled = true;
        return IntPtr.Zero;
    }

    // ── 위쪽 띠 ──

    /// <summary>
    /// 띠가 접혀 있을 때, 마우스를 창 맨 위 이만큼 안에 대면 띠가 내려옵니다.
    /// 맨 위 6픽셀은 크기를 조절하는 테두리라 마우스 움직임이 우리에게 오지 않습니다.
    /// 그보다 넉넉히 잡아야 띠를 부를 수 있습니다.
    /// </summary>
    private const double EdgeReach = 14;

    /// <summary>
    /// 띠를 늘 보이게 둘지, 마우스를 올렸을 때만 내려오게 할지 바꿉니다.
    /// </summary>
    private void OnTogglePin(object sender, RoutedEventArgs e)
    {
        var view = _host.Settings.View;
        view.ShowMenuBar = !view.ShowMenuBar;

        ApplyBarMode();
        _host.SettingsService.Save(_host.Settings);
    }

    private void ApplyBarMode()
    {
        var pinned = _host.Settings.View.ShowMenuBar;

        TopBar.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;

        // 늘 보이게 둘 때만 네 칸을 띠 높이만큼 아래로 내립니다.
        // 접어 두었을 때는 띠가 칸 위에 겹쳐 내려오므로 화면이 밀리지 않습니다.
        PaneArea.Margin = new Thickness(6, pinned ? TopBar.Height + 6 : 6, 6, 6);

        PinButton.Content = pinned ? "\uE77A" : "\uE718";
        PinButton.ToolTip = pinned ? "띠를 접고 필요할 때만 부르기" : "띠를 늘 보이게 두기";
    }

    /// <summary>
    /// 접어 둔 띠를 마우스 자리에 따라 내리고 올립니다.
    /// 창 맨 위 몇 픽셀 안에 들어오면 내려오고, 띠 아래로 충분히 내려가면 다시 접힙니다.
    /// 메뉴를 펼쳐 둔 동안에는 접지 않습니다.
    /// </summary>
    private void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        if (_host.Settings.View.ShowMenuBar)
        {
            return;
        }

        var y = e.GetPosition(this).Y;

        if (y <= EdgeReach)
        {
            TopBar.Visibility = Visibility.Visible;
            return;
        }

        if (TopBar.Visibility == Visibility.Visible
            && y > TopBar.ActualHeight + 12
            && !SettingsMenu.IsSubmenuOpen
            && !PhotoMenu.IsSubmenuOpen
            && !GlobalMenu.IsKeyboardFocusWithin
            && !PhotoMenuBar.IsKeyboardFocusWithin)
        {
            TopBar.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 띠의 빈 자리를 끌면 창이 따라옵니다. 두 번 누르면 최대화합니다.
    /// 끌기는 윈도우에 맡깁니다. WPF의 DragMove는 최대화된 창을 끌지 못합니다.
    /// </summary>
    private void OnTopBarPressed(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        WindowDrag.Begin(new WindowInteropHelper(this).Handle);
    }

    private void OnMinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeWindow(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;

        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "이전 크기로" : "최대화";
    }

    /// <summary>창 설정 창을 띄우고, 고친 것을 화면에 옮깁니다.</summary>
    private void EditLayout()
    {
        var before = new PaneRole[_panes.Length];

        for (var i = 0; i < _panes.Length; i++)
        {
            before[i] = _host.Settings.Panes[i].Role;
        }

        var dialog = new LayoutWindow(_host.Settings) { Owner = this };
        var confirmed = dialog.ShowDialog() == true;

        if (!confirmed && !dialog.SplitsReset)
        {
            return;
        }

        if (confirmed)
        {
            ApplyPaneChanges(before);
            ApplyGeneralSettings();
        }
        else
        {
            // 취소했지만 비율은 되돌린 경우입니다.
            BuildLayout();
            _host.SettingsService.Save(_host.Settings);
        }

        foreach (var pane in _panes)
        {
            pane.Pane?.NotifyStatusBarChanged();
            pane.NotifyTreeSplitChanged();
        }
    }

    /// <summary>즐겨찾기 열네 칸과 제목 표시줄 체크를 직접 고치는 창을 띄웁니다.</summary>
    private void EditFavorites()
    {
        var dialog = new FavoritesWindow(_host.Settings) { Owner = this };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _host.Settings.SetFavorites(dialog.Result, dialog.TitleBar);
        _host.SettingsService.Save(_host.Settings);
        _host.NotifyFavoritesChanged();
    }

    /// <summary>즐겨찾기가 바뀌면 네 칸의 탐색 창 맨 위 줄과 제목 표시줄 단추를 다시 세웁니다.</summary>
    private void OnFavoritesChanged(object? sender, EventArgs e)
    {
        foreach (var pane in _panes)
        {
            pane.RefreshFavorites();
        }

        BuildFavoriteBar();
    }

    /// <summary>창 전체에 걸리는 설정입니다.</summary>
    private void BuildSettingsMenu()
    {
        SettingsMenu.Items.Clear();
        SettingsMenu.Items.Add(BuildFavoritesMenu());
        SettingsMenu.Items.Add(new Separator());

        var settings = new MenuItem
        {
            Header = "설정…",
            ToolTip = "창 나누기와 테마·아이콘 크기·검색 같은 값들을 한자리에서 고칩니다"
        };

        settings.Click += (_, _) => EditLayout();
        SettingsMenu.Items.Add(settings);

        var resetSplits = new MenuItem
        {
            Header = "창 너비 초기화",
            ToolTip = "가룻대를 끌어 바꾼 너비를 기본값으로 되돌립니다"
        };

        resetSplits.Click += (_, _) => ResetSplits();
        SettingsMenu.Items.Add(resetSplits);

        SettingsMenu.Items.Add(new Separator());

        var openBin = new MenuItem
        {
            Header = "휴지통 열기",
            ToolTip = "지금 칸에 새 탭으로 엽니다"
        };

        openBin.Click += (_, _) => OpenRecycleBin();
        SettingsMenu.Items.Add(openBin);

        var emptyBin = new MenuItem
        {
            Header = "휴지통 비우기…",
            ToolTip = "정말 비울지는 윈도우가 묻습니다"
        };

        emptyBin.Click += (_, _) => EmptyRecycleBin();
        SettingsMenu.Items.Add(emptyBin);
    }

    /// <summary>
    /// 휴지통을 활성 칸의 새 탭으로 엽니다.
    /// 셸에게 맡기지 않고 $I·$R 짝 파일을 우리가 읽어 목록으로 세웁니다.
    /// </summary>
    private void OpenRecycleBin()
    {
        var pane = (_activePane ?? FirstExplorerPane())?.Pane;

        if (pane is null)
        {
            return;
        }

        pane.AddTab(SpecialPaths.RecycleBin);
    }

    private void EmptyRecycleBin()
    {
        var owner = new WindowInteropHelper(this).Handle;

        // 정말 비울지 묻는 창과 진행률 창은 윈도우가 띄웁니다. 우리가 한 번 더 물을 까닭이 없습니다.
        if (ShellRecycleBin.Empty(owner))
        {
            return;
        }

        MessageBox.Show(
            this,
            "휴지통을 비우지 못했습니다. 이미 비어 있었을 수 있습니다.",
            "2x2 Explorer",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    /// <summary>설정 창에서 고친 값 가운데 지금 화면에 곧바로 걸어야 하는 것들입니다.</summary>
    private void ApplyGeneralSettings()
    {
        ApplyBatchRenameButton();
        (Application.Current as App)?.ApplyTheme(_host.Settings.Theme);
        DiagnosticLog.Enabled = _host.Settings.View.WriteDiagnosticLog;

        foreach (var pane in _panes)
        {
            pane.Pane?.NotifyThumbnailSizeChanged();
        }
    }

    /// <summary>즐겨찾기 메뉴에 보일 이름입니다. 폴더 이름만 쓰고, 드라이브 뿌리는 경로 그대로 씁니다.</summary>
    private static string DescribeFavorite(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "(빈 칸)";
        }

        // 내 PC · 바탕 화면 · 다운로드 · 문서 표시는 그 이름으로 보입니다.
        if (SpecialPaths.IsVirtual(path) || SpecialPaths.IsKnownFolder(path))
        {
            return SpecialPaths.ToDisplay(path);
        }

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);

        return name.Length > 0 ? name : path;
    }

    private void AddCurrentFolderToFavorites()
    {
        var path = _activePane?.Pane?.SelectedTab?.CurrentPath;

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var favorites = new List<string>(_host.Settings.Favorites);

        if (favorites.Count >= AppSettings.MaxFavorites)
        {
            return;
        }

        favorites.Add(path);

        // 같은 폴더가 이미 있으면 여기서 걸러집니다. 제목 표시줄 체크는 그대로 둡니다.
        _host.Settings.SetFavorites(favorites);
        _host.SettingsService.Save(_host.Settings);
        _host.NotifyFavoritesChanged();
    }

    private void OpenFavorite(string path)
    {
        // 활성 패널에 새 탭으로 엽니다.
        (_activePane ?? _panes[0]).Pane?.AddTab(path);
    }

    // ── 제목 표시줄의 즐겨찾기 단추 ──

    /// <summary>제목 표시줄에 떠 있는 즐겨찾기 단추들입니다. 즐겨찾기 차례를 함께 들고 있어 색을 맞춥니다.</summary>
    private readonly List<(Button Button, string Path)> _favoriteButtons = new();

    /// <summary>자리가 모자라 숨긴 즐겨찾기입니다. » 단추의 메뉴에 나옵니다.</summary>
    private readonly List<(string Path, int Index)> _hiddenFavorites = new();

    /// <summary>
    /// 즐겨찾기 편집 창에서 "상단"을 체크한 것만 단추로 세웁니다. 차례는 즐겨찾기 차례를 따릅니다.
    /// 단추마다 윈도우 탐색기와 같은 폴더 아이콘을 붙입니다.
    /// </summary>
    private void BuildFavoriteBar()
    {
        FavoriteBar.Children.Clear();
        _favoriteButtons.Clear();

        var favorites = _host.Settings.Favorites;

        for (var index = 0; index < favorites.Count; index++)
        {
            var path = favorites[index];

            if (!_host.Settings.IsOnTitleBar(path))
            {
                continue;
            }

            var button = CreateFavoriteButton(path, index);
            FavoriteBar.Children.Add(button);
            _favoriteButtons.Add((button, path));
        }

        // 크기를 재려면 화면에 한 번 놓여야 합니다. 배치가 끝난 뒤에 자리를 나눕니다.
        Dispatcher.BeginInvoke(new Action(LayoutFavoriteBar), DispatcherPriority.Loaded);
    }

    private Button CreateFavoriteButton(string path, int index)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8, 0, 8, 0)
        };

        content.Children.Add(CreateFavoriteIcon(path, index, new Thickness(0, 0, 6, 0)));

        content.Children.Add(new TextBlock
        {
            Text = DescribeFavorite(path),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12
        });

        var button = new Button
        {
            Style = (Style)FindResource("ToolButton"),
            Width = double.NaN,
            Height = 26,
            Content = content,
            ToolTip = $"{DescribeFavorite(path)} — 누르면 마지막으로 누른 칸에 새 탭으로 엽니다."
        };

        // 누르면 늘 새 탭으로 엽니다. 보고 있던 탭은 그대로 남습니다.
        button.Click += (_, _) => OpenFavoriteInNewTab(path);

        return button;
    }

    /// <summary>
    /// 즐겨찾기 단추의 아이콘입니다. 윈도우 탐색기가 그 폴더에 쓰는 아이콘을 셸에게 받아 씁니다.
    /// 바탕 화면·다운로드 같은 폴더는 제 모양 아이콘이, 사용자가 폴더 아이콘을 바꿔 두었으면 그 아이콘이 나옵니다.
    /// 받아 오는 동안이나 받지 못했을 때는 탭과 같은 색 점을 대신 보입니다.
    /// </summary>
    private FrameworkElement CreateFavoriteIcon(string path, int index, Thickness margin)
    {
        var holder = new Grid
        {
            Width = 16,
            Height = 16,
            Margin = margin,
            VerticalAlignment = VerticalAlignment.Center
        };

        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = FavoriteBrush(index),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var icon = new Image
        {
            Width = 16,
            Height = 16,
            SnapsToDevicePixels = true
        };

        System.Windows.Media.RenderOptions.SetBitmapScalingMode(icon, System.Windows.Media.BitmapScalingMode.HighQuality);

        holder.Children.Add(dot);
        holder.Children.Add(icon);

        LoadFavoriteIcon(icon, dot, path);
        return holder;
    }

    /// <summary>
    /// 셸에게 아이콘을 받아 옵니다. 썸네일과 같은 일꾼들이 화면 갈래 밖에서 받아 옵니다.
    /// 화면 배율이 높아도 흐리지 않도록 두 배 크기로 받아 16px 에 맞춰 줄입니다.
    /// </summary>
    private async void LoadFavoriteIcon(Image icon, UIElement placeholder, string favorite)
    {
        try
        {
            var image = await _host.Thumbnails
                .GetAsync(ShellPathOf(favorite), 32, iconOnly: true, System.Threading.CancellationToken.None)
                .ConfigureAwait(true);

            if (image is null)
            {
                return;
            }

            icon.Source = image;
            placeholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"즐겨찾기 아이콘을 받지 못했습니다 {favorite}: {ex.Message}");
        }
    }

    /// <summary>
    /// 셸이 알아듣는 경로입니다. 내 PC 는 디스크 위의 폴더가 아니라 셸의 고유 이름으로 부릅니다.
    /// 바탕 화면·다운로드·문서 표시는 이 컴퓨터의 실제 경로로 바꿉니다.
    /// </summary>
    private static string ShellPathOf(string favorite) =>
        SpecialPaths.IsMyComputer(favorite) ? "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"
        : SpecialPaths.IsRecycleBin(favorite) ? "::{645FF040-5081-101B-9F08-00AA002F954E}"
        : PathHelper.Expand(favorite);

    /// <summary>그 차례의 탭 색입니다. 색이 없으면 흐린 글자색을 씁니다.</summary>
    private System.Windows.Media.Brush FavoriteBrush(int index)
    {
        if (TabPalette.At(index) is { } hex)
        {
            try
            {
                var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
                var brush = new System.Windows.Media.SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
            catch (FormatException)
            {
            }
        }

        return (System.Windows.Media.Brush)FindResource("Text.Muted");
    }

    /// <summary>
    /// 들어갈 만큼만 단추를 보이고 나머지는 » 메뉴로 보냅니다.
    /// 제목 표시줄의 빈 자리는 창을 끄는 손잡이라, 한 뼘은 늘 남겨 둡니다.
    /// </summary>
    private void LayoutFavoriteBar()
    {
        _hiddenFavorites.Clear();

        if (TopBar.ActualWidth <= 0 || TopBar.Child is not DockPanel dock)
        {
            return;
        }

        const double DragRoom = 120;
        const double OverflowRoom = 30;

        var used = 0.0;

        foreach (UIElement child in dock.Children)
        {
            if (ReferenceEquals(child, FavoriteBar) || ReferenceEquals(child, FavoriteOverflow) || child is Border)
            {
                continue;
            }

            used += child.DesiredSize.Width;
        }

        var available = TopBar.ActualWidth - used - DragRoom - OverflowRoom - FavoriteBar.Margin.Left;
        var running = 0.0;
        var full = false;

        for (var i = 0; i < _favoriteButtons.Count; i++)
        {
            var (button, path) = _favoriteButtons[i];

            button.Visibility = Visibility.Visible;
            button.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            var width = button.DesiredSize.Width;

            if (!full && running + width <= available)
            {
                running += width;
                continue;
            }

            full = true;
            button.Visibility = Visibility.Collapsed;
            _hiddenFavorites.Add((path, _host.Settings.Favorites.IndexOf(path)));
        }

        FavoriteOverflow.Visibility = _hiddenFavorites.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTopBarSizeChanged(object sender, SizeChangedEventArgs e) => LayoutFavoriteBar();

    /// <summary>숨은 즐겨찾기를 메뉴로 펼칩니다.</summary>
    private void OnFavoriteOverflow(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = FavoriteOverflow,
            Placement = PlacementMode.Bottom
        };

        foreach (var (path, index) in _hiddenFavorites)
        {
            var item = new MenuItem
            {
                Header = DescribeFavorite(path),
                Icon = CreateFavoriteIcon(path, index, new Thickness(0)),
                ToolTip = "누르면 새 탭으로 엽니다."
            };

            var target = path;
            item.Click += (_, _) => OpenFavoriteInNewTab(target);

            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    /// <summary>즐겨찾기를 열 칸입니다. 마지막으로 누른 탐색기 칸이고, 없으면 첫 탐색기 칸입니다.</summary>
    private PaneViewModel? FavoriteTargetPane() =>
        (_activePane is { IsPreviewPane: false } active ? active : FirstExplorerPane())?.Pane;

    /// <summary>그 즐겨찾기를 새 탭으로 엽니다.</summary>
    private void OpenFavoriteInNewTab(string path) => FavoriteTargetPane()?.AddTab(path);

    /// <summary>제목 띠의 일괄 인쇄 단추입니다. 마지막으로 누른 탐색기 칸의 지금 탭을 기준으로 엽니다.</summary>
    private void OnBatchPrint(object sender, RoutedEventArgs e)
    {
        var tab = FavoriteTargetPane()?.SelectedTab;

        PrintWindow.ShowFor(
            this,
            tab?.SelectedPaths ?? Array.Empty<string>(),
            tab?.CurrentPath);
    }

    /// <summary>제목 띠의 일괄 이름 바꾸기(베타) 단추입니다. 마지막으로 누른 탐색기 칸의 지금 탭을 기준으로 엽니다.</summary>
    private void OnBatchRename(object sender, RoutedEventArgs e) =>
        RenameWindow.ShowFor(this, FavoriteTargetPane()?.SelectedTab);

    /// <summary>일괄 이름 바꾸기(베타) 단추를 설정대로 보이거나 숨깁니다.</summary>
    private void ApplyBatchRenameButton() =>
        BatchRenameButton.Visibility = _host.Settings.View.ShowBatchRename ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>제목 띠의 PDF 편집 단추입니다. 고른 PDF·그림을 쪽으로 넣어 엽니다.</summary>
    private void OnEditPdf(object sender, RoutedEventArgs e)
    {
        var tab = FavoriteTargetPane()?.SelectedTab;

        PdfEditorWindow.ShowFor(
            this,
            tab?.SelectedPaths ?? Array.Empty<string>(),
            tab?.CurrentPath);
    }

    /// <summary>그림 편집 메뉴의 [단일 편집]입니다. 고른 그림이 없으면 클립보드의 그림을 엽니다.</summary>
    private void OnEditSingleImage(object sender, RoutedEventArgs e)
    {
        var tab = FavoriteTargetPane()?.SelectedTab;

        ImageEditorWindow.ShowFor(
            this,
            tab?.SelectedPaths ?? Array.Empty<string>(),
            tab?.CurrentPath);
    }

    /// <summary>그림 편집 메뉴의 [일괄 편집]입니다. [결과 폴더 열기]는 같은 칸에 새 탭으로 엽니다.</summary>
    private void OnEditImageBatch(object sender, RoutedEventArgs e)
    {
        var pane = FavoriteTargetPane();

        BatchImageWindow.ShowFor(
            this,
            pane?.SelectedTab?.SelectedPaths ?? Array.Empty<string>(),
            folder => (FavoriteTargetPane() ?? pane)?.AddTab(folder));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        foreach (var pane in _panes)
        {
            pane.Activated -= OnPaneActivated;
            pane.PersistState();
            pane.Dispose();
        }

        WindowPlacementService.Capture(this, _host.Settings.Window);
        _host.SettingsService.Save(_host.Settings);
        base.OnClosing(e);
    }
}
