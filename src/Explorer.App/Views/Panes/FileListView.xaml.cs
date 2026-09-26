using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Explorer.App.Controls;
using Explorer.App.Services;
using Explorer.App.ViewModels;
using Explorer.App.Views.Dialogs;
using Explorer.Core.Models;
using Explorer.Core.Services;
using Explorer.Core.Settings;
using Explorer.Shell.Operations;

namespace Explorer.App.Views.Panes;

/// <summary>
/// 파일 목록 화면입니다. 뷰모델이 상태를 들고 있고, 이 파일은 입력을 옮겨 주기만 합니다.
/// </summary>
public partial class FileListView : UserControl
{
    /// <summary>
    /// 자세히 보기의 GridView 입니다. XAML 에서 ListView 안에 직접 선언한 것을 들고 있다가,
    /// 보기 방식을 오갈 때 다시 꽂습니다. 인스턴스마다 자기 것을 쓰므로 패널끼리 겹치지 않습니다.
    /// </summary>
    private readonly ViewBase? _detailsView;

    /// <summary>왼쪽 단추를 누른 자리입니다. 여기서 얼마나 움직였는지로 끌기 시작을 판단합니다.</summary>
    private Point _pressPoint;

    /// <summary>왼쪽 단추를 누른 줄입니다. 빈 자리를 눌렀으면 null 입니다.</summary>
    private FileItemViewModel? _pressedItem;

    /// <summary>
    /// 여러 줄을 고른 상태에서 그중 하나를 눌렀을 때, 고른 것을 지우지 않으려고 잠시 미뤄 둔 줄입니다.
    /// 끌지 않고 그냥 놓으면 그때 이 줄 하나만 고른 것으로 정리합니다.
    /// </summary>
    private FileItemViewModel? _pendingSelection;

    /// <summary>끌기를 이미 시작했는지 여부입니다. DoDragDrop 이 끝날 때까지 켜져 있습니다.</summary>
    private bool _dragging;

    /// <summary>오른쪽 단추를 누른 자리입니다. 오른쪽 단추 끌기를 가릴 때 씁니다.</summary>
    private Point _rightPressPoint;

    /// <summary>오른쪽 단추를 누른 줄입니다. 빈 자리를 눌렀으면 null 입니다.</summary>
    private FileItemViewModel? _rightPressedItem;

    /// <summary>오른쪽 단추를 누른 채 아직 놓지 않은 상태입니다.</summary>
    private bool _rightHolding;

    /// <summary>
    /// 오른쪽 단추로 끌었음을 적어 둡니다.
    /// 끌기가 끝나면 단추를 뗀 자리에서 우클릭 메뉴가 또 뜨므로, 그 한 번을 건너뛰려고 씁니다.
    /// </summary>
    private bool _rightDragged;

    /// <summary>지금 들어온 끌기가 오른쪽 단추로 시작한 것인지입니다. DragOver 에서 보고 적어 둡니다.</summary>
    private bool _dropUsedRightButton;

    /// <summary>지금 놓을 자리로 표시해 둔 줄입니다.</summary>
    private ListViewItem? _dropTarget;

    /// <summary>범위 지정 선택을 시작한 자리입니다. BandLayer 기준 좌표입니다.</summary>
    private Point _bandOrigin;

    /// <summary>마우스가 지금 있는 자리입니다. 가장자리 자동 스크롤이 이 값을 씁니다.</summary>
    private Point _bandCurrent;

    /// <summary>빈 자리를 눌러 마우스를 붙잡고 있는 상태입니다. 아직 상자가 안 보일 수도 있습니다.</summary>
    private bool _bandHolding;

    /// <summary>상자가 실제로 보이는 상태입니다. 문턱을 넘어야 켜집니다.</summary>
    private bool _bandDrawing;

    /// <summary>Ctrl을 누르고 시작했을 때, 원래 골라 두었던 것들입니다.</summary>
    private HashSet<FileItemViewModel>? _bandKeep;

    /// <summary>목록 밖으로 끌고 나갔을 때 조금씩 굴려 주는 시계입니다.</summary>
    private DispatcherTimer? _bandScroller;

    /// <summary>목록의 스크롤 뷰어입니다. 한 번 찾아 두고 계속 씁니다.</summary>
    private ScrollViewer? _scroller;

    /// <summary>줄이 실제로 그려지는 부분입니다. 열 머리글 쪽과 헷갈리지 않게 따로 찾아 둡니다.</summary>
    private ScrollContentPresenter? _presenter;

    /// <summary>그림 챙기기를 60밀리초에 한 번으로 묶어 주는 시계입니다.</summary>
    private DispatcherTimer? _thumbnailSweep;

    /// <summary>주소를 조각 단추 대신 글자로 고치는 중인지 여부입니다.</summary>
    private bool _editingAddress;

    /// <summary>목록에서 이어 친 글자입니다. 잠시 쉬면 지워집니다.</summary>
    private string _typed = string.Empty;

    private DateTime _typedAt;

    /// <summary>입력기 메시지를 듣기 위해 붙인 창입니다.</summary>
    private HwndSource? _messageSource;

    private HwndSourceHook? _messageHook;

    /// <summary>바로 앞의 글쇠가 글자를 만들어 냈는지 여부입니다.</summary>
    private bool _keyMadeLetter;

    /// <summary>자세히 보기에 놓을 수 있는 열입니다. 설정의 Key 와 여기 Key 로 맞춥니다.</summary>
    private sealed record ColumnSpec(string Key, string Header, string SortTag, string Template);

    private static readonly ColumnSpec[] ColumnCatalog =
    {
        new("Name", "이름", "Name", "Cell.Name"),
        new("Size", "크기", "Size", "Cell.Size"),
        new("Modified", "수정한 날짜", "LastWriteTime", "Cell.Modified"),
        new("Type", "유형", "Type", "Cell.Type"),
        new("Created", "만든 날짜", "CreationTime", "Cell.Created"),
        new("Extension", "확장자", "Type", "Cell.Extension"),
        new("Folder", "경로", "Name", "Cell.Folder")
    };

    /// <summary>
    /// 열 구성이 바뀌었다는 소식입니다. 네 패널이 같은 구성을 쓰므로 모두 함께 다시 그립니다.
    /// </summary>
    private static event EventHandler? ColumnsChanged;

    /// <summary>지금 그려 둔 열이 설정의 어느 항목인지 짝지어 둡니다. 너비를 적어 둘 때 씁니다.</summary>
    private readonly Dictionary<GridViewColumn, string> _columnKeys = new();

    /// <summary>열 구성 소식을 듣고 있는지 여부입니다.</summary>
    private bool _columnsHooked;

    public FileListView()
    {
        InitializeComponent();
        _detailsView = List.View;
        DataContextChanged += OnDataContextChanged;

        // 줄은 스크롤할 때마다 새로 만들어집니다. 그때마다 화면에 든 줄을 다시 챙깁니다.
        List.ItemContainerGenerator.StatusChanged += OnContainersChanged;

        // 목록에 초점이 있을 때는 WPF가 한글 입력기 신호를 올려 주지 않습니다.
        // 그래서 창으로 오는 글쇠 메시지를 직접 듣습니다.
        Loaded += OnViewLoaded;
        Unloaded += OnViewUnloaded;
    }

    private FileListViewModel? ViewModel => DataContext as FileListViewModel;

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        ApplyPaneRole();

        if (!_columnsHooked)
        {
            _columnsHooked = true;
            ColumnsChanged += OnColumnsChanged;
        }

        ApplyColumns();

        if (_messageSource is not null)
        {
            return;
        }

        if (PresentationSource.FromVisual(this) is not HwndSource source)
        {
            DiagnosticLog.Write("입력 진단: 창을 찾지 못해 메시지를 듣지 못합니다.");
            return;
        }

        _messageSource = source;
        _messageHook = OnWindowMessage;
        source.AddHook(_messageHook);

    }

    private void OnViewUnloaded(object sender, RoutedEventArgs e)
    {
        if (_columnsHooked)
        {
            _columnsHooked = false;
            ColumnsChanged -= OnColumnsChanged;
        }

        if (_messageSource is not null && _messageHook is not null)
        {
            _messageSource.RemoveHook(_messageHook);
        }

        _messageSource = null;
        _messageHook = null;
    }

    /// <summary>
    /// 한글로 친 글쇠를 받아 글자 찾기에 씁니다.
    ///
    /// 목록에는 입력기가 붙지 않습니다. 그래서 한글을 치면 입력기가 글쇠를 삼키고
    /// 완성된 글자는 어디로도 오지 않습니다. 창에 남는 것은 글쇠를 뗐다는 소식뿐입니다.
    /// 그 글쇠를 두벌식 자모로 바꾸어 쓰면 입력기를 거치지 않고도 찾을 수 있습니다.
    ///
    /// 영문은 글자가 제대로 오므로 이 길을 타지 않습니다.
    /// 글쇠를 누른 뒤 글자가 하나 나왔는지로 둘을 가릅니다.
    /// </summary>
    private IntPtr OnWindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int KeyDown = 0x0100;
        const int KeyUp = 0x0101;
        const int Letter = 0x0102;

        if (message == Letter)
        {
            _keyMadeLetter = true;
            return IntPtr.Zero;
        }

        if (message == KeyDown)
        {
            _keyMadeLetter = false;
            return IntPtr.Zero;
        }

        if (message != KeyUp)
        {
            return IntPtr.Zero;
        }

        var madeLetter = _keyMadeLetter;
        _keyMadeLetter = false;

        // 초점은 이 패널 안이기만 하면 됩니다. 줄 하나에 있든 도구 모음에 있든 같습니다.
        var mine = IsKeyboardFocusWithin;

        // 숨은 글 상자가 초점을 쥐고 있는 일이 있습니다. 보이지 않는 칸에 글자를 넣을 수는 없으므로
        // 그런 경우는 글 상자가 없는 것으로 칩니다.
        var focusedBox = Keyboard.FocusedElement as TextBox;
        var onTextBox = focusedBox is { IsVisible: true };
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var jamo = HangulKeys.FromVirtualKey((int)wParam, shift);

        if (madeLetter
            || !mine
            || onTextBox
            || jamo is null
            || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0)
        {
            return IntPtr.Zero;
        }

        ForgetTypedIfIdle();
        _typed += jamo;

        JumpTo(_typed, repeating: false);

        return IntPtr.Zero;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is FileListViewModel previous)
        {
            RememberScroll(previous);

            previous.EnsureVisibleRequested -= OnEnsureVisibleRequested;
            previous.PropertyChanged -= OnViewModelPropertyChanged;
            previous.SearchRequested -= OnSearchRequested;
        }

        if (e.NewValue is FileListViewModel current)
        {
            current.EnsureVisibleRequested += OnEnsureVisibleRequested;
            current.PropertyChanged += OnViewModelPropertyChanged;
            current.SearchRequested += OnSearchRequested;
        }

        ApplyColumns();
        ApplyViewMode();
        _editingAddress = false;
        SyncAddressMode();
        RestoreScroll();
    }

    /// <summary>탭을 떠나기 전에 지금 스크롤 자리를 그 탭에 적어 둡니다.</summary>
    private void RememberScroll(FileListViewModel tab)
    {
        if (FindScroller() is not { } scroller)
        {
            return;
        }

        tab.ScrollOffset = scroller.VerticalOffset;
        tab.HorizontalScrollOffset = scroller.HorizontalOffset;
    }

    /// <summary>
    /// 돌아온 탭의 스크롤 자리를 되돌립니다.
    /// 줄이 다 만들어진 뒤라야 움직일 수 있으므로 배치가 끝나기를 기다립니다.
    /// </summary>
    private void RestoreScroll()
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var vertical = viewModel.ScrollOffset;
        var horizontal = viewModel.HorizontalScrollOffset;

        if (vertical <= 0 && horizontal <= 0)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (!ReferenceEquals(ViewModel, viewModel) || FindScroller() is not { } scroller)
                {
                    return;
                }

                scroller.ScrollToVerticalOffset(vertical);
                scroller.ScrollToHorizontalOffset(horizontal);
            }),
            DispatcherPriority.Loaded);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileListViewModel.IsSearching))
        {
            SyncAddressMode();
            return;
        }

        if (e.PropertyName == nameof(FileListViewModel.ViewMode))
        {
            ApplyViewMode();
        }
        else if (e.PropertyName == nameof(FileListViewModel.Items))
        {
            QueueThumbnailSweep();
        }
        else if (e.PropertyName == nameof(FileListViewModel.SortColumn)
                 || e.PropertyName == nameof(FileListViewModel.SortDirection))
        {
            UpdateSortMark();
        }
    }

    /// <summary>
    /// 보기 방식에 맞춰 목록의 겉모습을 통째로 바꿉니다.
    ///
    /// 이 일을 Style 의 DataTrigger 로 하면 안 됩니다.
    /// 바인딩이 늦게 활성화될 때 WPF 가 같은 GridView 를 같은 ListView 에 다시 배정하는데,
    /// 그때 "두 개 이상의 ListView에서 뷰를 공유할 수 없습니다" 예외가 나면서 창이 뜨지 못합니다.
    /// </summary>
    // ── 자세히 보기의 열 ──

    /// <summary>설정에 적힌 대로 열을 다시 세웁니다. 순서·너비·보임 여부를 그대로 따릅니다.</summary>
    private void ApplyColumns()
    {
        if (ViewModel?.ViewOptions is not { } options)
        {
            return;
        }

        // 탭을 옮길 때마다 열을 다시 세우면 깜빡입니다. 구성이 그대로면 손대지 않습니다.
        if (ColumnsAlreadyDrawn(options))
        {
            return;
        }

        foreach (var drawn in _columnKeys.Keys)
        {
            ((INotifyPropertyChanged)drawn).PropertyChanged -= OnColumnWidthChanged;
        }

        _columnKeys.Clear();
        DetailsGrid.Columns.Clear();

        foreach (var column in options.DetailColumns)
        {
            if (!column.Visible || FindSpec(column.Key) is not { } spec)
            {
                continue;
            }

            var drawn = new GridViewColumn
            {
                Header = new GridViewColumnHeader { Content = spec.Header, Tag = spec.SortTag },
                CellTemplate = (DataTemplate)FindResource(spec.Template),
                Width = column.Width
            };

            _columnKeys[drawn] = spec.Key;
            ((INotifyPropertyChanged)drawn).PropertyChanged += OnColumnWidthChanged;
            DetailsGrid.Columns.Add(drawn);
        }

        UpdateSortMark();
    }

    /// <summary>지금 그려 둔 열이 설정과 같은지 봅니다. 같으면 다시 세울 까닭이 없습니다.</summary>
    private bool ColumnsAlreadyDrawn(ViewSettings options)
    {
        var index = 0;

        foreach (var column in options.DetailColumns)
        {
            if (!column.Visible || FindSpec(column.Key) is null)
            {
                continue;
            }

            if (index >= DetailsGrid.Columns.Count
                || !_columnKeys.TryGetValue(DetailsGrid.Columns[index], out var key)
                || !string.Equals(key, column.Key, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            index++;
        }

        return index == DetailsGrid.Columns.Count;
    }

    /// <summary>
    /// 지금 어느 열로, 어느 쪽으로 정렬했는지를 머리글에 표시합니다.
    /// 유형과 확장자는 같은 기준으로 묶이므로 둘 다 표시됩니다.
    /// </summary>
    private void UpdateSortMark()
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var mark = viewModel.SortDirection == SortDirection.Ascending ? "  ▲" : "  ▼";

        foreach (var drawn in DetailsGrid.Columns)
        {
            if (drawn.Header is not GridViewColumnHeader header
                || !_columnKeys.TryGetValue(drawn, out var key)
                || FindSpec(key) is not { } spec)
            {
                continue;
            }

            header.Content = string.Equals(spec.SortTag, viewModel.SortColumn.ToString(), StringComparison.Ordinal)
                ? spec.Header + mark
                : spec.Header;
        }
    }

    /// <summary>열 너비를 끌어 바꾸면 그 자리에서 설정에 적어 둡니다.</summary>
    private void OnColumnWidthChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GridViewColumn.ActualWidth)
            || sender is not GridViewColumn drawn
            || !_columnKeys.TryGetValue(drawn, out var key)
            || ViewModel?.ViewOptions is not { } options)
        {
            return;
        }

        var width = drawn.ActualWidth;

        if (width is < 40 or > 1200)
        {
            return;
        }

        foreach (var column in options.DetailColumns)
        {
            if (string.Equals(column.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                column.Width = width;
                return;
            }
        }
    }

    /// <summary>열 머리글에서 오른쪽 단추를 누르면 어떤 열을 보일지 고르는 메뉴를 띄웁니다.</summary>
    private void ShowColumnMenu()
    {
        if (ViewModel?.ViewOptions is not { } options)
        {
            return;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = List,
            Placement = PlacementMode.MousePoint
        };

        foreach (var column in options.DetailColumns)
        {
            if (FindSpec(column.Key) is not { } spec)
            {
                continue;
            }

            var item = new MenuItem
            {
                Header = spec.Header,
                IsCheckable = true,
                IsChecked = column.Visible,

                // 이름 열은 끌 수 없습니다. 탐색기도 같습니다.
                IsEnabled = !string.Equals(spec.Key, "Name", StringComparison.OrdinalIgnoreCase)
            };

            var target = column;

            item.Click += (_, _) =>
            {
                target.Visible = item.IsChecked;
                ColumnsChanged?.Invoke(null, EventArgs.Empty);
            };

            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        var reset = new MenuItem { Header = "기본 열로 되돌리기" };

        reset.Click += (_, _) =>
        {
            options.DetailColumns = ColumnSettings.CreateDefaults();
            ColumnsChanged?.Invoke(null, EventArgs.Empty);
        };

        menu.Items.Add(reset);
        menu.IsOpen = true;
    }

    private void OnColumnsChanged(object? sender, EventArgs e) => ApplyColumns();

    private static ColumnSpec? FindSpec(string key)
    {
        foreach (var spec in ColumnCatalog)
        {
            if (string.Equals(spec.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return spec;
            }
        }

        return null;
    }

    /// <summary>시각 트리를 거슬러 올라가며 찾습니다.</summary>
    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T found)
            {
                return found;
            }

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }

    private void ApplyViewMode()
    {
        // 보기 방식을 바꾸면 목록의 속 구조가 새로 만들어집니다.
        // 찾아 두었던 부품은 더 이상 지금 화면의 것이 아니므로 버립니다.
        _scroller = null;
        _presenter = null;

        switch (ViewModel?.ViewMode ?? ViewMode.Details)
        {
            case ViewMode.List:
                List.View = null;
                List.ItemTemplate = (DataTemplate)FindResource("ListItemTemplate");
                List.ItemContainerStyle = (Style)FindResource("SimpleRow");
                List.ItemsPanel = (ItemsPanelTemplate)FindResource("ColumnsItemsPanel");

                // 가상화 패널이 굴리는 일을 직접 맡습니다. 그러려면 이 설정이 켜져 있어야 합니다.
                VirtualizingPanel.SetVirtualizationMode(List, VirtualizationMode.Standard);
                ScrollViewer.SetCanContentScroll(List, true);
                ScrollViewer.SetVerticalScrollBarVisibility(List, ScrollBarVisibility.Disabled);
                ScrollViewer.SetHorizontalScrollBarVisibility(List, ScrollBarVisibility.Auto);
                break;

            case ViewMode.LargeIcons:
                List.View = null;
                List.ItemTemplate = (DataTemplate)FindResource("TileItemTemplate");
                List.ItemContainerStyle = (Style)FindResource("TileRow");
                List.ItemsPanel = (ItemsPanelTemplate)FindResource("WrapItemsPanel");

                VirtualizingPanel.SetVirtualizationMode(List, VirtualizationMode.Standard);
                ScrollViewer.SetCanContentScroll(List, true);
                ScrollViewer.SetVerticalScrollBarVisibility(List, ScrollBarVisibility.Auto);
                ScrollViewer.SetHorizontalScrollBarVisibility(List, ScrollBarVisibility.Disabled);
                break;

            default:
                List.ItemTemplate = null;
                if (!ReferenceEquals(List.View, _detailsView))
                {
                    List.View = _detailsView;
                }

                List.ItemContainerStyle = (Style)FindResource("FileRow");
                List.ItemsPanel = (ItemsPanelTemplate)FindResource("VerticalItemsPanel");

                // 자세히 보기는 WPF 가 주는 세로 가상화 패널을 씁니다. 줄을 돌려쓰는 쪽이 낫습니다.
                VirtualizingPanel.SetVirtualizationMode(List, VirtualizationMode.Recycling);
                ScrollViewer.SetCanContentScroll(List, true);
                ScrollViewer.SetVerticalScrollBarVisibility(List, ScrollBarVisibility.Auto);
                ScrollViewer.SetHorizontalScrollBarVisibility(List, ScrollBarVisibility.Auto);
                break;
        }

        // 보기 방식이 바뀌면 썸네일 크기도 달라져 뷰모델이 그림을 버립니다. 다시 받아 옵니다.
        QueueThumbnailSweep();
    }

    private void OnSearchRequested(object? sender, EventArgs e)
    {
        SyncAddressMode();
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    // ── 주소 표시줄 ──

    /// <summary>조각 단추를 보일지, 글자 칸을 보일지 정합니다.</summary>
    private void SyncAddressMode()
    {
        var typing = _editingAddress || (ViewModel?.IsSearching ?? false);

        // 초점을 쥔 채로 감추면 그 칸이 글자를 계속 받아 갑니다. 보이지도 않는데 말입니다.
        // 그래서 감추기 전에 초점을 목록으로 옮깁니다.
        if (!typing && AddressBox.IsKeyboardFocusWithin)
        {
            List.Focus();
        }

        AddressBox.Visibility = typing ? Visibility.Visible : Visibility.Collapsed;
        Crumbs.Visibility = typing ? Visibility.Collapsed : Visibility.Visible;
        AddressBackdrop.Visibility = typing ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>조각이 없는 빈 자리를 누르면 경로를 직접 적는 칸으로 바뀝니다.</summary>
    private void OnAddressBackdropClick(object sender, MouseButtonEventArgs e)
    {
        BeginAddressEditing();
        e.Handled = true;
    }

    private void BeginAddressEditing()
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        viewModel.AddressText = SpecialPaths.ToDisplay(viewModel.CurrentPath);
        _editingAddress = true;
        SyncAddressMode();

        AddressBox.Focus();
        AddressBox.SelectAll();
        RefreshSuggestions();
    }

    private void EndAddressEditing()
    {
        CloseSuggestions();
        _editingAddress = false;
        SyncAddressMode();
    }

    /// <summary>
    /// 주소 칸이 초점을 받으면 언제나 "고치는 중"으로 둡니다.
    ///
    /// 초점만 들어오고 고치는 중이 아니면 칸에는 지금 경로가 그대로 남아 있습니다.
    /// 그 뒤에 글자를 치면 경로 끝에 덧붙게 되어 맞는 폴더가 하나도 없습니다.
    /// 처음에 후보가 안 뜨던 까닭입니다.
    /// </summary>
    private void OnAddressGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_editingAddress)
        {
            _editingAddress = true;
            SyncAddressMode();
        }

        AddressBox.SelectAll();
    }

    /// <summary>
    /// 글쇠를 뗄 때마다 후보를 다시 고릅니다.
    /// 한글을 만드는 동안에는 칸의 글자가 바뀌었다는 신호가 늦게 오거나 건너뛰기도 합니다.
    /// 그래서 글자 바뀜만 믿지 않고 여기서도 한 번 더 살핍니다.
    /// </summary>
    private void OnAddressKeyUp(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up:
            case Key.Down:
            case Key.Left:
            case Key.Right:
            case Key.Enter:
            case Key.Escape:
            case Key.Tab:
                return;
        }

        RefreshSuggestions();
    }

    private void OnAddressLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // 후보를 고르는 중이면 그대로 둡니다. 후보를 누르는 순간에도 초점이 잠시 떠납니다.
        // 다른 곳을 누르면 고치던 것을 접고 조각 단추로 돌아갑니다.
        if (_editingAddress && !AddressSuggestions.IsOpen)
        {
            EndAddressEditing();
        }
    }

    /// <summary>
    /// 적고 있는 글자에 맞는 폴더를 아래에 늘어놓습니다.
    ///
    /// 칸 안의 글자를 대신 채우지 않습니다.
    /// 한글은 글자를 만드는 중에 칸의 내용을 건드리면 조합이 깨져 글자가 뭉개집니다.
    /// </summary>
    private void OnAddressTextChanged(object sender, TextChangedEventArgs e) => RefreshSuggestions();

    private void RefreshSuggestions()
    {
        if (ViewModel is null || ViewModel.IsSearching || !AddressBox.IsKeyboardFocusWithin)
        {
            CloseSuggestions();
            return;
        }

        var found = FindFolders(AddressBox.Text);

        if (found.Count == 0)
        {
            CloseSuggestions();
            return;
        }

        // 고르고 있던 것이 그대로 남아 있으면 그 자리를 지켜 줍니다.
        var keep = SuggestionList.SelectedItem as string;

        SuggestionList.ItemsSource = found;
        SuggestionList.SelectedIndex = keep is null ? -1 : IndexOfSuggestion(found, keep);

        if (AddressBox.ActualWidth > 0)
        {
            AddressSuggestions.MinWidth = AddressBox.ActualWidth;
        }

        AddressSuggestions.IsOpen = true;
    }

    private static int IndexOfSuggestion(IReadOnlyList<string> found, string wanted)
    {
        for (var index = 0; index < found.Count; index++)
        {
            if (string.Equals(found[index], wanted, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private void CloseSuggestions()
    {
        AddressSuggestions.IsOpen = false;
        SuggestionList.ItemsSource = null;
    }

    /// <summary>
    /// 적은 글자에 맞는 폴더를 찾습니다.
    /// 역슬래시가 있으면 그 앞을 부모로 삼고, 없으면 지금 보고 있는 폴더 안에서 찾습니다.
    /// </summary>
    private IReadOnlyList<string> FindFolders(string text)
    {
        var empty = Array.Empty<string>();

        if (ViewModel is not { } viewModel)
        {
            return empty;
        }

        string parent;
        string typed;

        var separator = text.LastIndexOf(Path.DirectorySeparatorChar);

        if (separator >= 0)
        {
            parent = text[..(separator + 1)];
            typed = text[(separator + 1)..];
        }
        else
        {
            // 경로를 적지 않고 이름만 쳤습니다. 지금 폴더 안에서 찾아 줍니다.
            parent = viewModel.CurrentPath;

            if (parent.Length == 0 || SpecialPaths.IsVirtual(parent))
            {
                return empty;
            }

            if (!parent.EndsWith(Path.DirectorySeparatorChar))
            {
                parent += Path.DirectorySeparatorChar;
            }

            typed = text;
        }

        try
        {
            var found = new List<string>(12);

            foreach (var folder in Directory.EnumerateDirectories(parent))
            {
                var name = Path.GetFileName(folder);

                if (typed.Length > 0 && !name.StartsWith(typed, StringComparison.CurrentCultureIgnoreCase))
                {
                    continue;
                }

                found.Add(folder);

                if (found.Count == 12)
                {
                    break;
                }
            }

            return found;
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or ArgumentException
                                       or NotSupportedException)
        {
            // 아직 없는 폴더를 적는 중입니다. 보여 줄 것이 없습니다.
            return empty;
        }
    }

    private void OnSuggestionClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(SuggestionList, source) is not ListBoxItem { Content: string path })
        {
            return;
        }

        e.Handled = true;
        AcceptSuggestion(path);
    }

    private void AcceptSuggestion(string path)
    {
        CloseSuggestions();

        if (ViewModel is not { } viewModel)
        {
            return;
        }

        viewModel.AddressText = path;
        viewModel.NavigateToAddressAsync().Fire("주소로 이동", viewModel);
        EndAddressEditing();
        List.Focus();
    }

    /// <summary>후보 목록에서 위아래로 옮겨 다닙니다.</summary>
    private void MoveSuggestion(int step)
    {
        if (SuggestionList.Items.Count == 0)
        {
            return;
        }

        var next = SuggestionList.SelectedIndex + step;

        if (next < 0)
        {
            next = SuggestionList.Items.Count - 1;
        }
        else if (next >= SuggestionList.Items.Count)
        {
            next = 0;
        }

        SuggestionList.SelectedIndex = next;
        SuggestionList.ScrollIntoView(SuggestionList.SelectedItem);
    }

    // ── 글자를 쳐서 항목 찾기 ──

    /// <summary>
    /// 글자가 확정되면 불립니다. 영문과 숫자는 조합 없이 곧바로 여기로 옵니다.
    /// 1초 넘게 쉬면 앞서 친 글자는 잊습니다. 같은 글자를 거듭 누르면 다음 것으로 넘어갑니다.
    /// </summary>
    private void OnListTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.OriginalSource is TextBox
            || string.IsNullOrEmpty(e.Text)
            || char.IsControl(e.Text[0]))
        {
            return;
        }

        ForgetTypedIfIdle();

        var repeating = _typed.Length == 1 && _typed == e.Text;

        if (!repeating)
        {
            _typed += e.Text;
        }

        if (JumpTo(_typed, repeating))
        {
            e.Handled = true;
        }
    }

    private void ForgetTypedIfIdle()
    {
        var now = DateTime.UtcNow;

        if (now - _typedAt > TimeSpan.FromSeconds(1))
        {
            _typed = string.Empty;
        }

        _typedAt = now;
    }

    /// <summary>친 글자로 시작하는 첫 항목으로 뜁니다. 찾았으면 true입니다.</summary>
    private bool JumpTo(string needle, bool repeating)
    {
        if (ViewModel is not { } viewModel || needle.Length == 0)
        {
            return false;
        }

        var items = viewModel.Items;

        if (items.Count == 0)
        {
            return false;
        }

        var startAt = 0;

        if (repeating)
        {
            for (var index = 0; index < items.Count; index++)
            {
                if (ReferenceEquals(items[index], viewModel.SelectedItem))
                {
                    startAt = index + 1;
                    break;
                }
            }
        }

        for (var step = 0; step < items.Count; step++)
        {
            var item = items[(startAt + step) % items.Count];

            var matched = HangulKeys.HasJamo(needle)
                ? HangulKeys.StartsWithJamo(item.Name, needle)
                : item.Name.StartsWith(needle, StringComparison.CurrentCultureIgnoreCase);

            if (!matched)
            {
                continue;
            }

            List.SelectedItems.Clear();
            List.SelectedItem = item;
            List.ScrollIntoView(item);
            return true;
        }

        return false;
    }

    private void OnEnsureVisibleRequested(object? sender, FileItemViewModel item)
    {
        List.ScrollIntoView(item);
        List.UpdateLayout();
    }

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down when AddressSuggestions.IsOpen:
                e.Handled = true;
                MoveSuggestion(1);
                break;

            case Key.Up when AddressSuggestions.IsOpen:
                e.Handled = true;
                MoveSuggestion(-1);
                break;

            case Key.Enter when AddressSuggestions.IsOpen && SuggestionList.SelectedItem is string chosen:
                e.Handled = true;
                AcceptSuggestion(chosen);
                break;

            case Key.Enter:
                e.Handled = true;
                CloseSuggestions();
                ViewModel.NavigateToAddressAsync().Fire("주소로 이동", ViewModel);
                EndAddressEditing();
                List.Focus();
                break;

            case Key.Escape when AddressSuggestions.IsOpen:
                e.Handled = true;
                CloseSuggestions();
                break;

            case Key.Escape when ViewModel.IsSearching:
                e.Handled = true;
                ViewModel.ExitSearch();
                List.Focus();
                break;

            case Key.Escape:
                e.Handled = true;
                EndAddressEditing();
                List.Focus();
                break;
        }
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 빈 자리를 두 번 눌렀을 때는 아무 일도 하지 않습니다.
        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(List, source) is not ListViewItem)
        {
            return;
        }

        if (ViewModel?.SelectedItem is { } item)
        {
            ViewModel.OpenAsync(item).Fire("열기", ViewModel);
        }
    }

    /// <summary>셸 대화상자가 우리 창을 부모로 삼도록 창 손잡이를 넘깁니다.</summary>
    private IntPtr OwnerHandle =>
        Window.GetWindow(this) is { } window
            ? new WindowInteropHelper(window).Handle
            : IntPtr.Zero;

    private IReadOnlyList<FileItemViewModel> Selection
    {
        get
        {
            var items = new List<FileItemViewModel>(List.SelectedItems.Count);

            foreach (var entry in List.SelectedItems)
            {
                if (entry is FileItemViewModel item)
                {
                    items.Add(item);
                }
            }

            return items;
        }
    }

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        using var step = UiWatch.Step("목록에서 고름이 바뀜");

        ViewModel?.ReportSelection(Selection);
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        switch (e.Key)
        {
            // 조건이 붙은 쪽을 먼저 둡니다. 순서를 바꾸면 뒤엣것이 영영 안 걸립니다.
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.Alt:
                e.Handled = true;
                ViewModel.ShowProperties(ViewModel.SelectedItem, OwnerHandle);
                break;

            case Key.Enter:
                e.Handled = true;
                ViewModel.OpenAsync(ViewModel.SelectedItem).Fire("열기", ViewModel);
                break;

            case Key.Back:
                e.Handled = true;

                if (ViewModel.UpCommand.CanExecute(null))
                {
                    ViewModel.UpCommand.Execute(null);
                }

                break;

            case Key.F2:
                e.Handled = true;
                ViewModel.SelectedItem?.BeginEdit();
                break;

            case Key.F5:
                e.Handled = true;
                ViewModel.RefreshCommand.Execute(null);
                break;

            case Key.L when Keyboard.Modifiers == ModifierKeys.Control:
                e.Handled = true;
                BeginAddressEditing();
                break;

            case Key.F when Keyboard.Modifiers == ModifierKeys.Control:
                e.Handled = true;

                if (ViewModel.SearchCommand.CanExecute(null))
                {
                    ViewModel.SearchCommand.Execute(null);
                }

                break;

            case Key.Escape when ViewModel.IsSearching:
                e.Handled = true;
                ViewModel.ExitSearch();
                break;

            case Key.C when Keyboard.Modifiers == ModifierKeys.Control:
                e.Handled = true;
                ViewModel.CopyToClipboard(Selection, cut: false);
                break;

            case Key.X when Keyboard.Modifiers == ModifierKeys.Control:
                e.Handled = true;
                ViewModel.CopyToClipboard(Selection, cut: true);
                break;

            case Key.Z when Keyboard.Modifiers == ModifierKeys.Control:
                e.Handled = true;
                ViewModel.UndoAsync(OwnerHandle).Fire("되돌리기", ViewModel);
                break;

            case Key.P when Keyboard.Modifiers == ModifierKeys.Control:
                e.Handled = true;
                OpenBatchPrint();
                break;

            case Key.R when Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                e.Handled = true;
                OpenBatchRename();
                break;

            case Key.V when Keyboard.Modifiers == ModifierKeys.Control:
                e.Handled = true;
                ViewModel.PasteAsync(OwnerHandle).Fire("붙여넣기", ViewModel);
                break;

            case Key.Delete when ViewModel.IsRecycleBin:
                e.Handled = true;

                // 이미 휴지통 안입니다. 더 보낼 데가 없으니 아주 지우기를 묻습니다.
                OnMenuPurge(this, e);
                break;

            case Key.Delete:
                e.Handled = true;

                // Shift를 함께 누르면 휴지통을 거치지 않습니다. 탐색기와 같은 규칙입니다.
                var permanent = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
                ViewModel.DeleteAsync(Selection, permanent, OwnerHandle).Fire("삭제", ViewModel);
                break;
        }
    }

    // ── 오른쪽 클릭 메뉴 ──

    /// <summary>
    /// 윈도우 탐색기의 메뉴를 그대로 띄웁니다.
    /// 줄 위에서 누르면 그 파일들의 메뉴, 빈 자리에서 누르면 폴더의 메뉴입니다.
    /// </summary>
    private void OnListRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        _rightHolding = false;

        // 오른쪽 단추로 끌었으면 이 뗌은 끌기의 끝입니다. 메뉴는 놓은 자리에서 따로 띄웁니다.
        if (_rightDragged)
        {
            _rightDragged = false;
            e.Handled = true;
            return;
        }

        // 열 머리글 위라면 셸 메뉴 대신 열을 고르는 메뉴를 띄웁니다.
        if (FindAncestor<GridViewColumnHeader>(e.OriginalSource as DependencyObject) is not null)
        {
            e.Handled = true;
            ShowColumnMenu();
            return;
        }

        DiagnosticLog.Write("우클릭 감지");

        if (ViewModel is null)
        {
            DiagnosticLog.Write("  뷰모델 없음 — 중단");
            return;
        }

        e.Handled = true;

        // PointToScreen은 실제 화면 픽셀을 돌려줍니다. 메뉴는 그 좌표를 씁니다.
        var screen = List.PointToScreen(e.GetPosition(List));
        var x = (int)screen.X;
        var y = (int)screen.Y;

        var container = e.OriginalSource is DependencyObject source
            ? ItemsControl.ContainerFromElement(List, source) as ListViewItem
            : null;

        // 줄은 목록 폭 전체를 차지하지만, 마지막 열 오른쪽은 사람이 보기에 빈 자리입니다.
        // 그 자리를 누르면 폴더 메뉴가 나와야 합니다. 탐색기와 같은 동작입니다.
        if (container is not null && IsRightOfColumns(container, e))
        {
            container = null;
        }

        if (container?.DataContext is FileItemViewModel item)
        {
            // 고르지 않은 줄에서 눌렀으면 그 줄만 고른 것으로 칩니다. 탐색기와 같은 동작입니다.
            if (!List.SelectedItems.Contains(item))
            {
                List.SelectedItems.Clear();
                List.SelectedItems.Add(item);
            }

            // 휴지통에서는 윈도우 메뉴를 부르지 않습니다.
            // 셸이 보기에 $R 파일은 그냥 파일이라, 되돌리기가 아니라 열기·삭제가 나옵니다.
            if (ViewModel.IsRecycleBin)
            {
                ShowFallbackMenu();
                return;
            }

            var paths = new List<string>();

            foreach (var selected in Selection)
            {
                paths.Add(selected.FullPath);
            }

            DiagnosticLog.Write($"  항목 메뉴 {paths.Count}개, 창 0x{OwnerHandle.ToInt64():X}");

            // 검색 결과일 때는 윈도우 메뉴 맨 위에 "경로 열기"를 하나 얹습니다.
            // Everything이 주는 것과 같은 기능입니다.
            var extra = ViewModel.IsSearching ? "경로 열기" : null;

            if (!ShellContextMenu.ShowForItems(OwnerHandle, paths, x, y, extra, out var openPath))
            {
                ShowFallbackMenu();
            }
            else if (openPath)
            {
                ViewModel.RevealAsync(item.FullPath).Fire("경로 열기", ViewModel);
                return;
            }
        }
        else
        {
            // "내 PC"나 휴지통은 진짜 폴더가 아니라 메뉴를 얻을 수 없습니다.
            if (SpecialPaths.IsVirtual(ViewModel.CurrentPath))
            {
                return;
            }

            DiagnosticLog.Write($"  빈 자리 메뉴: {ViewModel.CurrentPath}");

            // 빈 자리 메뉴는 윈도우 셸 메뉴라, "새로 만들기 > 폴더"를 골라도 셸이 폴더만 만들고 끝납니다.
            // 윈도우 탐색기는 셸이 알려 주는 신호로 이름 편집을 여는데 우리 창은 그 신호를 받지 못합니다.
            // 그래서 메뉴 전후의 폴더 내용을 견주어 새로 생긴 항목을 찾아 이름 편집을 엽니다(2026-09-27).
            var folder = ViewModel.CurrentPath;
            var before = SnapshotNames(folder);

            if (!ShellContextMenu.ShowForFolderBackground(OwnerHandle, folder, x, y))
            {
                // 대비 메뉴는 자기 "새 폴더"가 이름 편집을 직접 엽니다. 여기서 또 찾으면 두 번 엽니다.
                ShowFallbackMenu();
                before = null;
            }

            FollowBackgroundMenuAsync(ViewModel, folder, before).Fire("빈 자리 메뉴 뒤처리", ViewModel);
            return;
        }

        // 메뉴에서 무엇을 했는지 알 수 없으므로 한 번 다시 읽습니다.
        ViewModel.RefreshEverythingAsync().Fire("새로 고침", ViewModel);
    }

    /// <summary>
    /// 빈 자리 셸 메뉴가 닫힌 뒤의 일입니다.
    /// "새로 만들기"로 항목이 하나 생겼으면 그 이름 편집을 열고, 아니면 평소처럼 다시 읽습니다.
    /// 셸이 항목을 조금 늦게 만드는 경우를 생각해 0.5초까지 기다려 봅니다.
    /// </summary>
    private static async Task FollowBackgroundMenuAsync(FileListViewModel viewModel, string folder, HashSet<string>? before)
    {
        if (before is not null)
        {
            for (var attempt = 0; attempt < 6; attempt++)
            {
                if (FindNewMenuItem(folder, before) is { } created)
                {
                    DiagnosticLog.Write($"  새로 만든 항목: {created} — 이름 편집을 엽니다");
                    await viewModel.EditNewItemAsync(created).ConfigureAwait(true);
                    return;
                }

                await Task.Delay(100).ConfigureAwait(true);
            }
        }

        await viewModel.RefreshEverythingAsync().ConfigureAwait(true);
    }

    /// <summary>폴더에 든 이름들입니다. 읽지 못하면 null 입니다(그때는 새 항목 찾기를 건너뜁니다).</summary>
    private static HashSet<string>? SnapshotNames(string folder)
    {
        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            {
                names.Add(Path.GetFileName(path));
            }

            return names;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            DiagnosticLog.Write($"  폴더 내용을 읽지 못해 새 항목 찾기를 건너뜁니다: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 메뉴 전에 없던 항목이 딱 하나 생겼고 이름이 "새 …" 또는 "New …" 이면 그 이름을 돌려줍니다.
    /// 붙여넣기처럼 다른 명령으로 생긴 것까지 편집을 열지 않으려고 이름을 봅니다.
    /// "새로 만들기"가 붙이는 이름은 윈도우 표시 언어에 따라 "새 폴더", "새 텍스트 문서.txt", "New folder" 꼴입니다.
    /// </summary>
    private static string? FindNewMenuItem(string folder, HashSet<string> before)
    {
        var after = SnapshotNames(folder);

        if (after is null)
        {
            return null;
        }

        string? found = null;
        var count = 0;

        foreach (var name in after)
        {
            if (!before.Contains(name))
            {
                found = name;
                count++;
            }
        }

        if (count != 1 || found is null)
        {
            return null;
        }

        return found.StartsWith("새 ", StringComparison.Ordinal)
            || found.StartsWith("New ", StringComparison.OrdinalIgnoreCase)
            ? found
            : null;
    }

    /// <summary>
    /// 누른 자리가 마지막 열보다 오른쪽인지 봅니다.
    /// 자세히 보기에서만 따집니다. 다른 보기에서는 열이라는 것이 없습니다.
    /// </summary>
    private bool IsRightOfColumns(ListViewItem container, MouseButtonEventArgs e)
    {
        if (List.View is not GridView grid)
        {
            return false;
        }

        var columnsWidth = 0.0;

        foreach (var column in grid.Columns)
        {
            columnsWidth += column.ActualWidth;
        }

        var inRow = e.GetPosition(container);
        return inRow.X - container.Padding.Left > columnsWidth;
    }

    /// <summary>윈도우 메뉴를 띄우지 못했을 때 우리 메뉴라도 보여 줍니다.</summary>
    private void ShowFallbackMenu()
    {
        DiagnosticLog.Write("  셸 메뉴 실패 — 대비 메뉴로 넘어감");

        if (FindResource("FallbackMenu") is not ContextMenu menu)
        {
            return;
        }

        menu.PlacementTarget = List;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void OnFallbackMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || ViewModel is null)
        {
            return;
        }

        var hasSelection = List.SelectedItems.Count > 0;
        var single = List.SelectedItems.Count == 1;
        var bin = ViewModel.IsRecycleBin;

        foreach (var entry in menu.Items)
        {
            // 휴지통에서만 쓰는 줄과 그 구분선은 다른 자리에서는 숨깁니다.
            if (entry is Separator separator)
            {
                separator.Visibility = separator.Tag as string == "binsep" && !bin
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                continue;
            }

            if (entry is not MenuItem item || item.Tag is not string tag)
            {
                continue;
            }

            var forBin = tag is "restore" or "purge";

            item.Visibility = forBin == bin || !forBin ? Visibility.Visible : Visibility.Collapsed;

            if (forBin && !bin)
            {
                continue;
            }

            item.IsEnabled = tag switch
            {
                "restore" or "purge" => hasSelection,
                "open" or "properties" or "rename" => single && !bin,
                "cut" or "copy" => hasSelection && !bin,
                "delete" => hasSelection && !bin,
                "paste" => ViewModel.CanPaste,
                "newfolder" => ViewModel.NewFolderCommand.CanExecute(null),
                _ => true
            };
        }
    }

    private void OnMenuOpen(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.OpenAsync(viewModel.SelectedItem).Fire("열기", viewModel);
        }
    }

    private void OnMenuCut(object sender, RoutedEventArgs e) =>
        ViewModel?.CopyToClipboard(Selection, cut: true);

    private void OnMenuCopy(object sender, RoutedEventArgs e) =>
        ViewModel?.CopyToClipboard(Selection, cut: false);

    private void OnMenuPaste(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.PasteAsync(OwnerHandle).Fire("붙여넣기", viewModel);
        }
    }

    private void OnMenuDelete(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.DeleteAsync(Selection, permanent: false, OwnerHandle).Fire("삭제", viewModel);
        }
    }

    private void OnMenuRestore(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.RestoreFromRecycleBinAsync(Selection, OwnerHandle).Fire("되돌리기", viewModel);
        }
    }

    private void OnMenuPurge(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel || Selection.Count == 0)
        {
            return;
        }

        var answer = MessageBox.Show(
            Window.GetWindow(this),
            $"고른 {Selection.Count}개를 아주 지웁니다. 되돌릴 수 없습니다.",
            "휴지통",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        viewModel.PurgeFromRecycleBinAsync(Selection).Fire("아주 지우기", viewModel);
    }

    /// <summary>
    /// 찾기 전용 칸이면 도구 줄을 뒤로·앞으로·위로만 남기고 접습니다.
    /// 새 폴더나 휴지통 같은 단추가 함께 있으면 탐색기 칸과 구별되지 않습니다.
    /// </summary>
    /// <summary>
    /// 도구 줄을 뒤로·앞으로·위로만 남길지입니다.
    /// 검색 창처럼 찾기만 하는 자리에서 켭니다. 칸의 역할이 검색기일 때도 같은 모습이 됩니다.
    /// </summary>
    public bool CompactTools { get; set; }

    private void ApplyPaneRole()
    {
        var search = CompactTools || Pane?.Settings.Role == PaneRole.Search;

        ExtraTools.Visibility = search ? Visibility.Collapsed : Visibility.Visible;
        LocalToggle.Visibility = search ? Visibility.Visible : Visibility.Collapsed;
        NetworkToggle.Visibility = search ? Visibility.Visible : Visibility.Collapsed;
        CloudToggle.Visibility = search ? Visibility.Visible : Visibility.Collapsed;

        ApplyLocalToggle();
        ApplyNetworkToggle();
        ApplyCloudToggle();
    }

    /// <summary>로컬 토글의 색과 풍선말을 지금 상태에 맞춥니다.</summary>
    private void ApplyLocalToggle()
    {
        var on = ViewModel?.SearchLocalDrives ?? true;

        LocalToggle.Foreground = (Brush)FindResource(on ? "Accent" : "Text.Muted");

        LocalToggle.ToolTip = on
            ? "로컬 포함(고정 디스크·USB 메모리 등) — 누르면 뺍니다."
            : "로컬 제외 — 누르면 다시 넣습니다.";
    }

    private void OnToggleLocalClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        viewModel.SearchLocalDrives = !viewModel.SearchLocalDrives;
        ApplyLocalToggle();
    }

    /// <summary>네트워크 드라이브 토글의 색과 풍선말을 지금 상태에 맞춥니다.</summary>
    private void ApplyNetworkToggle()
    {
        var on = ViewModel?.SearchNetworkDrives ?? true;

        NetworkToggle.Foreground = (Brush)FindResource(on ? "Accent" : "Text.Muted");

        NetworkToggle.ToolTip = (on
            ? "네트워크 드라이브 포함 — 누르면 뺍니다. 네트워크 결과는 크기·날짜를 읽는 데 오래 걸립니다."
            : "네트워크 드라이브 제외 — 누르면 다시 넣습니다.")
            + "\n\n" + EverythingNeeded("네트워크 드라이브(예: W:\\)");
    }

    private void OnToggleNetworkClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        viewModel.SearchNetworkDrives = !viewModel.SearchNetworkDrives;
        ApplyNetworkToggle();
    }

    /// <summary>클라우드 토글의 색과 풍선말을 지금 상태에 맞춥니다.</summary>
    private void ApplyCloudToggle()
    {
        var on = ViewModel?.SearchCloudDrives ?? true;

        CloudToggle.Foreground = (Brush)FindResource(on ? "Accent" : "Text.Muted");

        CloudToggle.ToolTip = (on
            ? "클라우드 포함(MYBOX·OneDrive 등) — 누르면 뺍니다."
            : "클라우드 제외 — 누르면 다시 넣습니다.")
            + "\n\n" + EverythingNeeded("클라우드 드라이브(예: MYBOX 의 N:\\)");
    }

    /// <summary>
    /// 네트워크·클라우드 단추 풍선말 뒤에 붙이는 안내입니다.
    /// 이 단추들은 Everything 검색 결과를 거르기만 하므로, Everything 이 그 드라이브를 색인하지 않으면 결과가 나오지 않습니다.
    /// Everything 은 로컬 NTFS 드라이브만 저절로 색인하고, 네트워크·가상 드라이브는 사용자가 폴더 색인으로 더해야 합니다.
    /// (2026-09-26 MYBOX N: 가 검색되지 않아 확인)
    /// </summary>
    private static string EverythingNeeded(string what) =>
        "이 결과를 찾으려면 Everything 이 설치되어 켜져 있어야 하고,\n"
        + $"Everything 의 도구 → 옵션 → 색인 → 폴더에 {what}가 추가되어 있어야 합니다.\n"
        + "Everything 이 꺼져 있으면 지금 폴더 아래만 찾으며, 이 단추는 쓰이지 않습니다.";

    private void OnToggleCloudClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        viewModel.SearchCloudDrives = !viewModel.SearchCloudDrives;
        ApplyCloudToggle();
    }

    /// <summary>
    /// 도구 줄의 찾기 단추입니다. 탭을 건드리지 않고 검색 창을 따로 띄웁니다.
    /// 지금 탭 안에서 찾는 길(Ctrl+F)은 그대로 둡니다.
    /// </summary>
    private void OnSearchPopupClick(object sender, RoutedEventArgs e)
    {
        if (Pane is not { } pane)
        {
            return;
        }

        var window = new SearchWindow(pane, ViewModel?.CurrentPath)
        {
            Owner = Window.GetWindow(this)
        };

        window.SizeToQuarter();
        window.Show();
    }

    private void OnMenuRename(object sender, RoutedEventArgs e) =>
        RunAfterMenuClosed(sender, () => ViewModel?.SelectedItem?.BeginEdit());

    private void OnMenuNewFolder(object sender, RoutedEventArgs e) =>
        RunAfterMenuClosed(sender, () => ViewModel?.NewFolderCommand.Execute(null));

    /// <summary>
    /// 오른쪽 클릭 메뉴가 완전히 닫힌 뒤에 일을 합니다. 이름 편집을 여는 항목(새 폴더·이름 바꾸기)에 씁니다.
    ///
    /// 메뉴는 닫히면서 열리기 전에 초점을 쥐고 있던 목록에 초점을 되돌려 줍니다.
    /// 그 전에 이름 칸이 열려 초점을 받으면, 되돌아온 초점 때문에 이름 칸이 초점을 잃고
    /// OnRenameEditorLostFocus 가 편집을 곧바로 끝냅니다. 그래서 "새 폴더"만 생기고 이름 편집으로 넘어가지 않았습니다(2026-09-27).
    /// 도구 모음 단추는 메뉴를 거치지 않아 이 일이 없었습니다.
    /// </summary>
    private void RunAfterMenuClosed(object sender, Action action)
    {
        var menu = sender is MenuItem item
            ? ItemsControl.ItemsControlFromItemContainer(item) as ContextMenu
            : null;

        // 초점 되돌리기까지 끝난 뒤, 화면 갈래가 한가해졌을 때 합니다.
        void Run() => Dispatcher.BeginInvoke(action, DispatcherPriority.ApplicationIdle);

        if (menu is { IsOpen: true })
        {
            RoutedEventHandler? closed = null;
            closed = (_, _) =>
            {
                menu.Closed -= closed;
                Run();
            };
            menu.Closed += closed;
            return;
        }

        Run();
    }

    private void OnMenuProperties(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.ShowProperties(viewModel.SelectedItem, OwnerHandle);
        }
    }

    private void OnColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader { Tag: string columnName } && ViewModel is not null)
        {
            ViewModel.SortCommand.Execute(columnName);
        }
    }

    /// <summary>
    /// 일괄 인쇄 창을 엽니다 (Ctrl+P). 제목 띠의 단추도 같은 창을 엽니다.
    /// 고른 것이 있으면 그것들을 담고, 없으면 빈 목록으로 엽니다.
    /// </summary>
    private void OpenBatchPrint()
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var paths = new List<string>();

        foreach (var item in Selection)
        {
            paths.Add(item.FullPath);
        }

        PrintWindow.ShowFor(Window.GetWindow(this), paths, viewModel.CurrentPath);
    }

    /// <summary>
    /// 일괄 이름 바꾸기(베타) 창을 엽니다(Ctrl+Shift+R). 설정에서 끈 경우에는 열지 않습니다.
    /// 단추는 제목 띠로 옮겼고, 여는 일은 <see cref="RenameWindow.ShowFor"/> 가 합니다.
    /// </summary>
    private void OpenBatchRename()
    {
        if (!global::Explorer.App.App.Host.Settings.View.ShowBatchRename)
        {
            return;
        }

        RenameWindow.ShowFor(Window.GetWindow(this), ViewModel);
    }

    private void OnViewModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null)
        {
            return;
        }

        // 메뉴는 시각 트리 밖에 있어 DataContext가 자동으로 흘러들지 않습니다.
        button.ContextMenu.DataContext = DataContext;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = PlacementMode.Bottom;
        button.ContextMenu.IsOpen = true;
    }

    private void OnContainersChanged(object? sender, EventArgs e)
    {
        if (List.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
        {
            QueueThumbnailSweep();
        }
    }

    private void OnListScrollChanged(object sender, ScrollChangedEventArgs e) => QueueThumbnailSweep();

    /// <summary>
    /// 화면에 든 줄의 그림을 챙깁니다.
    ///
    /// 60밀리초에 한 번만 돕니다. 스크롤 중에는 이 신호가 쉴 새 없이 오는데,
    /// 그때마다 훑으면 굴리는 동안 화면이 무거워집니다.
    /// </summary>
    private void QueueThumbnailSweep()
    {
        _thumbnailSweep ??= CreateThumbnailSweep();

        if (!_thumbnailSweep.IsEnabled)
        {
            _thumbnailSweep.Start();
        }
    }

    private DispatcherTimer CreateThumbnailSweep()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            RequestVisibleThumbnails();
        };

        return timer;
    }

    /// <summary>
    /// 지금 화면에 든 줄에만 그림을 요청합니다.
    ///
    /// 만들어진 줄을 전부 훑으면 안 됩니다.
    /// 창이 자리를 잡는 동안 목록 전체가 한꺼번에 만들어지는 때가 있어,
    /// 파일이 수천 개인 폴더를 열면 보이지도 않는 줄까지 셸을 부르게 됩니다.
    /// </summary>
    private void RequestVisibleThumbnails()
    {
        if (ViewModel is not { } viewModel || FindItemsHost() is not { } host)
        {
            return;
        }

        using var step = UiWatch.Step("보이는 줄에 그림 요청");

        var area = ItemsArea();

        if (area.IsEmpty || area.Height <= 0)
        {
            return;
        }

        // 위아래로 반 화면씩 미리 챙깁니다. 굴리기 시작하자마자 빈칸이 보이지 않게 합니다.
        var reach = new Rect(
            area.X,
            area.Y - (area.Height / 2),
            area.Width,
            area.Height * 2);

        foreach (var child in host.Children)
        {
            if (child is not ListViewItem { DataContext: FileItemViewModel item } container
                || !container.IsVisible)
            {
                continue;
            }

            if (reach.IntersectsWith(BoundsInLayer(container)))
            {
                _ = viewModel.RequestThumbnailAsync(item);
            }
        }
    }

    /// <summary>줄을 담는 판입니다. 여기 들어 있는 것이 곧 지금 만들어져 있는 줄입니다.</summary>
    private Panel? FindItemsHost()
    {
        if (FindDescendant<ItemsPresenter>(List) is not { } presenter
            || VisualTreeHelper.GetChildrenCount(presenter) == 0)
        {
            return null;
        }

        return VisualTreeHelper.GetChild(presenter, 0) as Panel;
    }

    /// <summary>
    /// 이름 상자가 화면에 나타나는 순간에 커서를 주고 이름을 고릅니다.
    ///
    /// Loaded 로는 안 됩니다. 상자는 줄이 그려질 때 이미 만들어져 있고 숨어 있을 뿐이라,
    /// Loaded 는 F2 를 누르기 한참 전에 지나가 버립니다.
    /// </summary>
    private void OnRenameEditorVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox box || !box.IsVisible)
        {
            return;
        }

        // 막 나타난 참이라 아직 자리를 잡지 못했습니다. 배치가 끝난 뒤에 커서를 줍니다.
        _ = box.Dispatcher.BeginInvoke(new Action(() => FocusRenameEditor(box)), DispatcherPriority.Input);
    }

    /// <summary>
    /// 커서를 주고 이름을 골라 둡니다. 바로 글자를 치면 이름이 통째로 바뀝니다.
    /// 파일은 윈도우 탐색기처럼 확장자를 뺀 부분만 고르고, 폴더는 이름 전체를 고릅니다.
    /// </summary>
    private static void FocusRenameEditor(TextBox box)
    {
        if (!box.IsVisible)
        {
            return;
        }

        box.Focus();
        Keyboard.Focus(box);

        var text = box.Text;
        var length = text.Length;

        if (box.DataContext is FileItemViewModel { IsDirectory: false })
        {
            var dot = text.LastIndexOf('.');

            // 맨 앞의 점은 확장자가 아니라 숨김 파일 표시입니다.
            if (dot > 0)
            {
                length = dot;
            }
        }

        box.Select(0, length);
    }

    private void OnRenameEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: FileItemViewModel item } || ViewModel is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                ViewModel.CommitRenameAsync(item, OwnerHandle).Fire("이름 바꾸기", ViewModel);
                break;

            case Key.Escape:
                e.Handled = true;
                ViewModel.CancelRename(item);
                List.Focus();
                break;
        }
    }

    private void OnRenameEditorLostFocus(object sender, RoutedEventArgs e)
    {
        // 다른 곳을 누르면 고친 이름을 그대로 적용합니다. 윈도우 탐색기와 같은 동작입니다.
        if (sender is TextBox { DataContext: FileItemViewModel item } && ViewModel is not null)
        {
            ViewModel.CommitRenameAsync(item, OwnerHandle).Fire("이름 바꾸기", ViewModel);
        }
    }

    // ── 끌어다 놓기 ──

    /// <summary>
    /// 왼쪽 단추를 누른 자리와 줄을 적어 둡니다.
    ///
    /// 여러 줄을 고른 상태에서 그중 하나를 누르면 WPF 는 곧바로 그 한 줄만 남깁니다.
    /// 그러면 여러 개를 한꺼번에 끌 수가 없으므로, 그때만 누르기를 가로채고
    /// 끌지 않고 놓았을 때 OnListLeftButtonUp 에서 정리합니다.
    /// </summary>
    private void OnListLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 목록을 눌렀으면 글쇠도 목록이 받아야 합니다.
        // 주소 칸이 초점을 쥐고 있으면 친 글자가 그쪽으로 갑니다.
        if (!List.IsKeyboardFocusWithin)
        {
            _editingAddress = false;
            SyncAddressMode();
            List.Focus();
        }

        _pressedItem = null;
        _pendingSelection = null;
        _pressPoint = e.GetPosition(List);

        var container = e.OriginalSource is DependencyObject source
            ? ItemsControl.ContainerFromElement(List, source) as ListViewItem
            : null;

        // 줄은 목록 폭 전체를 차지하지만, 마지막 열 오른쪽은 사람이 보기에 빈 자리입니다.
        if (container is not null && IsRightOfColumns(container, e))
        {
            container = null;
        }

        if (container?.DataContext is FileItemViewModel item)
        {
            _pressedItem = item;

            if (e.ClickCount == 1
                && Keyboard.Modifiers == ModifierKeys.None
                && List.SelectedItems.Count > 1
                && List.SelectedItems.Contains(item))
            {
                _pendingSelection = item;
                container.Focus();
                e.Handled = true;
            }

            return;
        }

        // 빈 자리입니다. 머리글이나 스크롤 막대가 아니면 범위 지정 선택을 시작합니다.
        if (IsChrome(e.OriginalSource))
        {
            return;
        }

        // 줄이 그려지는 칸 안에서 누른 것만 받습니다. 열 머리글 줄에서는 시작하지 않습니다.
        if (!ItemsArea().Contains(e.GetPosition(BandLayer)))
        {
            return;
        }

        BeginBand(e);
    }

    private void OnListLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_bandHolding)
        {
            var wasDrawing = _bandDrawing;
            EndBand();

            // 끌지 않고 빈 자리를 그냥 눌렀으면 고른 것을 모두 풉니다. 탐색기와 같은 동작입니다.
            if (!wasDrawing && Keyboard.Modifiers == ModifierKeys.None)
            {
                List.SelectedItems.Clear();
            }

            return;
        }

        // 끌지 않고 그냥 놓았으면, 미뤄 두었던 대로 누른 줄 하나만 남깁니다.
        if (_pendingSelection is { } item)
        {
            List.SelectedItems.Clear();
            List.SelectedItems.Add(item);
        }

        _pendingSelection = null;
        _pressedItem = null;
    }

    private void OnListLostMouseCapture(object sender, MouseEventArgs e) => EndBand();

    /// <summary>
    /// 아이콘 보기에서 Ctrl+휠로 아이콘 크기를 바꿉니다. 탐색기와 같은 조작입니다.
    /// 다른 보기에서는 손대지 않고 평소대로 굴러가게 둡니다.
    /// </summary>
    private void OnListMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
            && ViewModel is { ViewMode: ViewMode.LargeIcons })
        {
            e.Handled = true;
            Pane?.AdjustIconSize(Math.Sign(e.Delta));
            QueueThumbnailSweep();
            return;
        }

        // 목록 보기는 옆으로 늘어나므로 휠도 옆으로 굴려야 합니다.
        // 세로 스크롤을 꺼 두었기 때문에 그냥 두면 휠이 아무 일도 하지 않습니다.
        if (ViewModel is { ViewMode: ViewMode.List } && FindScroller() is { } scroller)
        {
            e.Handled = true;
            scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - e.Delta);
        }
    }

    /// <summary>이 목록이 들어 있는 패널입니다. 아이콘 크기처럼 패널 전체가 함께 쓰는 값을 맡습니다.</summary>
    private PaneViewModel? Pane
    {
        get
        {
            DependencyObject? node = this;

            while (node is not null)
            {
                if (node is FrameworkElement { DataContext: PaneViewModel pane })
                {
                    return pane;
                }

                node = VisualTreeHelper.GetParent(node);
            }

            return null;
        }
    }

    /// <summary>
    /// 오른쪽 단추를 눌렀을 때입니다. 여기서는 메뉴를 띄우지 않고, 끌기가 될지 지켜보기만 합니다.
    /// 메뉴는 단추를 뗄 때 <see cref="OnListRightButtonUp"/> 이 띄웁니다.
    /// </summary>
    private void OnListRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _rightHolding = true;
        _rightDragged = false;
        _rightPressedItem = null;
        _rightPressPoint = e.GetPosition(List);

        var container = e.OriginalSource is DependencyObject source
            ? ItemsControl.ContainerFromElement(List, source) as ListViewItem
            : null;

        if (container is not null && IsRightOfColumns(container, e))
        {
            container = null;
        }

        if (container?.DataContext is FileItemViewModel item)
        {
            _rightPressedItem = item;
        }
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (_rightHolding)
        {
            if (e.RightButton != MouseButtonState.Pressed)
            {
                _rightHolding = false;
            }
            else if (!_dragging && _rightPressedItem is not null && ViewModel is not null)
            {
                var pushed = e.GetPosition(List) - _rightPressPoint;

                if (Math.Abs(pushed.X) >= SystemParameters.MinimumHorizontalDragDistance
                    || Math.Abs(pushed.Y) >= SystemParameters.MinimumVerticalDragDistance)
                {
                    _rightHolding = false;
                    _rightDragged = true;
                    StartDrag(CollectDragPaths(_rightPressedItem), rightButton: true);
                    _rightPressedItem = null;
                    return;
                }
            }
        }

        if (_bandHolding)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndBand();
                return;
            }

            UpdateBand(e.GetPosition(BandLayer));
            return;
        }

        if (_dragging
            || _pressedItem is null
            || e.LeftButton != MouseButtonState.Pressed
            || ViewModel is null)
        {
            return;
        }

        var moved = e.GetPosition(List) - _pressPoint;

        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var paths = CollectDragPaths(_pressedItem);

        _pendingSelection = null;
        _pressedItem = null;

        StartDrag(paths, rightButton: false);
    }

    /// <summary>누른 줄이 고른 것 안에 있으면 고른 것 전부를, 아니면 그 줄 하나만 끕니다.</summary>
    private List<string> CollectDragPaths(FileItemViewModel pressed)
    {
        var paths = new List<string>();

        if (List.SelectedItems.Contains(pressed))
        {
            foreach (var selected in Selection)
            {
                paths.Add(selected.FullPath);
            }
        }
        else
        {
            paths.Add(pressed.FullPath);
        }

        return paths;
    }

    /// <param name="rightButton">오른쪽 단추로 끄는 경우입니다. 놓을 때 무엇을 할지 묻는 메뉴가 뜹니다.</param>
    private void StartDrag(List<string> paths, bool rightButton)
    {
        if (paths.Count == 0)
        {
            return;
        }

        _dragging = true;

        try
        {
            // 바로가기까지 허용해 두어야 Alt 나 Ctrl+Shift 로 놓았을 때 받아 줍니다.
            DragDrop.DoDragDrop(
                List,
                FileClipboard.CreateData(paths, cut: false),
                DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            DiagnosticLog.Write($"끌기 실패: {ex.Message}");
        }
        finally
        {
            _dragging = false;

            if (rightButton)
            {
                // 끌기가 끝나면 단추를 뗀 자리에서 우클릭 메뉴가 뒤따릅니다. 그 한 번은 건너뜁니다.
                _rightDragged = true;
            }
        }
    }

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;

        var container = ContainerUnder(e);
        var destination = ResolveDropFolder(container);

        if (destination is null
            || !e.Data.GetDataPresent(DataFormats.FileDrop)
            || e.Data.GetData(DataFormats.FileDrop) is not string[] sources)
        {
            e.Effects = DragDropEffects.None;
            SetDropTarget(null);
            return;
        }

        // 오른쪽 단추로 끄는 중인지는 지나갈 때 봐 두어야 합니다. 놓는 순간에는 이미 뗀 뒤일 수 있습니다.
        _dropUsedRightButton = (e.KeyStates & DragDropKeyStates.RightMouseButton) == DragDropKeyStates.RightMouseButton;

        // 오른쪽 단추로 끄는 중이면 무엇을 할지는 놓은 뒤에 메뉴로 묻습니다. 여기서는 옮김 모양만 보여 줍니다.
        var action = _dropUsedRightButton ? DropAction.Move : ResolveAction(e, destination, sources);
        var wanted = EffectFor(action);

        if ((e.AllowedEffects & wanted) != wanted || FilterSources(sources, destination, action).Count == 0)
        {
            e.Effects = DragDropEffects.None;
            SetDropTarget(null);
            return;
        }

        e.Effects = wanted;

        // 폴더 줄 위에 있을 때만 그 줄을 표시합니다. 빈 자리는 현재 폴더가 받으므로 표시하지 않습니다.
        SetDropTarget(container?.DataContext is FileItemViewModel { IsDirectory: true } ? container : null);
    }

    private void OnListDragLeave(object sender, DragEventArgs e) => SetDropTarget(null);

    private void OnListDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        SetDropTarget(null);

        if (ViewModel is null
            || !e.Data.GetDataPresent(DataFormats.FileDrop)
            || e.Data.GetData(DataFormats.FileDrop) is not string[] sources)
        {
            return;
        }

        var destination = ResolveDropFolder(ContainerUnder(e));

        if (destination is null)
        {
            return;
        }

        var rightButton = _dropUsedRightButton
            || (e.KeyStates & DragDropKeyStates.RightMouseButton) == DragDropKeyStates.RightMouseButton;

        _dropUsedRightButton = false;

        if (rightButton)
        {
            ShowDropMenu(sources, destination);
            return;
        }

        var action = ResolveAction(e, destination, sources);
        var filtered = FilterSources(sources, destination, action);

        if (filtered.Count == 0)
        {
            return;
        }

        ViewModel.DropAsync(filtered, destination, action, OwnerHandle).Fire("끌어다 놓기", ViewModel);
    }

    /// <summary>
    /// 오른쪽 단추로 끌어다 놓았을 때 무엇을 할지 묻습니다. 탐색기와 같은 메뉴입니다.
    /// </summary>
    private void ShowDropMenu(IReadOnlyList<string> sources, string destination)
    {
        if (ViewModel is null)
        {
            return;
        }

        var copies = FilterSources(sources, destination, DropAction.Copy);
        var moves = FilterSources(sources, destination, DropAction.Move);
        var links = FilterSources(sources, destination, DropAction.Link);

        if (copies.Count == 0 && moves.Count == 0 && links.Count == 0)
        {
            return;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = List,
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint
        };

        menu.Items.Add(DropMenuItem("여기에 복사", copies, destination, DropAction.Copy));
        menu.Items.Add(DropMenuItem("여기로 이동", moves, destination, DropAction.Move));
        menu.Items.Add(DropMenuItem("여기에 바로가기 만들기", links, destination, DropAction.Link));
        menu.Items.Add(new Separator());

        var cancel = new MenuItem { Header = "취소" };
        menu.Items.Add(cancel);

        menu.IsOpen = true;
    }

    private MenuItem DropMenuItem(string header, List<string> sources, string destination, DropAction action)
    {
        var item = new MenuItem
        {
            Header = header,
            IsEnabled = sources.Count > 0
        };

        item.Click += (_, _) =>
        {
            if (ViewModel is { } viewModel)
            {
                viewModel.DropAsync(sources, destination, action, OwnerHandle).Fire("끌어다 놓기", viewModel);
            }
        };
        return item;
    }

    private static DragDropEffects EffectFor(DropAction action) => action switch
    {
        DropAction.Copy => DragDropEffects.Copy,
        DropAction.Link => DragDropEffects.Link,
        _ => DragDropEffects.Move
    };

    private ListViewItem? ContainerUnder(DragEventArgs e) =>
        e.OriginalSource is DependencyObject source
            ? ItemsControl.ContainerFromElement(List, source) as ListViewItem
            : null;

    /// <summary>놓을 자리를 정합니다. 폴더 줄 위면 그 폴더, 그 밖에는 지금 보고 있는 폴더입니다.</summary>
    private string? ResolveDropFolder(ListViewItem? container)
    {
        if (container?.DataContext is FileItemViewModel { IsDirectory: true } folder)
        {
            return folder.FullPath;
        }

        var current = ViewModel?.CurrentPath;

        return string.IsNullOrEmpty(current) || SpecialPaths.IsVirtual(current)
            ? null
            : current;
    }

    /// <summary>
    /// 복사인지 이동인지 바로가기인지 정합니다. 윈도우 탐색기와 같은 규칙입니다.
    /// Alt 나 Ctrl+Shift 는 바로가기, Ctrl 은 복사, Shift 는 이동,
    /// 아무것도 안 누르면 같은 드라이브 안에서는 이동이고 드라이브를 넘으면 복사입니다.
    /// </summary>
    private static DropAction ResolveAction(DragEventArgs e, string destination, IReadOnlyList<string> sources)
    {
        var control = (e.KeyStates & DragDropKeyStates.ControlKey) == DragDropKeyStates.ControlKey;
        var shift = (e.KeyStates & DragDropKeyStates.ShiftKey) == DragDropKeyStates.ShiftKey;

        // 끌기 중의 Alt 는 KeyStates 에 실려 오지 않으므로 글쇠판 쪽을 직접 봅니다.
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;

        if (alt || (control && shift))
        {
            return DropAction.Link;
        }

        if (control)
        {
            return DropAction.Copy;
        }

        if (shift)
        {
            return DropAction.Move;
        }

        if (sources.Count == 0)
        {
            return DropAction.Copy;
        }

        var sourceRoot = TryGetRoot(sources[0]);
        var destinationRoot = TryGetRoot(destination);

        return sourceRoot is null
            || destinationRoot is null
            || !string.Equals(sourceRoot, destinationRoot, StringComparison.OrdinalIgnoreCase)
                ? DropAction.Copy
                : DropAction.Move;
    }

    /// <summary>제자리에 놓는 것처럼 아무 일도 못 하는 항목을 걸러 냅니다.</summary>
    private static List<string> FilterSources(IReadOnlyList<string> sources, string destination, DropAction action)
    {
        var result = new List<string>(sources.Count);
        var target = Normalize(destination);
        var targetWithSeparator = target.EndsWith(Path.DirectorySeparatorChar)
            ? target
            : target + Path.DirectorySeparatorChar;

        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            var full = Normalize(source);

            // 바로가기는 어디에 만들어도 됩니다. 같은 폴더에 만드는 것도 탐색기가 허용합니다.
            if (action == DropAction.Link)
            {
                result.Add(source);
                continue;
            }

            // 자기 자신 위에 놓은 경우입니다.
            if (string.Equals(full, target, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 폴더를 자기 안쪽으로 옮길 수는 없습니다.
            var fullWithSeparator = full.EndsWith(Path.DirectorySeparatorChar)
                ? full
                : full + Path.DirectorySeparatorChar;

            if (targetWithSeparator.StartsWith(fullWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 이동인데 이미 그 폴더에 있으면 할 일이 없습니다. 복사는 사본이 생기므로 그대로 둡니다.
            if (action == DropAction.Move)
            {
                var parent = Path.GetDirectoryName(full);

                if (parent is not null && string.Equals(Normalize(parent), target, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            result.Add(source);
        }

        return result;
    }

    /// <summary>비교하려고 경로 모양을 맞춥니다. 드라이브 뿌리만 끝의 역슬래시를 남깁니다.</summary>
    private static string Normalize(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);

            return root is not null && full.Length > root.Length
                ? full.TrimEnd(Path.DirectorySeparatorChar)
                : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static string? TryGetRoot(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : root;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// 놓을 자리로 표시할 줄을 바꿉니다.
    /// 직접 지정한 값은 스타일보다 세므로, 표시를 지울 때는 ClearValue 로 스타일에 되돌려 줍니다.
    /// </summary>
    private void SetDropTarget(ListViewItem? container)
    {
        if (ReferenceEquals(_dropTarget, container))
        {
            return;
        }

        _dropTarget?.ClearValue(BackgroundProperty);
        _dropTarget = container;

        if (_dropTarget is not null && TryFindResource("Row.Selected") is Brush brush)
        {
            _dropTarget.Background = brush;
        }
    }

    // ── 범위 지정 선택 ──

    /// <summary>빈 자리를 누른 순간입니다. 아직 상자는 그리지 않고 마우스만 붙잡아 둡니다.</summary>
    private void BeginBand(MouseButtonEventArgs e)
    {
        _bandOrigin = e.GetPosition(BandLayer);
        _bandCurrent = _bandOrigin;
        _bandHolding = true;
        _bandDrawing = false;

        // Ctrl이나 Shift를 누르고 시작하면 이미 고른 것을 그대로 둡니다.
        _bandKeep = Keyboard.Modifiers == ModifierKeys.None
            ? null
            : new HashSet<FileItemViewModel>(Selection);

        List.CaptureMouse();
    }

    /// <summary>상자를 그리고, 그 안에 든 줄을 고릅니다.</summary>
    private void UpdateBand(Point current)
    {
        _bandCurrent = current;

        if (!_bandDrawing)
        {
            if (Math.Abs(current.X - _bandOrigin.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(current.Y - _bandOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _bandDrawing = true;
            BandBox.Visibility = Visibility.Visible;
            StartBandScroller();
        }

        var band = Rect.Intersect(new Rect(_bandOrigin, current), ItemsArea());

        if (band.IsEmpty)
        {
            BandBox.Width = 0;
            BandBox.Height = 0;
        }
        else
        {
            Canvas.SetLeft(BandBox, band.X);
            Canvas.SetTop(BandBox, band.Y);
            BandBox.Width = band.Width;
            BandBox.Height = band.Height;
        }

        ApplyBandSelection(band);
    }

    /// <summary>
    /// 상자에 닿은 줄을 고르고 벗어난 줄은 풉니다.
    /// 화면에 만들어져 있는 줄만 볼 수 있습니다. 가상화 때문에 보이지 않는 줄은 아예 없기 때문입니다.
    /// 그래서 위로 흘러 나간 줄은 나갈 때의 상태를 그대로 지닙니다.
    /// </summary>
    /// <summary>
    /// 상자에 걸린 줄을 고릅니다.
    ///
    /// 만들어 둔 줄만 보고 판단하면 화면 밖으로 굴러 나간 줄은 잡지 못합니다.
    /// 가상화를 넣은 뒤로는 그 줄이 아예 없기 때문입니다.
    /// 그래서 화면에 있는 줄을 재는 대신, 몇 번째 줄이 어디에 놓이는지를 계산해서 가릅니다.
    /// </summary>
    private void ApplyBandSelection(Rect band)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var inBand = IndicesInBand(band);

        // 고른 것을 하나씩 물어보면 목록이 길어질수록 제곱으로 느려집니다. 한 번에 모아 둡니다.
        var selected = new HashSet<FileItemViewModel>();

        foreach (var entry in List.SelectedItems)
        {
            if (entry is FileItemViewModel chosen)
            {
                selected.Add(chosen);
            }
        }

        var items = viewModel.Items;

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var wanted = inBand.Contains(index) || (_bandKeep?.Contains(item) ?? false);
            var now = selected.Contains(item);

            if (wanted == now)
            {
                continue;
            }

            if (wanted)
            {
                List.SelectedItems.Add(item);
            }
            else
            {
                List.SelectedItems.Remove(item);
            }
        }
    }

    /// <summary>상자에 걸리는 줄의 차례들입니다. 보기 방식마다 놓이는 규칙이 달라 갈라집니다.</summary>
    private HashSet<int> IndicesInBand(Rect band)
    {
        var found = new HashSet<int>();

        if (band.IsEmpty || ViewModel is not { } viewModel || viewModel.Items.Count == 0)
        {
            return found;
        }

        var area = ItemsArea();
        var overlap = Rect.Intersect(band, area);

        if (overlap.IsEmpty)
        {
            return found;
        }

        // 줄이 놓이는 자리는 목록 칸의 왼쪽 위를 기준으로 셉니다.
        var local = new Rect(
            overlap.X - area.X,
            overlap.Y - area.Y,
            overlap.Width,
            overlap.Height);

        // 아이콘·목록 보기는 패널이 자기 계산을 그대로 알려 줍니다.
        if (FindDescendant<UniformVirtualizingPanel>(List) is { } panel)
        {
            foreach (var index in panel.IndicesIn(local))
            {
                found.Add(index);
            }

            return found;
        }

        // 자세히 보기입니다. 줄 높이가 모두 같으므로 나누기 한 번이면 됩니다.
        var height = RowHeight();

        if (height <= 0)
        {
            return found;
        }

        // 자세히 보기는 줄 단위로 굴러갑니다. 굴린 양이 픽셀이 아니라 줄 수입니다.
        var scrolled = (FindScroller()?.VerticalOffset ?? 0) * height;
        var first = (int)((scrolled + local.Top) / height);
        var last = (int)((scrolled + local.Bottom - 0.01) / height);

        for (var index = Math.Max(0, first); index <= last && index < viewModel.Items.Count; index++)
        {
            found.Add(index);
        }

        return found;
    }

    private Rect BoundsInLayer(ListViewItem container)
    {
        try
        {
            var origin = container.TransformToVisual(BandLayer).Transform(new Point(0, 0));
            return new Rect(origin, new Size(container.ActualWidth, container.ActualHeight));
        }
        catch (InvalidOperationException)
        {
            // 화면에서 떨어져 나간 줄입니다. 자리를 물을 수 없으니 없는 것으로 칩니다.
            return Rect.Empty;
        }
    }

    /// <summary>
    /// 줄이 실제로 그려지는 자리입니다. 열 머리글은 뺍니다.
    ///
    /// 찾아 둔 부품이 화면에서 떨어져 나갔을 수 있습니다.
    /// 보기 방식을 바꾸면 목록의 속 구조가 통째로 새로 만들어지기 때문입니다.
    /// 그때 좌표를 물으면 "공통 상위 항목이 없다"며 예외가 납니다.
    /// </summary>
    private Rect ItemsArea()
    {
        var whole = new Rect(0, 0, BandLayer.ActualWidth, BandLayer.ActualHeight);

        if (FindPresenter() is not { } presenter || !presenter.IsDescendantOf(this))
        {
            _presenter = null;
            _scroller = null;
            return whole;
        }

        try
        {
            var origin = presenter.TransformToVisual(BandLayer).Transform(new Point(0, 0));
            return new Rect(origin, new Size(presenter.ActualWidth, presenter.ActualHeight));
        }
        catch (InvalidOperationException)
        {
            _presenter = null;
            _scroller = null;
            return whole;
        }
    }

    private void StartBandScroller()
    {
        _bandScroller ??= CreateBandScroller();
        _bandScroller.Start();
    }

    private DispatcherTimer CreateBandScroller()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };

        timer.Tick += (_, _) => ScrollBandEdge();
        return timer;
    }

    /// <summary>
    /// 목록 위아래 밖으로 끌고 나가 있으면 한 줄씩 굴립니다.
    ///
    /// 굴린 만큼 시작점도 함께 옮겨야 합니다.
    /// 그러지 않으면 내용만 움직이고 상자는 제자리에 남아 고르는 범위가 어긋납니다.
    /// 얼마나 움직였는지는 기준으로 잡은 줄이 실제로 옮겨 간 거리로 잽니다.
    /// 스크롤 값의 단위가 보기 방식마다 다르기 때문입니다.
    /// </summary>
    private void ScrollBandEdge()
    {
        if (!_bandDrawing || FindScroller() is not { } scroller)
        {
            return;
        }

        var area = ItemsArea();
        var up = _bandCurrent.Y < area.Top;
        var down = _bandCurrent.Y > area.Bottom;

        if (!up && !down)
        {
            return;
        }

        var marker = FindMarkerIndex(area);
        var before = MarkerTop(marker);

        if (up)
        {
            scroller.LineUp();
        }
        else
        {
            scroller.LineDown();
        }

        scroller.UpdateLayout();

        var after = MarkerTop(marker);

        if (before is not null && after is not null)
        {
            _bandOrigin.Y += after.Value - before.Value;
        }
        else
        {
            _bandOrigin.Y += up ? RowHeight() : -RowHeight();
        }

        UpdateBand(_bandCurrent);
    }

    /// <summary>화면 한가운데쯤에 있는 줄을 기준으로 삼습니다. 한 줄 굴린 뒤에도 남아 있을 자리입니다.</summary>
    private int FindMarkerIndex(Rect area)
    {
        var middle = area.Top + (area.Height / 2);
        var best = -1;
        var bestGap = double.MaxValue;

        for (var index = 0; index < List.Items.Count; index++)
        {
            if (List.ItemContainerGenerator.ContainerFromIndex(index) is not ListViewItem container
                || !container.IsVisible)
            {
                continue;
            }

            var bounds = BoundsInLayer(container);
            var gap = Math.Abs(bounds.Top + (bounds.Height / 2) - middle);

            if (gap < bestGap)
            {
                bestGap = gap;
                best = index;
            }
        }

        return best;
    }

    private double? MarkerTop(int index)
    {
        if (index < 0 || List.ItemContainerGenerator.ContainerFromIndex(index) is not ListViewItem container)
        {
            return null;
        }

        return BoundsInLayer(container).Top;
    }

    private double RowHeight()
    {
        for (var index = 0; index < List.Items.Count; index++)
        {
            if (List.ItemContainerGenerator.ContainerFromIndex(index) is ListViewItem { ActualHeight: > 0 } container)
            {
                return container.ActualHeight;
            }
        }

        return 20;
    }

    private void EndBand()
    {
        if (!_bandHolding)
        {
            return;
        }

        _bandHolding = false;
        _bandDrawing = false;
        _bandKeep = null;
        _bandScroller?.Stop();
        BandBox.Visibility = Visibility.Collapsed;

        if (List.IsMouseCaptured)
        {
            List.ReleaseMouseCapture();
        }
    }

    /// <summary>머리글이나 스크롤 막대처럼 목록의 테두리 부품을 누른 것인지 봅니다.</summary>
    private static bool IsChrome(object? source)
    {
        var node = source as DependencyObject;

        while (node is not null)
        {
            if (node is GridViewColumnHeader or GridViewHeaderRowPresenter or ScrollBar or TextBox)
            {
                return true;
            }

            if (node is ListView)
            {
                return false;
            }

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return false;
    }

    /// <summary>
    /// 목록의 스크롤 뷰어입니다. 한 번 찾아 두되, 화면에서 떨어져 나갔으면 다시 찾습니다.
    /// 보기 방식을 바꾸면 목록의 속 구조가 통째로 새로 만들어집니다.
    /// </summary>
    private ScrollViewer? FindScroller()
    {
        if (_scroller is not null && _scroller.IsDescendantOf(this))
        {
            return _scroller;
        }

        _scroller = FindDescendant<ScrollViewer>(List);
        return _scroller;
    }

    /// <summary>
    /// 줄이 그려지는 칸을 찾습니다.
    ///
    /// 스크롤 뷰어 밑을 곧장 뒤지면 안 됩니다. 열 머리글이 자기만의 스크롤 뷰어 안에 들어 있어서
    /// 먼저 걸리고, 그러면 머리글 줄을 줄 영역으로 잘못 잡습니다.
    /// 그래서 줄을 담는 ItemsPresenter 를 먼저 찾고 거기서 위로 거슬러 올라갑니다.
    /// </summary>
    private ScrollContentPresenter? FindPresenter()
    {
        if (_presenter is not null && _presenter.IsDescendantOf(this))
        {
            return _presenter;
        }

        _presenter = null;

        if (FindDescendant<ItemsPresenter>(List) is not { } items)
        {
            return null;
        }

        DependencyObject? node = items;

        while (node is not null)
        {
            if (node is ScrollContentPresenter found)
            {
                _presenter = found;
                return found;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);

            if (child is T found)
            {
                return found;
            }

            if (FindDescendant<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }
}
