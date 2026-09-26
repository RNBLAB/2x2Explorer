using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Explorer.App.Services;
using Explorer.App.ViewModels;
using Explorer.Core.Models;
using Explorer.Core.Services;
using Explorer.Shell.Operations;

namespace Explorer.App.Views.Dialogs;

/// <summary>
/// 여러 파일의 이름을 표로 놓고 한꺼번에 바꾸는 창입니다.
///
/// 가운데는 둘로 나뉩니다. 왼쪽 1/3 은 "변경 대상 목록"(끌어 놓아 담는 곳), 오른쪽 2/3 은 스프레드시트입니다.
/// 둘은 같은 줄 목록(<see cref="_rows"/>)을 봅니다. 왼쪽은 그 가운데 파일이 있는 줄만, 표는 모든 줄을 보입니다.
/// 그래서 파일을 넣거나 빼거나 표에서 순서를 바꾸면 양쪽이 곧바로 같이 바뀝니다.
///
/// 위쪽 단추들은 '이름 변경 후' 칸을 채워 줄 뿐이고, 실제로 바꾸는 것은 아래 "이름 바꾸기"입니다.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class RenameWindow : Window
{
    /// <summary>표 안에서 줄을 끌 때 쓰는 끌기 형식입니다. 이 창 안에서만 씁니다.</summary>
    private const string RowDragFormat = "Explorer.RenameRows";

    private readonly UndoHistory _undo;

    /// <summary>표의 모든 줄입니다. 파일 없는 줄(붙여 넣은 이름만 있는 줄, 맨 아래 빈 줄)도 들어 있습니다.</summary>
    private readonly ObservableCollection<RenameRowViewModel> _rows = new();

    /// <summary>왼쪽 목록입니다. <see cref="_rows"/> 가운데 파일이 있는 줄만, 같은 차례로 담습니다.</summary>
    private readonly ObservableCollection<RenameRowViewModel> _targets = new();

    private bool _checking;
    private bool _syncingTargets;

    // 줄 끌기 상태
    private Point _dragStart;
    private RenameRowViewModel? _pressedRow;
    private RenameRowViewModel? _anchorRow;
    private bool _deferredSelect;
    private bool _bulk;
    private List<RenameRowViewModel>? _draggedRows;

    public RenameWindow(string? startFolder, UndoHistory undo)
    {
        _undo = undo ?? throw new ArgumentNullException(nameof(undo));

        InitializeComponent();
        PopupTitleBar.Apply(this, fillScreen: true);

        Sheet.ItemsSource = _rows;
        Targets.ItemsSource = _targets;
        _rows.CollectionChanged += OnRowsChanged;
        Sheet.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, AfterCellEdit);

        // Ctrl+C 는 DataGrid 기본 복사 대신 직접 합니다. 기본 복사는 다른 프로그램이 클립보드를 잡고 있으면 예외로 창을 멈춥니다.
        Sheet.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, e) =>
        {
            CopySelectedCells();
            e.Handled = true;
        }));

        EnsureTrailingBlank();
        UpdateNumberSample();

        // 목록은 빈 채로 시작합니다. 오른쪽 탐색기만 부른 쪽의 지금 폴더에서 엽니다.
        SideExplorer.FilesActivated += Add;
        SideExplorer.Start(startFolder);
        TitleBar.EnableFavorites(SideExplorer.OpenFavorite);
    }

    /// <summary>
    /// 창을 엽니다. 제목 띠 단추와 칸의 Ctrl+Shift+R 이 함께 씁니다.
    /// <paramref name="tab"/>은 마지막으로 누른 탐색기 칸의 지금 탭입니다. 오른쪽 탐색기를 그 폴더에서 시작하고,
    /// 되돌리기(Ctrl+Z) 기록을 그 탭에 남기며, 마치면 그 탭을 새로 읽습니다.
    /// </summary>
    public static void ShowFor(Window? owner, FileListViewModel? tab)
    {
        var undo = tab?.Undo ?? global::Explorer.App.App.Host.Undo;
        var dialog = new RenameWindow(tab?.CurrentPath, undo) { Owner = owner };

        if (dialog.ShowDialog() != true || tab is null)
        {
            return;
        }

        tab.ReportStatus($"{dialog.RenamedCount}개의 이름을 바꿨습니다.");
        tab.RefreshAsync().Fire("새로 고침", tab);
    }

    /// <summary>바꾼 개수입니다. 창을 닫은 뒤 부른 쪽이 읽습니다.</summary>
    public int RenamedCount { get; private set; }

    /// <summary>남긴 기록 파일의 경로입니다.</summary>
    public string? LogPath { get; private set; }

    // ── 목록 만들기 ──

    /// <summary>
    /// 파일·폴더를 담습니다. 이미 담긴 것은 건너뜁니다.
    /// 파일 없는 줄(붙여 넣은 이름만 있는 줄)이 있으면 위에서부터 그 줄을 먼저 채우고, 모자라면 아래에 붙입니다.
    /// </summary>
    private void Add(IReadOnlyList<string> paths)
    {
        CommitSheet();

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in _rows)
        {
            if (row.FullPath is { } path)
            {
                known.Add(path);
            }
        }

        var slot = 0;

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !known.Add(path))
            {
                continue;
            }

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                continue;
            }

            while (slot < _rows.Count && _rows[slot].HasFile)
            {
                slot++;
            }

            if (slot < _rows.Count)
            {
                _rows[slot].AssignFile(path);
            }
            else
            {
                _rows.Add(Hook(new RenameRowViewModel(path)));
            }
        }

        EnsureTrailingBlank();
        Recheck();
    }

    private RenameRowViewModel Hook(RenameRowViewModel row)
    {
        row.PropertyChanged += OnRowChanged;
        return row;
    }

    private void Remove(IEnumerable<RenameRowViewModel> rows)
    {
        CommitSheet();

        foreach (var row in new List<RenameRowViewModel>(rows))
        {
            row.PropertyChanged -= OnRowChanged;
            _rows.Remove(row);
        }

        EnsureTrailingBlank();
        Recheck();
    }

    /// <summary>표 맨 아래에 늘 빈 줄 하나를 남겨 둡니다. 새 이름을 적을 자리이자 표가 끝없이 이어진다는 표시입니다.</summary>
    private void EnsureTrailingBlank()
    {
        if (_rows.Count == 0 || !_rows[^1].IsBlank)
        {
            _rows.Add(Hook(new RenameRowViewModel()));
        }
    }

    /// <summary>
    /// 칸 편집이 끝난 뒤에 맨 아래 빈 줄을 채워 둡니다. 편집 중에 줄 목록을 건드리면 DataGrid 가 편집을 놓치므로
    /// 편집이 끝나고 나서 한 박자 늦게 부릅니다. 그 사이 다시 글자를 고치기 시작했으면 다음 편집 끝으로 미룹니다.
    /// </summary>
    private void AfterCellEdit()
    {
        if (IsEditingCell)
        {
            return;
        }

        CommitSheet();
        EnsureTrailingBlank();
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 줄을 여러 번 옮기는 동안에는 검사를 미루고 끝나고 한 번만 합니다(검사는 디스크를 봅니다).
        if (!_bulk)
        {
            Recheck();
        }
    }

    /// <summary>맨 아래 빈 줄입니다. 옮기거나 끌 대상이 아니고, 다른 줄이 그 아래로 가지도 않습니다.</summary>
    private RenameRowViewModel? TrailingBlank => _rows.Count > 0 && _rows[^1].IsBlank ? _rows[^1] : null;

    /// <summary>옮길 수 있는 줄의 끝(맨 아래 빈 줄 바로 앞)입니다.</summary>
    private int MovableEnd => TrailingBlank is null ? _rows.Count : _rows.Count - 1;

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RenameRowViewModel.After) or nameof(RenameRowViewModel.FullPath))
        {
            Recheck();
        }
    }

    /// <summary>표에서 편집 중인 칸이 있으면 먼저 확정합니다. 줄을 옮기거나 지우기 전에 부릅니다.</summary>
    private void CommitSheet()
    {
        Sheet.CommitEdit(DataGridEditingUnit.Row, true);
    }

    // ── 끌어 놓기 (파일) ──

    private bool IsOverExplorer(DragEventArgs e) =>
        e.OriginalSource is DependencyObject over && SideExplorer.IsAncestorOf(over);

    private void OnFilesDragOver(object sender, DragEventArgs e)
    {
        // 오른쪽 탐색기 위에 놓는 것은 받지 않습니다. 탐색기에서 끌어 온 것을 제자리에 놓는 경우입니다.
        if (IsOverExplorer(e))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        // 끄는 쪽이 허락한 효과 안에서 답해야 놓을 수 있습니다.
        // 편집 창 탐색기는 Copy 만 허락하므로 Link 로 답하면 놓기가 거부됩니다(예전 증상).
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFilesDropped(object sender, DragEventArgs e)
    {
        if (IsOverExplorer(e))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Handled = true;

        if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped)
        {
            Add(dropped);
        }
    }

    // ── 왼쪽 목록 ──

    /// <summary>왼쪽 목록을 표의 차례에 맞춥니다. 통째로 새로 채우지 않고 옮기기만 해서 고른 것이 풀리지 않게 합니다.</summary>
    private void SyncTargets()
    {
        var files = new List<RenameRowViewModel>();

        foreach (var row in _rows)
        {
            if (row.HasFile)
            {
                files.Add(row);
            }
        }

        _syncingTargets = true;

        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                if (i < _targets.Count && ReferenceEquals(_targets[i], files[i]))
                {
                    continue;
                }

                var at = _targets.IndexOf(files[i]);

                if (at >= 0)
                {
                    _targets.Move(at, i);
                }
                else
                {
                    _targets.Insert(i, files[i]);
                }
            }

            while (_targets.Count > files.Count)
            {
                _targets.RemoveAt(_targets.Count - 1);
            }
        }
        finally
        {
            _syncingTargets = false;
        }
    }

    /// <summary>왼쪽에서 고르면 표에서도 그 줄을 골라 보여 줍니다.</summary>
    private void OnTargetsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTargets || !Targets.IsKeyboardFocusWithin)
        {
            return;
        }

        var chosen = new List<RenameRowViewModel>();

        foreach (var item in Targets.SelectedItems)
        {
            if (item is RenameRowViewModel row)
            {
                chosen.Add(row);
            }
        }

        SelectRows(InSheetOrder(chosen), focus: false);
    }

    private void OnTargetsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            e.Handled = true;
            RemoveTargets();
        }
    }

    private void OnRemoveTargets(object sender, RoutedEventArgs e) => RemoveTargets();

    private void RemoveTargets()
    {
        var chosen = new List<RenameRowViewModel>();

        foreach (var item in Targets.SelectedItems)
        {
            if (item is RenameRowViewModel row)
            {
                chosen.Add(row);
            }
        }

        Remove(chosen);
    }

    private void OnClearAll(object sender, RoutedEventArgs e) => Remove(_rows);

    // ── 왼쪽 목록: 순서 바꾸기 ──

    /// <summary>왼쪽 목록 안에서 줄을 끌 때 쓰는 끌기 형식입니다. 이 창 안에서만 씁니다.</summary>
    private const string TargetDragFormat = "Explorer.RenameTargets";

    private Point _targetDragStart;
    private ListBoxItem? _targetPressed;
    private bool _targetDeferred;
    private List<RenameRowViewModel>? _draggedTargets;

    /// <summary>왼쪽에서 고른 파일들입니다. 목록 차례대로 돌려줍니다.</summary>
    private List<RenameRowViewModel> SelectedTargets()
    {
        var chosen = new List<RenameRowViewModel>();

        foreach (var item in Targets.SelectedItems)
        {
            if (item is RenameRowViewModel row)
            {
                chosen.Add(row);
            }
        }

        chosen.Sort((left, right) => _targets.IndexOf(left).CompareTo(_targets.IndexOf(right)));
        return chosen;
    }

    private void OnTargetsUp(object sender, RoutedEventArgs e) => MoveTargets(-1);

    private void OnTargetsDown(object sender, RoutedEventArgs e) => MoveTargets(+1);

    /// <summary>고른 파일들을 한 칸 위나 아래로 옮깁니다. 떨어져 있는 파일은 서로의 간격을 지킵니다.</summary>
    private void MoveTargets(int step)
    {
        var chosen = SelectedTargets();

        if (chosen.Count == 0)
        {
            return;
        }

        var order = new List<RenameRowViewModel>(_targets);
        var picked = new HashSet<RenameRowViewModel>(chosen);

        if (step < 0)
        {
            for (var i = 1; i < order.Count; i++)
            {
                if (picked.Contains(order[i]) && !picked.Contains(order[i - 1]))
                {
                    (order[i - 1], order[i]) = (order[i], order[i - 1]);
                }
            }
        }
        else
        {
            for (var i = order.Count - 2; i >= 0; i--)
            {
                if (picked.Contains(order[i]) && !picked.Contains(order[i + 1]))
                {
                    (order[i + 1], order[i]) = (order[i], order[i + 1]);
                }
            }
        }

        ApplyTargetOrder(order, chosen);
    }

    /// <summary>파일 묶음을 왼쪽 목록의 <paramref name="insertAt"/> 자리(옮기기 전 차례 기준)로 옮깁니다.</summary>
    private void MoveTargetsTo(IReadOnlyList<RenameRowViewModel> moving, int insertAt)
    {
        var order = new List<RenameRowViewModel>(_targets);
        var before = 0;

        foreach (var row in moving)
        {
            if (order.IndexOf(row) < insertAt)
            {
                before++;
            }
        }

        foreach (var row in moving)
        {
            order.Remove(row);
        }

        order.InsertRange(Math.Clamp(insertAt - before, 0, order.Count), moving);
        ApplyTargetOrder(order, moving);
    }

    /// <summary>
    /// 왼쪽 목록의 새 차례를 표에 옮깁니다.
    /// 표에서 파일이 있는 줄들이 차지한 자리는 그대로 두고, 그 자리들에 파일 줄을 새 차례로 다시 앉힙니다.
    /// 줄은 통째로 움직이므로 '이름 변경 후' 값도 파일을 따라갑니다. 파일 없는 줄은 제자리에 남습니다.
    /// </summary>
    private void ApplyTargetOrder(IReadOnlyList<RenameRowViewModel> newOrder, IReadOnlyList<RenameRowViewModel> reselect)
    {
        CommitSheet();

        var order = new List<RenameRowViewModel>(_rows);
        var slot = 0;

        for (var i = 0; i < order.Count && slot < newOrder.Count; i++)
        {
            if (order[i].HasFile)
            {
                order[i] = newOrder[slot++];
            }
        }

        _bulk = true;

        try
        {
            for (var i = 0; i < order.Count; i++)
            {
                var at = _rows.IndexOf(order[i]);

                if (at != i)
                {
                    _rows.Move(at, i);
                }
            }
        }
        finally
        {
            _bulk = false;
        }

        Recheck();

        _syncingTargets = true;

        try
        {
            Targets.SelectedItems.Clear();

            foreach (var row in reselect)
            {
                Targets.SelectedItems.Add(row);
            }
        }
        finally
        {
            _syncingTargets = false;
        }

        if (reselect.Count > 0)
        {
            Targets.ScrollIntoView(reselect[0]);

            if (Targets.ItemContainerGenerator.ContainerFromItem(reselect[0]) is ListBoxItem item)
            {
                item.Focus();
            }
        }

        SelectRows(InSheetOrder(reselect), focus: false);
    }

    /// <summary>
    /// 누른 자리를 끌기 기준으로 기억합니다. 여러 개를 고른 채 그 가운데 하나를 그냥 누르면
    /// 떼는 순간까지 선택을 미룹니다(누르는 순간 하나만 남기면 여러 파일을 한꺼번에 끌 수 없습니다).
    /// </summary>
    private void OnTargetsMouseDown(object sender, MouseButtonEventArgs e)
    {
        _targetDragStart = e.GetPosition(Targets);
        _targetPressed = null;
        _targetDeferred = false;

        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(Targets, source) is not ListBoxItem item)
        {
            return;
        }

        _targetPressed = item;

        if (Keyboard.Modifiers == ModifierKeys.None && item.IsSelected && Targets.SelectedItems.Count > 1)
        {
            _targetDeferred = true;
            item.Focus();
            e.Handled = true;
        }
    }

    private void OnTargetsMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_targetDeferred && _targetPressed is { } item)
        {
            Targets.SelectedItems.Clear();
            item.IsSelected = true;
        }

        _targetDeferred = false;
        _targetPressed = null;
    }

    private void OnTargetsMouseMove(object sender, MouseEventArgs e)
    {
        if (_targetPressed is not { } pressed || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var moved = e.GetPosition(Targets) - _targetDragStart;

        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var chosen = SelectedTargets();

        if (pressed.DataContext is RenameRowViewModel row && !chosen.Contains(row))
        {
            chosen = new List<RenameRowViewModel> { row };
        }

        _targetPressed = null;
        _targetDeferred = false;

        if (chosen.Count == 0)
        {
            return;
        }

        e.Handled = true;
        Mouse.Capture(null);
        _draggedTargets = chosen;

        try
        {
            DragDrop.DoDragDrop(Targets, new DataObject(TargetDragFormat, TargetDragFormat), DragDropEffects.Move);
        }
        finally
        {
            _draggedTargets = null;
            TargetsDropLine.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>마우스 자리로 끼워 넣을 차례와 표시선 높이를 셉니다. 항목의 아래쪽 반이면 그 뒤로 봅니다.</summary>
    private int TargetDropIndexAt(DragEventArgs e, out double lineY)
    {
        var overlay = (UIElement)TargetsDropLine.Parent;
        var hit = Targets.InputHitTest(e.GetPosition(Targets)) as DependencyObject;
        var item = hit is null ? null : ItemsControl.ContainerFromElement(Targets, hit) as ListBoxItem;

        if (item?.DataContext is RenameRowViewModel row)
        {
            var lower = e.GetPosition(item).Y > item.ActualHeight / 2;
            lineY = item.TranslatePoint(new Point(0, lower ? item.ActualHeight : 0), overlay).Y;
            return _targets.IndexOf(row) + (lower ? 1 : 0);
        }

        // 항목이 없는 빈 자리입니다. 맨 뒤로 봅니다. 마지막 항목이 화면 밖이면 목록 아래 끝에 선을 긋습니다.
        var last = _targets.Count > 0
            ? Targets.ItemContainerGenerator.ContainerFromItem(_targets[^1]) as ListBoxItem
            : null;
        lineY = last?.TranslatePoint(new Point(0, last.ActualHeight), overlay).Y
            ?? Math.Max(0, ((FrameworkElement)overlay).ActualHeight - 2);
        return _targets.Count;
    }

    private void OnTargetsDragOver(object sender, DragEventArgs e)
    {
        if (_draggedTargets is not null && e.Data.GetDataPresent(TargetDragFormat))
        {
            TargetDropIndexAt(e, out var y);
            TargetsDropLine.Margin = new Thickness(1, Math.Max(0, y - 1), 1, 0);
            TargetsDropLine.Visibility = Visibility.Visible;
            AutoScrollList(Targets, e);

            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        // 파일 끌어 놓기는 창의 처리에 맡깁니다(목록 뒤에 담김).
        TargetsDropLine.Visibility = Visibility.Collapsed;
    }

    private void OnTargetsDragLeave(object sender, DragEventArgs e) => TargetsDropLine.Visibility = Visibility.Collapsed;

    private void OnTargetsDrop(object sender, DragEventArgs e)
    {
        TargetsDropLine.Visibility = Visibility.Collapsed;

        if (_draggedTargets is { Count: > 0 } moving && e.Data.GetDataPresent(TargetDragFormat))
        {
            e.Handled = true;
            MoveTargetsTo(moving, TargetDropIndexAt(e, out _));
        }
    }

    /// <summary>끄는 중에 목록 위·아래 끝에 가까우면 그쪽으로 굴립니다.</summary>
    private static void AutoScrollList(FrameworkElement list, DragEventArgs e)
    {
        if (FindChild<ScrollViewer>(list) is not { } viewer)
        {
            return;
        }

        var y = e.GetPosition(list).Y;
        const double edge = 24;

        if (y < edge)
        {
            viewer.LineUp();
        }
        else if (y > list.ActualHeight - edge)
        {
            viewer.LineDown();
        }
    }

    // ── 표: 고르기 ──

    /// <summary>표에서 고른 칸이 걸친 줄들입니다. 표의 차례대로 돌려줍니다.</summary>
    private List<RenameRowViewModel> SelectedRows()
    {
        var chosen = new HashSet<RenameRowViewModel>();

        foreach (var cell in Sheet.SelectedCells)
        {
            if (cell.Item is RenameRowViewModel row)
            {
                chosen.Add(row);
            }
        }

        return InSheetOrder(chosen);
    }

    private List<RenameRowViewModel> InSheetOrder(IEnumerable<RenameRowViewModel> rows)
    {
        var list = new List<RenameRowViewModel>(rows);
        list.Sort((left, right) => _rows.IndexOf(left).CompareTo(_rows.IndexOf(right)));
        return list;
    }

    /// <summary>줄을 통째로 고릅니다(두 칸 모두).</summary>
    private void SelectRows(IReadOnlyList<RenameRowViewModel> rows, bool focus, RenameRowViewModel? current = null)
    {
        Sheet.SelectedCells.Clear();

        foreach (var row in rows)
        {
            foreach (var column in Sheet.Columns)
            {
                Sheet.SelectedCells.Add(new DataGridCellInfo(row, column));
            }
        }

        var focusRow = current ?? (rows.Count > 0 ? rows[0] : null);

        if (focusRow is not null)
        {
            Sheet.CurrentCell = new DataGridCellInfo(focusRow, Sheet.Columns[1]);
            Sheet.ScrollIntoView(focusRow);
        }

        if (focus)
        {
            Sheet.Focus();
        }
    }

    // ── 검사 ──

    /// <summary>
    /// 줄마다 문제를 다시 보고, 줄 번호·왼쪽 목록·요약을 맞춥니다.
    /// '이름 변경 후' 칸을 한 글자 칠 때마다 도는 일이라 디스크는 한 번씩만 건드립니다.
    /// </summary>
    private void Recheck()
    {
        if (_checking)
        {
            return;
        }

        _checking = true;

        try
        {
            var files = new List<RenameRowViewModel>(_rows.Count);
            var orphans = 0;

            for (var i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                row.Number = i + 1;

                if (row.HasFile)
                {
                    files.Add(row);
                }
                else
                {
                    row.Problem = string.Empty;

                    if (!row.IsBlank)
                    {
                        orphans++;
                    }
                }
            }

            var input = new List<(string Folder, string OldName, string NewName)>(files.Count);

            foreach (var row in files)
            {
                input.Add((row.Folder, row.Before, row.After.Trim()));
            }

            var problems = RenameRules.Check(input);
            var blocked = 0;
            var changed = 0;

            for (var i = 0; i < files.Count; i++)
            {
                var row = files[i];
                var problem = problems[i];

                if (problem == RenameProblem.None && row.Changed && AlreadyThere(row))
                {
                    problem |= RenameProblem.Exists;
                }

                row.Problem = RenameRules.Describe(problem);

                if (problem != RenameProblem.None)
                {
                    blocked++;
                }
                else if (row.Changed)
                {
                    changed++;
                }
            }

            SyncTargets();

            EmptyHint.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TargetsLabel.Text = files.Count == 0 ? "변경 대상 목록" : $"변경 대상 목록 ({files.Count})";

            var summary = new StringBuilder();

            if (files.Count > 0)
            {
                summary.Append($"{files.Count}개 가운데 {changed}개가 바뀝니다.");

                if (blocked > 0)
                {
                    summary.Append($" 붉게 칠한 칸 {blocked}개를 먼저 고쳐 주십시오(칸에 마우스를 올리면 사유가 보입니다).");
                }
            }

            if (orphans > 0)
            {
                summary.Append(summary.Length > 0 ? " " : string.Empty)
                    .Append($"대상 파일이 없는 이름 {orphans}줄은 건너뜁니다.");
            }

            Summary.Text = summary.ToString();
            UpdateNumberSample();
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>그 폴더에 이미 그 이름이 있는지입니다. 자기 자신은 셈에 넣지 않습니다.</summary>
    private static bool AlreadyThere(RenameRowViewModel row)
    {
        try
        {
            var target = Path.Combine(row.Folder, row.After.Trim());

            if (string.Equals(target, row.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return File.Exists(target) || Directory.Exists(target);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    // ── 이름 규칙 ──

    /// <summary>규칙을 걸 줄입니다. 표에서 고른 줄 가운데 파일이 있는 것, 고른 것이 없으면 파일이 있는 모든 줄입니다.</summary>
    private List<RenameRowViewModel> RuleTargets()
    {
        var targets = new List<RenameRowViewModel>();

        foreach (var row in SelectedRows())
        {
            if (row.HasFile)
            {
                targets.Add(row);
            }
        }

        if (targets.Count == 0)
        {
            foreach (var row in _rows)
            {
                if (row.HasFile)
                {
                    targets.Add(row);
                }
            }
        }

        return targets;
    }

    /// <summary>
    /// 규칙을 겁니다. 연번처럼 차례가 필요한 규칙을 위해 몇 번째인지도 함께 넘깁니다.
    /// 고른 차례가 아니라 표에 놓인 차례대로 매깁니다.
    /// </summary>
    private void Apply(Func<RenameRowViewModel, int, int, string> rule)
    {
        CommitSheet();

        var targets = RuleTargets();

        for (var i = 0; i < targets.Count; i++)
        {
            targets[i].After = rule(targets[i], i + 1, targets.Count);
        }

        Recheck();
    }

    /// <summary>
    /// 정규화 창을 엽니다(분석 → 목표 패턴 → 적용). 고른 줄 가운데 파일이 있는 것, 고른 것이 없으면 모든 파일이 대상입니다.
    /// 창은 '변경 전' 이름을 분석하고, 적용하면 결과를 '이름 변경 후' 칸에 채웁니다.
    /// </summary>
    private void OnNormalize(object sender, RoutedEventArgs e)
    {
        CommitSheet();

        var targets = RuleTargets();

        if (targets.Count == 0)
        {
            Summary.Text = "정규화할 파일이 없습니다. 먼저 변경 대상 목록에 파일을 담으십시오.";
            return;
        }

        var dialog = new NormalizeWindow(targets) { Owner = this };

        if (dialog.ShowDialog() == true)
        {
            Recheck();
        }
    }

    private void OnLower(object sender, RoutedEventArgs e) =>
        Apply((row, _, _) => RenameRules.ToLowerName(row.After));

    private void OnUpper(object sender, RoutedEventArgs e) =>
        Apply((row, _, _) => RenameRules.ToUpperName(row.After));

    private void OnRevert(object sender, RoutedEventArgs e) =>
        Apply((row, _, _) => row.Before);

    private void OnChangeExtension(object sender, RoutedEventArgs e) =>
        Apply((row, _, _) => RenameRules.WithExtension(row.After, ExtensionBox.Text));

    private void OnReplace(object sender, RoutedEventArgs e) =>
        Apply((row, _, _) => RenameRules.Replace(row.After, FindBox.Text, ReplaceBox.Text));

    // ── 연번 ──

    private bool NumberAtFront => NumberPositionBox?.SelectedIndex != 1;

    private string NumberSeparator =>
        (NumberSeparatorBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "_";

    /// <summary>자릿수입니다. '자동'이면 개수에 맞춥니다.</summary>
    private int NumberDigits(int count)
    {
        var tag = (NumberDigitsBox?.SelectedItem as ComboBoxItem)?.Tag as string;
        return int.TryParse(tag, out var digits) && digits > 0 ? digits : RenameRules.DigitsFor(count);
    }

    private void OnNumber(object sender, RoutedEventArgs e)
    {
        var atFront = NumberAtFront;
        var separator = NumberSeparator;

        Apply((row, index, count) =>
            RenameRules.Numbered(row.After, index, NumberDigits(count), atFront, separator));
    }

    private void OnNumberOptionChanged(object sender, SelectionChangedEventArgs e) => UpdateNumberSample();

    /// <summary>고른 선택지로 어떻게 붙는지 보기 글로 보입니다.</summary>
    private void UpdateNumberSample()
    {
        if (NumberSample is null)
        {
            return;
        }

        var count = Math.Max(1, RuleTargetsCountQuick());
        NumberSample.Text = "예: " + RenameRules.Numbered("이름.pdf", 1, NumberDigits(count), NumberAtFront, NumberSeparator);
    }

    private int RuleTargetsCountQuick()
    {
        var count = 0;

        foreach (var row in _rows)
        {
            if (row.HasFile)
            {
                count++;
            }
        }

        return count;
    }

    // ── 표: 순서 바꾸기 ──

    private void OnMoveUp(object sender, RoutedEventArgs e) => MoveSelected(-1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => MoveSelected(+1);

    /// <summary>고른 줄들을 한 칸 위나 아래로 옮깁니다. 떨어져 있는 줄은 서로의 간격을 지킵니다.</summary>
    private void MoveSelected(int step)
    {
        CommitSheet();

        var chosen = SelectedRows();

        if (TrailingBlank is { } blank)
        {
            chosen.Remove(blank);
        }

        if (chosen.Count == 0)
        {
            return;
        }

        var first = _rows.IndexOf(chosen[0]);
        var last = _rows.IndexOf(chosen[^1]);

        if ((step < 0 && first == 0) || (step > 0 && last >= MovableEnd - 1))
        {
            return;
        }

        _bulk = true;

        try
        {
        if (step < 0)
        {
            foreach (var row in chosen)
            {
                var at = _rows.IndexOf(row);
                _rows.Move(at, at - 1);
            }
        }
        else
        {
            for (var i = chosen.Count - 1; i >= 0; i--)
            {
                var at = _rows.IndexOf(chosen[i]);
                _rows.Move(at, at + 1);
            }
        }
        }
        finally
        {
            _bulk = false;
        }

        EnsureTrailingBlank();
        Recheck();
        SelectRows(chosen, focus: true);
    }

    /// <summary>
    /// 줄 묶음을 <paramref name="insertAt"/> 자리(옮기기 전 차례 기준)로 옮깁니다.
    /// 옮기는 줄끼리의 차례는 지킵니다.
    /// </summary>
    private void MoveRowsTo(IReadOnlyList<RenameRowViewModel> requested, int insertAt)
    {
        CommitSheet();

        var moving = new List<RenameRowViewModel>(requested);

        if (TrailingBlank is { } blank)
        {
            moving.Remove(blank);
        }

        if (moving.Count == 0)
        {
            return;
        }

        insertAt = Math.Min(insertAt, MovableEnd);

        var order = new List<RenameRowViewModel>(_rows);
        var before = 0;

        foreach (var row in moving)
        {
            if (order.IndexOf(row) < insertAt)
            {
                before++;
            }
        }

        foreach (var row in moving)
        {
            order.Remove(row);
        }

        order.InsertRange(Math.Clamp(insertAt - before, 0, order.Count), moving);

        _bulk = true;

        try
        {
            for (var i = 0; i < order.Count; i++)
            {
                var at = _rows.IndexOf(order[i]);

                if (at != i)
                {
                    _rows.Move(at, i);
                }
            }
        }
        finally
        {
            _bulk = false;
        }

        EnsureTrailingBlank();
        Recheck();
        SelectRows(moving, focus: true);
    }

    private void OnDeleteRows(object sender, RoutedEventArgs e) => Remove(SelectedRows());

    // ── 표: 줄 끌기 ──

    private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        while (start is not null and not T)
        {
            start = start is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(start)
                : LogicalTreeHelper.GetParent(start);
        }

        return start as T;
    }

    /// <summary>
    /// 줄 머리(번호 칸)를 누를 때의 고르기를 직접 합니다. DataGrid 에 맡기면 누르는 순간 끌어 고르기가 시작되어
    /// 여러 줄을 한꺼번에 끌 수 없고, Ctrl 로 누르면 끌려던 줄이 먼저 풀립니다.
    /// - 그냥 누르기: 그 줄만 고릅니다. 이미 고른 여러 줄 가운데 하나면 떼는 순간까지 미룹니다(끌기용).
    /// - Ctrl: 그 줄을 더하거나 뺍니다.  - Shift: 기준 줄부터 그 줄까지 고릅니다(Ctrl+Shift 는 더하기).
    /// </summary>
    private void OnSheetMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressedRow = null;
        _deferredSelect = false;

        var header = FindAncestor<DataGridRowHeader>(e.OriginalSource as DependencyObject);

        if (header is null || FindAncestor<DataGridRow>(header)?.Item is not RenameRowViewModel row)
        {
            return;
        }

        e.Handled = true;
        CommitSheet();
        Sheet.Focus();

        _pressedRow = row;
        _dragStart = e.GetPosition(Sheet);

        var selected = SelectedRows();
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (shift && _anchorRow is { } anchor && _rows.IndexOf(anchor) is var from and >= 0)
        {
            var to = _rows.IndexOf(row);
            var range = new List<RenameRowViewModel>();

            for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++)
            {
                range.Add(_rows[i]);
            }

            if (ctrl)
            {
                foreach (var already in selected)
                {
                    if (!range.Contains(already))
                    {
                        range.Add(already);
                    }
                }
            }

            SelectRows(InSheetOrder(range), focus: false, current: row);
            return;
        }

        _anchorRow = row;

        if (ctrl)
        {
            if (selected.Contains(row))
            {
                selected.Remove(row);
                SelectRows(selected, focus: false, current: selected.Count > 0 ? selected[0] : null);
            }
            else
            {
                selected.Add(row);
                SelectRows(InSheetOrder(selected), focus: false, current: row);
            }

            return;
        }

        if (selected.Contains(row) && selected.Count > 1)
        {
            _deferredSelect = true;
            return;
        }

        SelectRows(new[] { row }, focus: false);
    }

    private void OnSheetMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_deferredSelect && _pressedRow is { } row)
        {
            SelectRows(new[] { row }, focus: false);
        }

        _deferredSelect = false;
        _pressedRow = null;
    }

    private void OnSheetMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedRow is not { } pressed || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var moved = e.GetPosition(Sheet) - _dragStart;

        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var chosen = SelectedRows();

        if (!chosen.Contains(pressed))
        {
            chosen = new List<RenameRowViewModel> { pressed };
        }

        if (TrailingBlank is { } blank)
        {
            chosen.Remove(blank);
        }

        _deferredSelect = false;
        _pressedRow = null;

        if (chosen.Count == 0)
        {
            return;
        }

        e.Handled = true;
        Mouse.Capture(null);
        _draggedRows = chosen;
        _deferredSelect = false;
        _pressedRow = null;

        try
        {
            DragDrop.DoDragDrop(Sheet, new DataObject(RowDragFormat, RowDragFormat), DragDropEffects.Move);
        }
        finally
        {
            _draggedRows = null;
            DropLine.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>마우스 자리로 끼워 넣을 차례와 표시선 높이를 셉니다.</summary>
    private int DropIndexAt(DragEventArgs e, out double lineY)
    {
        var overlay = (UIElement)DropLine.Parent;
        var hit = Sheet.InputHitTest(e.GetPosition(Sheet)) as DependencyObject;
        var row = FindAncestor<DataGridRow>(hit);

        if (row?.Item is RenameRowViewModel item)
        {
            var lower = e.GetPosition(row).Y > row.ActualHeight / 2;
            var index = _rows.IndexOf(item) + (lower ? 1 : 0);
            lineY = row.TranslatePoint(new Point(0, lower ? row.ActualHeight : 0), overlay).Y;
            return index;
        }

        // 줄이 없는 자리입니다. 머리글 위면 맨 앞, 그 밖에는 맨 뒤로 봅니다.
        var header = FindAncestor<DataGridColumnHeadersPresenter>(hit);

        if (header is not null || _rows.Count == 0)
        {
            lineY = header?.TranslatePoint(new Point(0, header.ActualHeight), overlay).Y ?? 0;
            return 0;
        }

        // 맨 아래 빈 줄 바로 위로 봅니다. 그 줄이 화면 밖이라 그려져 있지 않으면 표 아래 끝에 선을 긋습니다.
        var end = MovableEnd;
        var edgeRow = end < _rows.Count ? Sheet.ItemContainerGenerator.ContainerFromItem(_rows[end]) as DataGridRow : null;
        lineY = edgeRow?.TranslatePoint(new Point(0, 0), overlay).Y ?? Math.Max(0, ((FrameworkElement)overlay).ActualHeight - 2);
        return end;
    }

    private void OnSheetDragOver(object sender, DragEventArgs e)
    {
        if (_draggedRows is not null && e.Data.GetDataPresent(RowDragFormat))
        {
            DropIndexAt(e, out var y);
            DropLine.Margin = new Thickness(40, Math.Max(0, y - 1), 0, 0);
            DropLine.Visibility = Visibility.Visible;
            AutoScroll(e);

            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        DropLine.Visibility = Visibility.Collapsed;
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnSheetDragLeave(object sender, DragEventArgs e) => DropLine.Visibility = Visibility.Collapsed;

    private void OnSheetDrop(object sender, DragEventArgs e)
    {
        DropLine.Visibility = Visibility.Collapsed;
        e.Handled = true;

        if (_draggedRows is { Count: > 0 } moving && e.Data.GetDataPresent(RowDragFormat))
        {
            MoveRowsTo(moving, DropIndexAt(e, out _));
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped)
        {
            Add(dropped);
        }
    }

    /// <summary>끄는 중에 표 위·아래 끝에 가까우면 그쪽으로 굴립니다.</summary>
    private void AutoScroll(DragEventArgs e)
    {
        if (FindChild<ScrollViewer>(Sheet) is not { } viewer)
        {
            return;
        }

        var y = e.GetPosition(Sheet).Y;
        const double edge = 28;

        if (y < edge + 24)
        {
            viewer.LineUp();
        }
        else if (y > Sheet.ActualHeight - edge)
        {
            viewer.LineDown();
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

    // ── 표: 복사·붙여넣기·지우기 ──

    /// <summary>표 안에서 글자를 고치는 중인지입니다. 그때는 Ctrl+C·V·Delete 를 글자 칸에 맡깁니다.</summary>
    private bool IsEditingCell => Sheet.IsKeyboardFocusWithin && Keyboard.FocusedElement is TextBox;

    /// <summary>창 안 어느 글자 칸이든 글자를 치는 중인지입니다(찾을 말 칸 등). 그때는 창 단축키가 붙여넣기를 가로채지 않습니다.</summary>
    private bool IsTypingText => Keyboard.FocusedElement is TextBox;

    private void OnSheetKeyDown(object sender, KeyEventArgs e)
    {
        if (IsEditingCell)
        {
            return;
        }

        // 한글 입력기가 켜진 채 칸에 바로 치면 DataGrid 가 첫 글자를 흘리는 일이 있습니다.
        // 입력기가 키를 받은 순간(ImeProcessed) 먼저 편집을 열어 글자 칸이 조합을 받게 합니다.
        if (e.Key == Key.ImeProcessed && Sheet.CurrentCell.Column?.DisplayIndex == 1)
        {
            Sheet.BeginEdit();
            return;
        }

        if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            ClearSelectedCells();
        }
    }

    /// <summary>고른 '이름 변경 후' 칸을 비웁니다. '이름 변경 전' 칸은 목록과 이어져 있어 건드리지 않습니다.</summary>
    private void ClearSelectedCells()
    {
        foreach (var cell in Sheet.SelectedCells)
        {
            if (cell.Column?.DisplayIndex == 1 && cell.Item is RenameRowViewModel row)
            {
                row.After = string.Empty;
            }
        }

        Recheck();
    }

    /// <summary>
    /// 고른 칸을 엑셀과 같은 모양(줄은 줄바꿈, 칸은 탭)으로 복사합니다.
    /// 고른 칸이 들쭉날쭉하면 사각형으로 맞추고 고르지 않은 칸은 비워 둡니다. 엑셀·구글 스프레드시트에 그대로 붙습니다.
    /// </summary>
    private void CopySelectedCells()
    {
        var rows = SelectedRows();

        if (rows.Count == 0)
        {
            return;
        }

        var picked = new HashSet<(RenameRowViewModel, int)>();
        var left = int.MaxValue;
        var right = int.MinValue;

        foreach (var cell in Sheet.SelectedCells)
        {
            if (cell.Item is RenameRowViewModel row && cell.Column is { } column)
            {
                picked.Add((row, column.DisplayIndex));
                left = Math.Min(left, column.DisplayIndex);
                right = Math.Max(right, column.DisplayIndex);
            }
        }

        var builder = new StringBuilder();

        foreach (var row in rows)
        {
            for (var column = left; column <= right; column++)
            {
                if (column > left)
                {
                    builder.Append('\t');
                }

                if (picked.Contains((row, column)))
                {
                    builder.Append(column == 0 ? row.Before : row.After);
                }
            }

            builder.Append("\r\n");
        }

        try
        {
            Clipboard.SetText(builder.ToString());
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            DiagnosticLog.WriteAlways($"칸을 복사하지 못했습니다: {ex.Message}");
            Summary.Text = "복사하지 못했습니다. 다른 프로그램이 클립보드를 쓰고 있을 수 있습니다.";
        }
    }

    /// <summary>머리글을 포함한 표 전체를 복사합니다. 맨 아래 빈 줄은 뺍니다.</summary>
    private void OnCopyTable(object sender, RoutedEventArgs e)
    {
        CommitSheet();

        var builder = new StringBuilder();
        builder.Append("이름 변경 전").Append('\t').Append("이름 변경 후").Append("\r\n");

        foreach (var row in _rows)
        {
            if (row.IsBlank)
            {
                continue;
            }

            builder.Append(row.Before).Append('\t').Append(row.After).Append("\r\n");
        }

        try
        {
            Clipboard.SetText(builder.ToString());
            Summary.Text = "표 전체를 복사했습니다. 엑셀·구글 스프레드시트에 붙여 넣으면 두 열로 들어갑니다.";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            DiagnosticLog.WriteAlways($"표를 복사하지 못했습니다: {ex.Message}");
        }
    }

    /// <summary>
    /// 엑셀·구글 스프레드시트에서 복사한 칸을 붙여 넣습니다(탭으로 나뉜 글).
    /// 지금 칸부터 아래로 채우고, 줄이 모자라면 파일 없는 줄을 늘립니다.
    /// '이름 변경 전' 칸에서 두 열을 붙이면 뒤쪽 열을, '이름 변경 후' 칸에서 붙이면 앞쪽 열을 씁니다.
    /// 값 하나를 여러 칸을 고른 채 붙이면 고른 칸을 모두 그 값으로 채웁니다(엑셀과 같습니다).
    /// </summary>
    private void PasteIntoSheet()
    {
        string text;

        try
        {
            text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            DiagnosticLog.WriteAlways($"붙여넣지 못했습니다: {ex.Message}");
            return;
        }

        if (text.Length == 0)
        {
            return;
        }

        CommitSheet();

        var lines = new List<string>(text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));

        // 엑셀은 복사한 글 끝에 줄바꿈을 하나 붙입니다.
        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count == 0)
        {
            return;
        }

        // 값 하나를 여러 칸에: 고른 '이름 변경 후' 칸을 모두 채웁니다.
        if (lines.Count == 1 && !lines[0].Contains('\t', StringComparison.Ordinal))
        {
            var cells = new List<RenameRowViewModel>();

            foreach (var cell in Sheet.SelectedCells)
            {
                if (cell.Column?.DisplayIndex == 1 && cell.Item is RenameRowViewModel row)
                {
                    cells.Add(row);
                }
            }

            if (cells.Count > 1)
            {
                foreach (var row in cells)
                {
                    row.After = lines[0].Trim();
                }

                EnsureTrailingBlank();
                Recheck();
                Summary.Text = $"{cells.Count}칸을 채웠습니다.";
                return;
            }
        }

        var start = 0;
        var startColumn = 1;

        if (Sheet.CurrentCell.Item is RenameRowViewModel current && _rows.IndexOf(current) is var at and >= 0)
        {
            start = at;
            startColumn = Sheet.CurrentCell.Column?.DisplayIndex ?? 1;
        }
        else if (SelectedRows() is { Count: > 0 } chosen)
        {
            start = _rows.IndexOf(chosen[0]);
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var parts = lines[i].Split('\t');
            var value = startColumn == 0 && parts.Length > 1 ? parts[1] : parts[0];

            while (start + i >= _rows.Count)
            {
                _rows.Add(Hook(new RenameRowViewModel()));
            }

            _rows[start + i].After = value.Trim();
        }

        EnsureTrailingBlank();
        Recheck();
        Summary.Text = $"{lines.Count}줄을 붙여넣었습니다. " + Summary.Text;
    }

    // ── 단축키 ──

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        // 오른쪽 탐색기에 초점이 있으면 그쪽 단축키입니다.
        if (SideExplorer.IsKeyboardFocusWithin)
        {
            return;
        }

        // Alt+↑/↓: 고른 줄 옮기기
        // 숫자 고르기 칸(ComboBox)의 Alt+↓ 는 그대로 두려고, 표나 왼쪽 목록에 초점이 있을 때만 받습니다.
        if (e.Key == Key.System && e.SystemKey is Key.Up or Key.Down && !IsEditingCell
            && (Sheet.IsKeyboardFocusWithin || Targets.IsKeyboardFocusWithin))
        {
            e.Handled = true;

            if (Targets.IsKeyboardFocusWithin)
            {
                MoveTargets(e.SystemKey == Key.Up ? -1 : +1);
            }
            else
            {
                MoveSelected(e.SystemKey == Key.Up ? -1 : +1);
            }

            return;
        }

        var control = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        if (!control)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.N when !shift:
                e.Handled = true;
                OnNormalize(sender, e);
                break;

            case Key.L when !shift:
                e.Handled = true;
                OnLower(sender, e);
                break;

            case Key.U when !shift:
                e.Handled = true;
                OnUpper(sender, e);
                break;

            case Key.D0 when !shift:
                e.Handled = true;
                OnRevert(sender, e);
                break;

            case Key.E when !shift:
                e.Handled = true;
                OnChangeExtension(sender, e);
                break;

            case Key.H when !shift:
                e.Handled = true;
                OnReplace(sender, e);
                break;

            case Key.R when !shift:
                e.Handled = true;
                OnNumber(sender, e);
                break;

            case Key.G when !shift:
                e.Handled = true;
                OnOpenLogs(sender, e);
                break;

            case Key.C when shift:
                e.Handled = true;
                OnCopyTable(sender, e);
                break;

            case Key.V when shift && !IsTypingText:
                e.Handled = true;
                PasteIntoSheet();
                break;

            // 글자 칸이 아닌 데서 누른 Ctrl+V 는 표 붙여넣기입니다. Ctrl+C 는 표가 스스로 처리합니다(고른 칸을 탭으로 나눠 복사).
            case Key.V when !IsTypingText:
                e.Handled = true;
                PasteIntoSheet();
                break;

            // Ctrl+- : 고른 줄을 통째로 뺍니다(엑셀의 행 삭제와 같은 키).
            case Key.OemMinus or Key.Subtract when !IsTypingText:
                e.Handled = true;
                Remove(SelectedRows());
                break;
        }
    }

    // ── 실행 ──

    private void OnRun(object sender, RoutedEventArgs e)
    {
        CommitSheet();
        Recheck();

        var requests = new List<ShellFileOperations.RenameRequest>();
        var pairs = new List<RenamePair>();

        foreach (var row in _rows)
        {
            if (row.FullPath is not { } fullPath)
            {
                continue;
            }

            if (row.HasProblem)
            {
                MessageBox.Show(
                    this,
                    "붉게 칠한 칸이 남아 있습니다. 칸에 마우스를 올려 사유를 보고 고친 뒤에 다시 눌러 주십시오.",
                    "일괄 이름 바꾸기",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                SelectRows(new[] { row }, focus: true);
                return;
            }

            if (!row.Changed)
            {
                continue;
            }

            var after = row.After.Trim();
            requests.Add(new ShellFileOperations.RenameRequest(fullPath, after));
            pairs.Add(new RenamePair(fullPath, Path.Combine(row.Folder, after)));
        }

        if (requests.Count == 0)
        {
            MessageBox.Show(
                this,
                "바뀌는 이름이 없습니다.",
                "일괄 이름 바꾸기",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var owner = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        if (!ShellFileOperations.RenameMany(requests, owner))
        {
            MessageBox.Show(
                this,
                "이름 바꾸기를 마치지 못했습니다. 파일이 열려 있거나 권한이 없을 수 있습니다.",
                "일괄 이름 바꾸기",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        RenamedCount = requests.Count;
        LogPath = RenameLog.Write(pairs);

        var before = new List<string>(pairs.Count);
        var after2 = new List<string>(pairs.Count);

        foreach (var pair in pairs)
        {
            before.Add(pair.Before);
            after2.Add(pair.After);
        }

        _undo.Record(new FileAction(FileActionKind.RenameMany, before, Renamed: after2));

        MessageBox.Show(
            this,
            LogPath is null
                ? $"{RenamedCount}개의 이름을 바꿨습니다. 기록은 남기지 못했습니다."
                : $"{RenamedCount}개의 이름을 바꿨습니다.{Environment.NewLine}{Environment.NewLine}"
                    + $"기록: {LogPath}{Environment.NewLine}"
                    + "Ctrl+Z 로 곧바로 되돌릴 수 있고, 나중에는 기록 창에서 되돌립니다.",
            "일괄 이름 바꾸기",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        DialogResult = true;
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        var dialog = new RenameLogWindow { Owner = this };
        dialog.ShowDialog();
    }
}
