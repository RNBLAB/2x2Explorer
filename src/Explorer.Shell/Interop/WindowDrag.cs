using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Explorer.Shell.Interop;

/// <summary>
/// 창을 제목 줄에서 끄는 일을 윈도우에 맡깁니다.
///
/// WPF의 DragMove는 최대화된 창을 끌지 못합니다.
/// 윈도우에 "제목 줄을 눌렀다"고 알려 주면 그다음은 윈도우가 알아서 합니다.
/// 최대화가 저절로 풀리며 창이 커서를 따라오고, 화면 가장자리에 붙이는 것도 그대로 됩니다.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowDrag
{
    private const int WindowMessageCaptionPressed = 0x00A1;

    /// <summary>눌린 자리가 제목 줄이라는 뜻입니다.</summary>
    private const int HitTestCaption = 2;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    /// <summary>창 끌기를 시작합니다.</summary>
    public static void Begin(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return;
        }

        // 우리가 마우스를 쥐고 있으면 윈도우가 끌기를 시작하지 못합니다.
        ReleaseCapture();
        SendMessage(window, WindowMessageCaptionPressed, new IntPtr(HitTestCaption), IntPtr.Zero);
    }
}
