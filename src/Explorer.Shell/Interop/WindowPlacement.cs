using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Explorer.Shell.Interop;

/// <summary>
/// 창의 자리를 윈도우가 쓰는 방식 그대로 읽고 씁니다.
///
/// WPF의 Left·Top은 주 모니터의 배율을 기준으로 한 값이라,
/// 배율이 다른 모니터가 섞여 있으면 어긋납니다.
/// 윈도우가 주는 자리는 화면 전체를 하나로 본 실제 픽셀이라 그런 일이 없습니다.
/// 최대화한 채로 껐다가 그 모니터에서 최대화된 채로 다시 뜨는 것도 여기서 따라옵니다.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowPlacement
{
    /// <summary>보통 크기로 보입니다.</summary>
    public const int ShowNormal = 1;

    /// <summary>작업 표시줄에 내려가 있습니다.</summary>
    public const int ShowMinimized = 2;

    /// <summary>최대화되어 있습니다.</summary>
    public const int ShowMaximized = 3;

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
    private struct PlacementData
    {
        public int Length;
        public int Flags;
        public int ShowCommand;
        public ScreenPoint MinimizedPosition;
        public ScreenPoint MaximizedPosition;
        public ScreenRect NormalPosition;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr window, ref PlacementData placement);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(IntPtr window, ref PlacementData placement);

    /// <summary>지금 창 자리를 읽습니다.</summary>
    public static bool TryRead(
        IntPtr window,
        out int showCommand,
        out int left,
        out int top,
        out int right,
        out int bottom)
    {
        showCommand = ShowNormal;
        left = 0;
        top = 0;
        right = 0;
        bottom = 0;

        if (window == IntPtr.Zero)
        {
            return false;
        }

        var placement = new PlacementData { Length = Marshal.SizeOf<PlacementData>() };

        if (!GetWindowPlacement(window, ref placement))
        {
            return false;
        }

        showCommand = placement.ShowCommand;
        left = placement.NormalPosition.Left;
        top = placement.NormalPosition.Top;
        right = placement.NormalPosition.Right;
        bottom = placement.NormalPosition.Bottom;

        return right > left && bottom > top;
    }

    /// <summary>적어 둔 자리로 창을 되돌립니다. 창 손잡이가 만들어진 뒤에 불러야 합니다.</summary>
    public static bool Apply(IntPtr window, int showCommand, int left, int top, int right, int bottom)
    {
        if (window == IntPtr.Zero || right <= left || bottom <= top)
        {
            return false;
        }

        var placement = new PlacementData
        {
            Length = Marshal.SizeOf<PlacementData>(),
            Flags = 0,

            // 내려가 있던 상태로 되살리지는 않습니다.
            ShowCommand = showCommand == ShowMinimized ? ShowNormal : showCommand,
            NormalPosition = new ScreenRect
            {
                Left = left,
                Top = top,
                Right = right,
                Bottom = bottom
            }
        };

        return SetWindowPlacement(window, ref placement);
    }
}
