using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Explorer.Shell.Interop;

namespace Explorer.Shell.Operations;

/// <summary>파일을 연결 프로그램으로 열거나 속성 창을 띄웁니다.</summary>
[SupportedOSPlatform("windows")]
public static class ShellLauncher
{
    /// <summary>윈도우에 등록된 연결 프로그램으로 엽니다. 실패하면 false입니다.</summary>
    public static bool Open(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path)
            {
                // 이 값이 false면 실행 파일만 열 수 있습니다. 연결 프로그램을 쓰려면 셸에 맡겨야 합니다.
                UseShellExecute = true
            });

            return true;
        }
        catch (Exception ex) when (ex is Win32Exception
                                       or FileNotFoundException
                                       or InvalidOperationException
                                       or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>폴더를 윈도우 탐색기 새 창으로 엽니다. 실패하면 false입니다.</summary>
    public static bool OpenInExplorer(string folder)
    {
        try
        {
            // 경로를 따옴표로 감싸야 공백이 든 폴더가 쪼개지지 않습니다.
            using var process = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"")
            {
                UseShellExecute = true
            });

            return true;
        }
        catch (Exception ex) when (ex is Win32Exception
                                       or FileNotFoundException
                                       or InvalidOperationException
                                       or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>윈도우 탐색기의 "속성" 창을 그대로 띄웁니다.</summary>
    public static bool ShowProperties(string path, IntPtr owner)
    {
        var info = new ShellNative.ShellExecuteInfo
        {
            Size = Marshal.SizeOf<ShellNative.ShellExecuteInfo>(),
            Mask = ShellNative.SEE_MASK_INVOKEIDLIST,
            Owner = owner,
            Verb = "properties",
            File = path,
            Show = ShellNative.SW_SHOW
        };

        try
        {
            return ShellNative.ShellExecuteEx(ref info);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            return false;
        }
    }
}
