using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Explorer.Core.Services;

namespace Explorer.Shell.Preview;

/// <summary>휠 한 번의 정보입니다. 좌표는 화면 픽셀입니다.</summary>
public readonly record struct WheelInput(int Delta, bool Control, bool Horizontal, int ScreenX, int ScreenY);

/// <summary>
/// 미리보기 핸들러가 그리는 자리 위에서 굴린 휠을 가로챕니다.
///
/// 핸들러는 다른 프로세스(prevhost.exe)에서 우리 창 안에 자식 창을 붙여 그립니다.
/// 휠은 그 자식 창으로 바로 가므로 WPF 는 받지 못합니다. 그래서 윈도우 전체의 마우스 입력을
/// 저수준 훅으로 먼저 보고, 등록한 창 위에서 굴린 것만 골라 우리 쪽에서 처리합니다.
///
/// 훅은 전용 갈래에서 돕니다. 화면 갈래가 바쁘면 훅 응답이 늦어 윈도우가 훅을 떼어 버리기 때문입니다.
/// 판정 함수는 이 전용 갈래에서 불리므로 짧게 끝나야 하고, 실제 일은 화면 갈래로 넘겨야 합니다.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WheelInterceptor
{
    private const int LowLevelMouseHook = 14;
    private const int MouseWheel = 0x020A;
    private const int MouseHorizontalWheel = 0x020E;
    private const int ControlKey = 0x11;

    private static readonly object Gate = new();
    private static readonly Dictionary<IntPtr, Func<WheelInput, bool>> Targets = new();

    // 훅 함수가 수거되면 윈도우가 사라진 함수를 부르게 됩니다. 붙들어 둡니다.
    private static HookProc? _proc;
    private static Thread? _thread;

    /// <summary>
    /// 그 창(과 그 안의 자식 창) 위에서 굴린 휠을 받습니다.
    /// 판정 함수가 true 를 돌려주면 그 휠은 핸들러에게 가지 않습니다.
    /// </summary>
    public static void Register(IntPtr window, Func<WheelInput, bool> handler)
    {
        if (window == IntPtr.Zero)
        {
            return;
        }

        lock (Gate)
        {
            Targets[window] = handler;
            EnsureHook();
        }
    }

    public static void Unregister(IntPtr window)
    {
        lock (Gate)
        {
            Targets.Remove(window);
        }
    }

    private static void EnsureHook()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "preview-wheel-hook"
        };
        _thread.Start();
    }

    private static void Run()
    {
        _proc = OnMouse;
        var hook = SetWindowsHookEx(LowLevelMouseHook, _proc, GetModuleHandle(null), 0);

        if (hook == IntPtr.Zero)
        {
            DiagnosticLog.Write($"휠 훅을 걸지 못했습니다: {Marshal.GetLastWin32Error()}");
            return;
        }

        // 저수준 훅은 건 갈래가 메시지를 돌려야 불립니다.
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }

        UnhookWindowsHookEx(hook);
    }

    private static IntPtr OnMouse(int code, IntPtr kind, IntPtr data)
    {
        if (code >= 0 && ((int)kind == MouseWheel || (int)kind == MouseHorizontalWheel))
        {
            try
            {
                var info = Marshal.PtrToStructure<MouseHookInfo>(data);
                var handler = FindTarget(info.X, info.Y);

                if (handler is not null)
                {
                    var input = new WheelInput(
                        (short)((info.MouseData >> 16) & 0xFFFF),
                        (GetAsyncKeyState(ControlKey) & 0x8000) != 0,
                        (int)kind == MouseHorizontalWheel,
                        info.X,
                        info.Y);

                    if (handler(input))
                    {
                        // 먹었습니다. 핸들러 창에는 보내지 않습니다.
                        return (IntPtr)1;
                    }
                }
            }
            catch (Exception ex)
            {
                // 여기서 예외가 새면 윈도우 전체의 마우스가 흔들립니다. 적고 넘깁니다.
                DiagnosticLog.Write($"휠 훅 처리 실패: {ex.GetType().Name} {ex.Message}");
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, kind, data);
    }

    private static Func<WheelInput, bool>? FindTarget(int x, int y)
    {
        lock (Gate)
        {
            if (Targets.Count == 0)
            {
                return null;
            }

            var under = WindowFromPoint(new NativePoint { X = x, Y = y });

            if (under == IntPtr.Zero)
            {
                return null;
            }

            foreach (var (window, handler) in Targets)
            {
                if ((under == window || IsChild(window, under)) && IsWindowVisible(window))
                {
                    return handler;
                }
            }

            return null;
        }
    }

    private delegate IntPtr HookProc(int code, IntPtr kind, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookInfo
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int kind, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr kind, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out NativeMessage message, IntPtr window, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parent, IntPtr child);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
}
