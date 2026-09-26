using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Explorer.App.ViewModels;
using Explorer.Core.Services;

namespace Explorer.App.Views.Dialogs;

/// <summary>
/// 이름 정규화 창입니다. ① 현재 패턴 분석 → ② 목표 패턴 설정 → ③ 적용 세 단계입니다.
///
/// ① 이름마다 요소(순번·날짜·문서번호·프로젝트명·본문·버전·작성자·비고)로 나눈 표를 보입니다.
///    판별 순위는 문서번호 → 순번 → 버전 → 날짜입니다(<see cref="NamePattern"/>). 짐작한 칸은 흐리게 보이고, 칸을 고쳐 바로잡습니다.
/// ② 요소 차례(끌기·◀▶)와 켜고 끄기, 구분자와 요소별 형식을 고릅니다. 바꿀 때마다 결과 열이 곧바로 바뀝니다.
/// ③ 결과를 일괄 이름 바꾸기 표의 '이름 변경 후' 칸에 채웁니다. 파일 이름은 이 창에서 바꾸지 않습니다.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class NormalizeWindow : Window
{
    /// <summary>목표 패턴의 요소 칩 하나입니다.</summary>
    public sealed class OrderItem : ObservableObject
    {
        private bool _isOn;

        public OrderItem(NamePart part, bool isOn)
        {
            Part = part;
            _isOn = isOn;
        }

        public NamePart Part { get; }

        public string Label => NamePattern.Label(Part);

        public bool IsOn
        {
            get => _isOn;
            set => SetProperty(ref _isOn, value);
        }
    }

    private const string OrderDragFormat = "Explorer.NameOrder";

    private readonly IReadOnlyList<RenameRowViewModel> _targets;
    private readonly ObservableCollection<NormalizeRowViewModel> _rows = new();
    private readonly ObservableCollection<OrderItem> _order = new();
    private bool _ready;

    private Point _orderDragStart;
    private OrderItem? _orderPressed;
    private OrderItem? _orderDragged;

    public NormalizeWindow(IReadOnlyList<RenameRowViewModel> targets)
    {
        _targets = targets;

        InitializeComponent();
        PopupTitleBar.Apply(this);

        Parts.ItemsSource = _rows;
        OrderList.ItemsSource = _order;

        var template = new NameTemplate();

        foreach (var part in template.Order)
        {
            var item = new OrderItem(part, template.Enabled.Contains(part));
            item.PropertyChanged += (_, _) => Recompose();
            _order.Add(item);
        }

        // Ctrl+C 는 직접 복사합니다(엑셀 모양, 클립보드 예외 처리).
        Parts.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, e) =>
        {
            CopyCells();
            e.Handled = true;
        }));

        _ready = true;
        Analyze();
    }

    // ── ① 분석 ──

    private void OnReanalyze(object sender, RoutedEventArgs e) => Analyze();

    private void Analyze()
    {
        Parts.CommitEdit(DataGridEditingUnit.Row, true);

        var names = new List<string>(_targets.Count);
        var folders = new List<bool>(_targets.Count);

        foreach (var row in _targets)
        {
            names.Add(row.Before);
            folders.Add(row.FullPath is { } path && Directory.Exists(path));
        }

        var parsed = NamePattern.Analyze(names, folders);

        foreach (var row in _rows)
        {
            row.PropertyChanged -= OnPartChanged;
        }

        _rows.Clear();

        for (var i = 0; i < parsed.Count; i++)
        {
            var row = new NormalizeRowViewModel(_targets[i], parsed[i]);
            row.PropertyChanged += OnPartChanged;
            _rows.Add(row);
        }

        AnalysisText.Text = Describe(parsed);
        Recompose();
    }

    /// <summary>분석 결과를 한 줄로 요약합니다. 요소별로 몇 개에서 찾았는지, 날짜는 어떤 형식이 많은지입니다.</summary>
    private static string Describe(IReadOnlyList<NameParts> parsed)
    {
        var total = parsed.Count;

        if (total == 0)
        {
            return "분석할 이름이 없습니다.";
        }

        int Count(Func<NameParts, bool> has) => parsed.Count(has);

        var builder = new StringBuilder();
        builder.Append($"{total}개 이름 — ");
        builder.Append($"순번 {Count(p => p.Sequence.Length > 0)} · ");
        builder.Append($"날짜 {Count(p => p.Date.Length > 0)} · ");
        builder.Append($"문서번호 {Count(p => p.DocNumber.Length > 0)} · ");
        builder.Append($"프로젝트명 {Count(p => p.Project.Length > 0)}(짐작 {Count(p => p.ProjectGuessed && p.Project.Length > 0)}) · ");
        builder.Append($"버전 {Count(p => p.Version.Length > 0)} · ");
        builder.Append($"작성자 {Count(p => p.Author.Length > 0)}(짐작) · ");
        builder.Append($"비고 {Count(p => p.Note.Length > 0)}");

        var unresolved = Count(p => p.Sequence.Length == 0 && p.Date.Length == 0 && p.DocNumber.Length == 0
            && p.Project.Length == 0 && p.Version.Length == 0 && p.Author.Length == 0);

        if (unresolved > 0)
        {
            builder.Append($".  요소를 못 찾은 이름 {unresolved}개(원래 이름이 주황색)");
        }

        builder.Append(".  흐린 기울임 칸은 짐작한 값이니 확인하십시오. 날짜가 없는 이름은 날짜를 비워 둡니다.");
        return builder.ToString();
    }

    private void OnPartChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NormalizeRowViewModel.Result) && sender is NormalizeRowViewModel row)
        {
            row.Result = NamePattern.Compose(row.Parts, CurrentTemplate());
            UpdateSummary();
        }
    }

    // ── ② 목표 패턴 ──

    private NameTemplate CurrentTemplate()
    {
        static string Tag(ComboBox box, string fallback) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

        var template = new NameTemplate
        {
            Order = _order.Select(item => item.Part).ToList(),
            Enabled = new HashSet<NamePart>(_order.Where(item => item.IsOn).Select(item => item.Part)),
            Separator = Tag(SeparatorBox, "_"),
            DateFormat = Tag(DateFormatBox, "yyMMdd"),
            VersionPrefix = Tag(VersionBox, "v"),
            SequenceDigits = int.TryParse(Tag(SequenceBox, "0"), out var digits) ? digits : 0,
            KeepMarker = MarkerCheck.IsChecked == true,
            NoteInParentheses = NoteParenCheck.IsChecked == true,
            LowerExtension = LowerExtCheck.IsChecked == true
        };

        return template;
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e) => Recompose();

    /// <summary>모든 줄의 결과를 다시 조립합니다.</summary>
    private void Recompose()
    {
        if (!_ready)
        {
            return;
        }

        var template = CurrentTemplate();

        foreach (var row in _rows)
        {
            row.Result = NamePattern.Compose(row.Parts, template);
        }

        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var changed = _rows.Count(row => !string.Equals(row.Result, row.Target.After, StringComparison.Ordinal));
        Summary.Text = $"{_rows.Count}개 가운데 {changed}개의 '이름 변경 후'가 바뀝니다.";
    }

    private void OnOrderLeft(object sender, RoutedEventArgs e) => MoveOrder(-1);

    private void OnOrderRight(object sender, RoutedEventArgs e) => MoveOrder(+1);

    private void MoveOrder(int step)
    {
        if (OrderList.SelectedItem is not OrderItem item)
        {
            return;
        }

        var at = _order.IndexOf(item);
        var to = at + step;

        if (to < 0 || to >= _order.Count)
        {
            return;
        }

        _order.Move(at, to);
        OrderList.SelectedItem = item;
        Recompose();
    }

    private void OnOrderMouseDown(object sender, MouseButtonEventArgs e)
    {
        _orderDragStart = e.GetPosition(OrderList);
        _orderPressed = e.OriginalSource is DependencyObject source
                        && ItemsControl.ContainerFromElement(OrderList, source) is ListBoxItem { DataContext: OrderItem item }
            ? item
            : null;
    }

    private void OnOrderMouseMove(object sender, MouseEventArgs e)
    {
        if (_orderPressed is not { } item || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var moved = e.GetPosition(OrderList) - _orderDragStart;

        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _orderPressed = null;
        _orderDragged = item;
        OrderList.SelectedItem = item;

        try
        {
            DragDrop.DoDragDrop(OrderList, new DataObject(OrderDragFormat, OrderDragFormat), DragDropEffects.Move);
        }
        finally
        {
            _orderDragged = null;
            OrderDropLine.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>마우스 자리로 끼워 넣을 차례와 표시선의 가로 위치를 셉니다. 칩의 오른쪽 반이면 그 뒤로 봅니다.</summary>
    private int OrderDropIndexAt(DragEventArgs e, out double lineX)
    {
        var overlay = (UIElement)OrderDropLine.Parent;
        var hit = OrderList.InputHitTest(e.GetPosition(OrderList)) as DependencyObject;

        if (hit is not null && ItemsControl.ContainerFromElement(OrderList, hit) is ListBoxItem { DataContext: OrderItem item } chip)
        {
            var after = e.GetPosition(chip).X > chip.ActualWidth / 2;
            lineX = chip.TranslatePoint(new Point(after ? chip.ActualWidth - 4 : -3, 0), overlay).X;
            return _order.IndexOf(item) + (after ? 1 : 0);
        }

        var last = _order.Count > 0 ? OrderList.ItemContainerGenerator.ContainerFromItem(_order[^1]) as ListBoxItem : null;
        lineX = last?.TranslatePoint(new Point(last.ActualWidth - 4, 0), overlay).X ?? 0;
        return _order.Count;
    }

    private void OnOrderDragOver(object sender, DragEventArgs e)
    {
        if (_orderDragged is null || !e.Data.GetDataPresent(OrderDragFormat))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        OrderDropIndexAt(e, out var x);
        OrderDropLine.Margin = new Thickness(Math.Max(0, x), 0, 0, 0);
        OrderDropLine.Visibility = Visibility.Visible;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnOrderDragLeave(object sender, DragEventArgs e) => OrderDropLine.Visibility = Visibility.Collapsed;

    private void OnOrderDrop(object sender, DragEventArgs e)
    {
        OrderDropLine.Visibility = Visibility.Collapsed;
        e.Handled = true;

        if (_orderDragged is not { } item)
        {
            return;
        }

        var insertAt = OrderDropIndexAt(e, out _);
        var from = _order.IndexOf(item);
        var to = insertAt > from ? insertAt - 1 : insertAt;

        if (to != from && to >= 0 && to < _order.Count)
        {
            _order.Move(from, to);
        }

        OrderList.SelectedItem = item;
        Recompose();
    }

    // ── 요소 표: 채우기·복사·붙여넣기 ──

    /// <summary>칸의 열이 어느 요소인지입니다. 원래 이름·결과 열이면 null 입니다.</summary>
    private static NamePart? PartOf(DataGridColumn? column) =>
        column?.SortMemberPath switch
        {
            nameof(NormalizeRowViewModel.Sequence) => NamePart.Sequence,
            nameof(NormalizeRowViewModel.Date) => NamePart.Date,
            nameof(NormalizeRowViewModel.DocNumber) => NamePart.DocNumber,
            nameof(NormalizeRowViewModel.Project) => NamePart.Project,
            nameof(NormalizeRowViewModel.Body) => NamePart.Body,
            nameof(NormalizeRowViewModel.Version) => NamePart.Version,
            nameof(NormalizeRowViewModel.Author) => NamePart.Author,
            nameof(NormalizeRowViewModel.Note) => NamePart.Note,
            _ => null
        };

    private static string ValueOf(NormalizeRowViewModel row, DataGridColumn column) =>
        column.SortMemberPath switch
        {
            nameof(NormalizeRowViewModel.Original) => row.Original,
            nameof(NormalizeRowViewModel.Result) => row.Result,
            _ => PartOf(column) is { } part ? row[part] : string.Empty
        };

    private bool IsEditingCell => Parts.IsKeyboardFocusWithin && Keyboard.FocusedElement is TextBox;

    private void OnFillColumn(object sender, RoutedEventArgs e) => FillColumn();

    /// <summary>지금 칸의 값을 같은 열의 고른 줄에 채웁니다. 고른 칸이 그 열에 하나뿐이면 모든 줄에 채웁니다.</summary>
    private void FillColumn()
    {
        Parts.CommitEdit(DataGridEditingUnit.Row, true);

        if (Parts.CurrentCell.Item is not NormalizeRowViewModel source || PartOf(Parts.CurrentCell.Column) is not { } part)
        {
            Summary.Text = "채울 값이 든 칸(요소 열)을 먼저 고르십시오.";
            return;
        }

        var value = source[part];
        var rows = Parts.SelectedCells
            .Where(cell => PartOf(cell.Column) == part && cell.Item is NormalizeRowViewModel)
            .Select(cell => (NormalizeRowViewModel)cell.Item)
            .Distinct()
            .ToList();

        if (rows.Count <= 1)
        {
            rows = _rows.ToList();
        }

        foreach (var row in rows)
        {
            row[part] = value;
        }

        Summary.Text = $"{NamePattern.Label(part)} 열 {rows.Count}칸을 '{value}'(으)로 채웠습니다.";
    }

    private void OnPartsKeyDown(object sender, KeyEventArgs e)
    {
        if (IsEditingCell)
        {
            return;
        }

        if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;

            foreach (var cell in Parts.SelectedCells)
            {
                if (cell.Item is NormalizeRowViewModel row && PartOf(cell.Column) is { } part)
                {
                    row[part] = string.Empty;
                }
            }
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || Keyboard.FocusedElement is TextBox)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.D:
                e.Handled = true;
                FillColumn();
                break;

            case Key.V:
                e.Handled = true;
                PasteCells();
                break;
        }
    }

    private void CopyCells()
    {
        var cells = Parts.SelectedCells.Where(cell => cell.Item is NormalizeRowViewModel && cell.Column is not null).ToList();

        if (cells.Count == 0)
        {
            return;
        }

        var rows = cells.Select(cell => (NormalizeRowViewModel)cell.Item).Distinct()
            .OrderBy(row => _rows.IndexOf(row)).ToList();
        var left = cells.Min(cell => cell.Column.DisplayIndex);
        var right = cells.Max(cell => cell.Column.DisplayIndex);
        var picked = new HashSet<(NormalizeRowViewModel, int)>(cells.Select(cell => ((NormalizeRowViewModel)cell.Item, cell.Column.DisplayIndex)));
        var columns = Parts.Columns.OrderBy(column => column.DisplayIndex).ToList();
        var builder = new StringBuilder();

        foreach (var row in rows)
        {
            for (var index = left; index <= right; index++)
            {
                if (index > left)
                {
                    builder.Append('\t');
                }

                if (picked.Contains((row, index)))
                {
                    builder.Append(ValueOf(row, columns[index]));
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
        }
    }

    /// <summary>
    /// 엑셀에서 복사한 칸을 지금 칸부터 오른쪽·아래로 채웁니다. 요소 열이 아닌 칸(원래 이름·결과)은 건너뜁니다.
    /// 값 하나를 여러 칸을 고른 채 붙이면 고른 칸을 모두 채웁니다.
    /// </summary>
    private void PasteCells()
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

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count == 0)
        {
            return;
        }

        Parts.CommitEdit(DataGridEditingUnit.Row, true);

        if (lines.Count == 1 && !lines[0].Contains('\t', StringComparison.Ordinal) && Parts.SelectedCells.Count > 1)
        {
            foreach (var cell in Parts.SelectedCells)
            {
                if (cell.Item is NormalizeRowViewModel row && PartOf(cell.Column) is { } part)
                {
                    row[part] = lines[0];
                }
            }

            return;
        }

        if (Parts.CurrentCell.Item is not NormalizeRowViewModel start || Parts.CurrentCell.Column is null)
        {
            return;
        }

        var columns = Parts.Columns.OrderBy(column => column.DisplayIndex).ToList();
        var firstRow = _rows.IndexOf(start);
        var firstColumn = Parts.CurrentCell.Column.DisplayIndex;

        for (var i = 0; i < lines.Count && firstRow + i < _rows.Count; i++)
        {
            var values = lines[i].Split('\t');

            for (var j = 0; j < values.Length && firstColumn + j < columns.Count; j++)
            {
                if (PartOf(columns[firstColumn + j]) is { } part)
                {
                    _rows[firstRow + i][part] = values[j];
                }
            }
        }
    }

    // ── ③ 적용 ──

    private void OnApply(object sender, RoutedEventArgs e)
    {
        Parts.CommitEdit(DataGridEditingUnit.Row, true);
        Recompose();

        foreach (var row in _rows)
        {
            row.Target.After = row.Result;
        }

        DialogResult = true;
    }
}
