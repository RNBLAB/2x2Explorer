using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Explorer.App.Controls;

/// <summary>
/// 칸 크기가 모두 같은 격자 패널입니다. 화면에 든 줄만 만들어 씁니다.
///
/// WPF 가 주는 WrapPanel 은 가상화를 하지 않아서, 파일이 수천 개인 폴더를
/// 아이콘이나 목록 보기로 열면 줄을 그 수만큼 다 만듭니다. 그게 느림의 원인입니다.
/// 칸 크기가 모두 같으면 몇 번째 항목이 어디에 놓이는지 계산으로 알 수 있으므로,
/// 보이는 구간만 만들어 두고 나머지는 자리만 비워 둘 수 있습니다.
///
/// <see cref="Orientation"/> 이 Horizontal 이면 왼쪽에서 오른쪽으로 채우다 줄을 바꾸고 세로로 굴립니다(아이콘 보기).
/// Vertical 이면 위에서 아래로 채우다 열을 바꾸고 가로로 굴립니다(목록 보기).
/// </summary>
public sealed class UniformVirtualizingPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation),
        typeof(Orientation),
        typeof(UniformVirtualizingPanel),
        new FrameworkPropertyMetadata(Orientation.Horizontal, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth),
        typeof(double),
        typeof(UniformVirtualizingPanel),
        new FrameworkPropertyMetadata(120d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight),
        typeof(double),
        typeof(UniformVirtualizingPanel),
        new FrameworkPropertyMetadata(130d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private Size _extent;
    private Size _viewport;
    private Point _offset;

    /// <summary>한 줄에 들어가는 칸 수입니다(세로 배치에서는 한 열에 들어가는 칸 수).</summary>
    private int _columns = 1;

    private int _rows = 1;

    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    // ── IScrollInfo ──

    public bool CanHorizontallyScroll { get; set; }

    public bool CanVerticallyScroll { get; set; }

    public double ExtentWidth => _extent.Width;

    public double ExtentHeight => _extent.Height;

    public double ViewportWidth => _viewport.Width;

    public double ViewportHeight => _viewport.Height;

    public double HorizontalOffset => _offset.X;

    public double VerticalOffset => _offset.Y;

    public ScrollViewer? ScrollOwner { get; set; }

    private double CellWidth => ItemWidth is > 0 and < 4000 ? ItemWidth : 120;

    private double CellHeight => ItemHeight is > 0 and < 4000 ? ItemHeight : 130;

    private int ItemCount => ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;

    public void LineUp() => SetVerticalOffset(_offset.Y - 16);

    public void LineDown() => SetVerticalOffset(_offset.Y + 16);

    public void LineLeft() => SetHorizontalOffset(_offset.X - 16);

    public void LineRight() => SetHorizontalOffset(_offset.X + 16);

    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);

    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);

    public void PageLeft() => SetHorizontalOffset(_offset.X - _viewport.Width);

    public void PageRight() => SetHorizontalOffset(_offset.X + _viewport.Width);

    /// <summary>
    /// 휠은 방향과 상관없이 이 둘로 들어옵니다.
    /// 그래서 세로로 채우는 목록 보기에서는 휠을 가로 이동으로 바꿔 줍니다. 탐색기와 같습니다.
    /// </summary>
    public void MouseWheelUp() => WheelStep(-1);

    public void MouseWheelDown() => WheelStep(1);

    public void MouseWheelLeft() => SetHorizontalOffset(_offset.X - CellWidth);

    public void MouseWheelRight() => SetHorizontalOffset(_offset.X + CellWidth);

    private void WheelStep(int direction)
    {
        if (Orientation == Orientation.Horizontal)
        {
            SetVerticalOffset(_offset.Y + (direction * CellHeight));
        }
        else
        {
            SetHorizontalOffset(_offset.X + (direction * CellWidth));
        }
    }

    public void SetHorizontalOffset(double offset)
    {
        var wanted = Clamp(offset, _extent.Width - _viewport.Width);

        if (Math.Abs(wanted - _offset.X) < 0.5)
        {
            return;
        }

        _offset.X = wanted;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public void SetVerticalOffset(double offset)
    {
        var wanted = Clamp(offset, _extent.Height - _viewport.Height);

        if (Math.Abs(wanted - _offset.Y) < 0.5)
        {
            return;
        }

        _offset.Y = wanted;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    private static double Clamp(double offset, double limit) =>
        double.IsNaN(offset) || offset < 0 || limit <= 0 ? 0 : Math.Min(offset, limit);

    /// <summary>글쇠로 옮겨 다닐 때 화면 밖으로 나간 줄을 다시 끌어 옵니다.</summary>
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var child = ChildOf(visual);

        if (child is null)
        {
            return rectangle;
        }

        var index = ItemContainerGenerator.IndexFromGeneratorPosition(
            new GeneratorPosition(InternalChildren.IndexOf(child), 0));

        if (index < 0)
        {
            return rectangle;
        }

        var bounds = CellBounds(index);
        ScrollTo(bounds);

        return new Rect(bounds.X - _offset.X, bounds.Y - _offset.Y, bounds.Width, bounds.Height);
    }

    /// <summary>아직 만들지 않은 줄로 옮겨 갈 때 그 자리까지 굴립니다.</summary>
    protected override void BringIndexIntoView(int index)
    {
        if (index < 0 || index >= ItemCount)
        {
            return;
        }

        ScrollTo(CellBounds(index));
        UpdateLayout();
    }

    private void ScrollTo(Rect bounds)
    {
        if (Orientation == Orientation.Horizontal)
        {
            if (bounds.Top < _offset.Y)
            {
                SetVerticalOffset(bounds.Top);
            }
            else if (bounds.Bottom > _offset.Y + _viewport.Height)
            {
                SetVerticalOffset(bounds.Bottom - _viewport.Height);
            }

            return;
        }

        if (bounds.Left < _offset.X)
        {
            SetHorizontalOffset(bounds.Left);
        }
        else if (bounds.Right > _offset.X + _viewport.Width)
        {
            SetHorizontalOffset(bounds.Right - _viewport.Width);
        }
    }

    private UIElement? ChildOf(Visual? visual)
    {
        DependencyObject? node = visual;

        while (node is not null && !ReferenceEquals(node, this))
        {
            if (node is UIElement element && InternalChildren.Contains(element))
            {
                return element;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    // ── 배치 ──

    protected override Size MeasureOverride(Size availableSize)
    {
        // 이 한 줄이 항목 생성기를 깨웁니다. 그 전에는 GeneratorPosition 을 물을 수 없습니다.
        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        var count = ItemCount;

        var cellWidth = CellWidth;
        var cellHeight = CellHeight;

        var viewport = new Size(
            double.IsInfinity(availableSize.Width) || availableSize.Width <= 0 ? cellWidth : availableSize.Width,
            double.IsInfinity(availableSize.Height) || availableSize.Height <= 0 ? cellHeight : availableSize.Height);

        Size extent;

        if (Orientation == Orientation.Horizontal)
        {
            _columns = Math.Max(1, (int)(viewport.Width / cellWidth));
            _rows = count == 0 ? 0 : ((count + _columns - 1) / _columns);
            extent = new Size(viewport.Width, _rows * cellHeight);
        }
        else
        {
            _rows = Math.Max(1, (int)(viewport.Height / cellHeight));
            _columns = count == 0 ? 0 : ((count + _rows - 1) / _rows);
            extent = new Size(_columns * cellWidth, viewport.Height);
        }

        UpdateScrollInfo(extent, viewport);

        var first = 0;
        var last = -1;

        if (count > 0)
        {
            if (Orientation == Orientation.Horizontal)
            {
                var firstRow = Math.Max(0, (int)(_offset.Y / cellHeight));
                var lastRow = (int)((_offset.Y + viewport.Height) / cellHeight);
                first = firstRow * _columns;
                last = Math.Min(count - 1, ((lastRow + 1) * _columns) - 1);
            }
            else
            {
                var firstColumn = Math.Max(0, (int)(_offset.X / cellWidth));
                var lastColumn = (int)((_offset.X + viewport.Width) / cellWidth);
                first = firstColumn * _rows;
                last = Math.Min(count - 1, ((lastColumn + 1) * _rows) - 1);
            }

            first = Math.Max(0, Math.Min(first, count - 1));
        }

        if (last >= first)
        {
            var start = generator.GeneratorPositionFromIndex(first);
            var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;

            using (generator.StartAt(start, GeneratorDirection.Forward, true))
            {
                for (var index = first; index <= last; index++, childIndex++)
                {
                    if (generator.GenerateNext(out var isNew) is not UIElement child)
                    {
                        break;
                    }

                    if (isNew)
                    {
                        if (childIndex >= children.Count)
                        {
                            AddInternalChild(child);
                        }
                        else
                        {
                            InsertInternalChild(childIndex, child);
                        }

                        generator.PrepareItemContainer(child);
                    }

                    child.Measure(new Size(cellWidth, cellHeight));
                }
            }
        }

        DropOutOfSight(first, last);

        return new Size(
            double.IsInfinity(availableSize.Width) ? extent.Width : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? extent.Height : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator;

        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            var index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));

            if (index < 0)
            {
                continue;
            }

            var bounds = CellBounds(index);
            child.Arrange(new Rect(bounds.X - _offset.X, bounds.Y - _offset.Y, bounds.Width, bounds.Height));
        }

        return finalSize;
    }

    /// <summary>
    /// 보이는 칸 기준으로 그린 네모에 걸치는 항목들의 차례를 돌려줍니다.
    /// 범위 지정 선택이 화면 밖으로 굴러 나간 줄까지 잡으려면 이 계산이 있어야 합니다.
    /// 줄을 만들어 두지 않아도 몇 번째가 어디에 놓이는지 알 수 있는 것이 이 패널의 이점입니다.
    /// </summary>
    public IReadOnlyList<int> IndicesIn(Rect area)
    {
        var count = ItemCount;

        if (count == 0 || area.IsEmpty || area.Width <= 0 || area.Height <= 0)
        {
            return Array.Empty<int>();
        }

        // 보이는 칸 기준을 전체 기준으로 옮깁니다.
        var left = area.Left + _offset.X;
        var top = area.Top + _offset.Y;
        var right = area.Right + _offset.X;
        var bottom = area.Bottom + _offset.Y;

        var cellWidth = CellWidth;
        var cellHeight = CellHeight;

        var firstColumn = Math.Max(0, (int)(left / cellWidth));
        var lastColumn = Math.Max(0, (int)((right - 0.01) / cellWidth));
        var firstRow = Math.Max(0, (int)(top / cellHeight));
        var lastRow = Math.Max(0, (int)((bottom - 0.01) / cellHeight));

        var found = new List<int>();

        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = firstColumn; column <= lastColumn; column++)
            {
                var index = Orientation == Orientation.Horizontal
                    ? (row * Math.Max(1, _columns)) + column
                    : (column * Math.Max(1, _rows)) + row;

                if (index >= 0 && index < count)
                {
                    found.Add(index);
                }
            }
        }

        return found;
    }

    /// <summary>몇 번째 항목이 어디에 놓이는지입니다. 굴린 양은 빼기 전의 자리입니다.</summary>
    private Rect CellBounds(int index)
    {
        var cellWidth = CellWidth;
        var cellHeight = CellHeight;

        if (Orientation == Orientation.Horizontal)
        {
            var columns = Math.Max(1, _columns);
            return new Rect(index % columns * cellWidth, index / columns * cellHeight, cellWidth, cellHeight);
        }

        var rows = Math.Max(1, _rows);
        return new Rect(index / rows * cellWidth, index % rows * cellHeight, cellWidth, cellHeight);
    }

    /// <summary>화면 밖으로 나간 줄은 버립니다. 이게 가상화의 본체입니다.</summary>
    private void DropOutOfSight(int first, int last)
    {
        var generator = ItemContainerGenerator;

        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            var index = generator.IndexFromGeneratorPosition(position);

            if (index >= first && index <= last)
            {
                continue;
            }

            generator.Remove(position, 1);
            RemoveInternalChildRange(i, 1);
        }
    }

    private void UpdateScrollInfo(Size extent, Size viewport)
    {
        var changed = extent != _extent || viewport != _viewport;

        _extent = extent;
        _viewport = viewport;

        var x = Clamp(_offset.X, extent.Width - viewport.Width);
        var y = Clamp(_offset.Y, extent.Height - viewport.Height);

        if (Math.Abs(x - _offset.X) > 0.5 || Math.Abs(y - _offset.Y) > 0.5)
        {
            _offset = new Point(x, y);
            changed = true;
        }

        if (changed)
        {
            ScrollOwner?.InvalidateScrollInfo();
        }
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;

            case NotifyCollectionChangedAction.Reset:
                RemoveInternalChildRange(0, InternalChildren.Count);
                _offset = new Point(0, 0);
                break;
        }

        InvalidateMeasure();
    }
}
