using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Explorer.Shell.Interop;

/// <summary>
/// 최대화했을 때 창이 작업 표시줄을 덮지 않게 합니다.
///
/// 제목 줄을 직접 그리는 창은 윈도우가 "화면 전체"를 최대화 크기로 잡아 줍니다.
/// 그래서 아래쪽이 작업 표시줄 밑으로 들어가 가려집니다.
/// 최대화 크기를 물어 올 때 작업 영역 크기로 고쳐 주면 됩니다.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowMaximize
{
    /// <summary>윈도우가 최대화·최소화 크기를 물어 오는 메시지입니다.</summary>
    public const int WindowMessageGetMinMaxInfo = 0x0024;

    /// <summary>창과 가장 많이 겹치는 화면을 고릅니다.</summary>
    private const int NearestMonitor = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public ScreenPoint Reserved;
        public ScreenPoint MaxSize;
        public ScreenPoint MaxPosition;
        public ScreenPoint MinTrackSize;
        public ScreenPoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public ScreenRect Monitor;
        public ScreenRect Work;
        public int Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, int flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    /// <summary>최대화 크기를 그 화면의 작업 영역에 맞춥니다.</summary>
    /// <param name="window">창 손잡이입니다.</param>
    /// <param name="minMaxInfo">윈도우가 건네준 크기 정보의 주소입니다.</param>
    public static void ClampToWorkArea(IntPtr window, IntPtr minMaxInfo)
    {
        if (window == IntPtr.Zero || minMaxInfo == IntPtr.Zero)
        {
            return;
        }

        var monitor = MonitorFromWindow(window, NearestMonitor);

        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var screen = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };

        if (!GetMonitorInfo(monitor, ref screen))
        {
            return;
        }

        var bounds = Marshal.PtrToStructure<MinMaxInfo>(minMaxInfo);

        // 자리는 화면 왼쪽 위를 0으로 본 값입니다. 작업 표시줄이 위나 왼쪽에 있을 수도 있습니다.
        bounds.MaxPosition.X = screen.Work.Left - screen.Monitor.Left;
        bounds.MaxPosition.Y = screen.Work.Top - screen.Monitor.Top;

        bounds.MaxSize.X = screen.Work.Right - screen.Work.Left;
        bounds.MaxSize.Y = screen.Work.Bottom - screen.Work.Top;

        bounds.MaxTrackSize.X = bounds.MaxSize.X;
        bounds.MaxTrackSize.Y = bounds.MaxSize.Y;

        Marshal.StructureToPtr(bounds, minMaxInfo, fDeleteOld: false);
    }

    /// <summary>
    /// 그 창이 놓인 화면의 작업 영역(작업 표시줄을 뺀 자리)입니다. 물리 픽셀 단위입니다.
    /// 팝업을 부른 창과 같은 화면에 띄우려고 씁니다.
    /// </summary>
    public static bool TryGetWorkArea(IntPtr window, out int left, out int top, out int right, out int bottom)
    {
        left = top = right = bottom = 0;

        if (window == IntPtr.Zero)
        {
            return false;
        }

        var monitor = MonitorFromWindow(window, NearestMonitor);

        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var screen = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };

        if (!GetMonitorInfo(monitor, ref screen))
        {
            return false;
        }

        left = screen.Work.Left;
        top = screen.Work.Top;
        right = screen.Work.Right;
        bottom = screen.Work.Bottom;
        return true;
    }
}
