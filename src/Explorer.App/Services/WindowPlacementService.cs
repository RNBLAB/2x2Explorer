using System;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;
using Explorer.Core.Settings;
using Explorer.Shell.Interop;

namespace Explorer.App.Services;

/// <summary>
/// 창의 위치와 크기를 설정에 넣고 빼는 일을 맡습니다.
/// 지난번에 쓰던 모니터를 빼 버린 경우처럼 좌표가 화면 밖일 때는 가운데로 되돌립니다.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowPlacementService
{
    private const double MinimumVisibleWidth = 120;
    private const double MinimumVisibleHeight = 40;

    public static void Restore(Window window, WindowSettings settings)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(settings);

        window.Width = Clamp(settings.Width, 800, SystemParameters.VirtualScreenWidth);
        window.Height = Clamp(settings.Height, 600, SystemParameters.VirtualScreenHeight);

        if (settings.Left is { } left && settings.Top is { } top
            && IsReachableOnScreen(left, top, window.Width, window.Height))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = left;
            window.Top = top;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        // 최대화는 여기서 걸지 않습니다.
        // 창 손잡이가 아직 없어서, 지금 최대화하면 지난번 모니터가 아니라 주 모니터에서 커집니다.
        window.WindowState = WindowState.Normal;
    }

    /// <summary>
    /// 창 손잡이가 만들어진 뒤에 불러 지난번 자리를 그대로 되돌립니다.
    /// 배율이 다른 모니터가 섞여 있어도, 최대화한 채로 껐어도 그 모니터에서 되살아납니다.
    /// </summary>
    public static void ApplySavedPlacement(Window window, WindowSettings settings)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(settings);

        var handle = new WindowInteropHelper(window).Handle;

        if (handle == IntPtr.Zero)
        {
            return;
        }

        if (settings.PlacementShow is { } show
            && settings.PlacementLeft is { } left
            && settings.PlacementTop is { } top
            && settings.PlacementRight is { } right
            && settings.PlacementBottom is { } bottom
            && WindowPlacement.Apply(handle, show, left, top, right, bottom))
        {
            return;
        }

        // 윈도우가 주는 자리가 아직 없는 옛 설정입니다. 최대화 여부만 살립니다.
        if (settings.IsMaximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }

    public static void Capture(Window window, WindowSettings settings)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(settings);

        settings.IsMaximized = window.WindowState == WindowState.Maximized;

        // 최대화 상태에서는 화면 전체 크기가 아니라 최대화 전 크기를 저장해야 합니다.
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;

        if (bounds.Width <= 0 || bounds.Height <= 0
            || double.IsNaN(bounds.Left) || double.IsNaN(bounds.Top))
        {
            return;
        }

        settings.Left = bounds.Left;
        settings.Top = bounds.Top;
        settings.Width = bounds.Width;
        settings.Height = bounds.Height;

        StoreNativePlacement(window, settings);
    }

    /// <summary>윈도우가 알려 주는 자리를 그대로 적어 둡니다. 되살릴 때 이것을 먼저 씁니다.</summary>
    private static void StoreNativePlacement(Window window, WindowSettings settings)
    {
        var handle = new WindowInteropHelper(window).Handle;

        if (WindowPlacement.TryRead(handle, out var show, out var left, out var top, out var right, out var bottom))
        {
            settings.PlacementShow = show;
            settings.PlacementLeft = left;
            settings.PlacementTop = top;
            settings.PlacementRight = right;
            settings.PlacementBottom = bottom;
        }
    }

    private static bool IsReachableOnScreen(double left, double top, double width, double height)
    {
        var desktop = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        var candidate = new Rect(left, top, width, height);
        candidate.Intersect(desktop);

        // 제목 표시줄을 잡을 수 있을 만큼은 보여야 합니다.
        return candidate.Width >= MinimumVisibleWidth && candidate.Height >= MinimumVisibleHeight;
    }

    private static double Clamp(double value, double minimum, double maximum)
    {
        if (double.IsNaN(value) || value < minimum)
        {
            return minimum;
        }

        return maximum > minimum && value > maximum ? maximum : value;
    }
}
