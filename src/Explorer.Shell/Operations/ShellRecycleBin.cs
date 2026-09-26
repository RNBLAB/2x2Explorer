using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Explorer.Core.Services;
using Explorer.Shell.Interop;

namespace Explorer.Shell.Operations;

/// <summary>
/// 휴지통에 든 항목을 제자리로 돌려놓습니다.
///
/// 휴지통의 "복원"은 셸이 쥐고 있는 기능입니다. 파일을 옮기기만 해서는 안 됩니다.
/// 원래 자리를 적어 둔 짝 파일이 휴지통에 남아 유령 항목이 되기 때문입니다.
/// 그래서 그 항목의 메뉴에 있는 undelete 를 그대로 부릅니다.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ShellRecycleBin
{
    /// <param name="recycled">휴지통에 들어갈 때 받아 적어 둔 자리들입니다.</param>
    /// <param name="original">지우기 전의 자리들입니다. 차례가 <paramref name="recycled"/>와 같아야 합니다.</param>
    /// <returns>제자리로 돌려놓은 개수입니다.</returns>
    public static int Restore(IReadOnlyList<string> recycled, IReadOnlyList<string> original, IntPtr owner)
    {
        if (recycled.Count == 0)
        {
            return 0;
        }

        var restored = 0;

        for (var i = 0; i < recycled.Count; i++)
        {
            var back = i < original.Count ? original[i] : null;

            if (RestoreOne(recycled[i], back, owner))
            {
                restored++;
            }
        }

        return restored;
    }

    /// <summary>
    /// 휴지통 안의 그 자리를 원래 자리로 되돌립니다.
    ///
    /// 셸이 알려 주는 "새로 생긴 자리"는 C:\$Recycle.Bin\... 꼴의 실제 파일 경로입니다.
    /// 그 경로로 셸 항목을 만들면 휴지통 항목이 아니라 그냥 파일 하나가 되어,
    /// 되돌리기 명령이 그 메뉴에 없습니다. 그래서 우리가 직접 옮깁니다.
    ///
    /// 휴지통은 지운 것마다 $R 파일(내용)과 $I 파일(원래 자리를 적어 둔 것) 둘을 둡니다.
    /// 내용만 옮기고 $I 를 남겨 두면 휴지통에 빈 항목이 남으므로 함께 지웁니다.
    /// </summary>
    private static bool MoveBack(string recycled, string original)
    {
        try
        {
            var isFolder = Directory.Exists(recycled);

            if (!isFolder && !File.Exists(recycled))
            {
                DiagnosticLog.WriteAlways($"되돌리기: 휴지통에 그 자리가 없습니다 {recycled}");
                return false;
            }

            if (File.Exists(original) || Directory.Exists(original))
            {
                DiagnosticLog.WriteAlways($"되돌리기: 원래 자리가 이미 차 있습니다 {original}");
                return false;
            }

            var parent = Path.GetDirectoryName(original);

            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            {
                Directory.CreateDirectory(parent);
            }

            if (isFolder)
            {
                Directory.Move(recycled, original);
            }
            else
            {
                File.Move(recycled, original);
            }

            RemoveMetaFile(recycled);
            DiagnosticLog.WriteAlways($"되돌리기: {original} 로 돌려놓았습니다.");
            return true;
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or ArgumentException
                                       or NotSupportedException)
        {
            DiagnosticLog.WriteAlways($"되돌리기 실패 {recycled}: {ex.GetType().Name} {ex.Message}");
            return false;
        }
    }

    /// <summary>원래 자리를 적어 둔 짝 파일을 지웁니다. 이름이 $R 로 시작하면 짝은 $I 입니다.</summary>
    private static void RemoveMetaFile(string recycled)
    {
        try
        {
            var folder = Path.GetDirectoryName(recycled);
            var name = Path.GetFileName(recycled);

            if (string.IsNullOrEmpty(folder) || !name.StartsWith("$R", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var meta = Path.Combine(folder, "$I" + name[2..]);

            if (File.Exists(meta))
            {
                File.Delete(meta);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            DiagnosticLog.WriteAlways($"되돌리기: 짝 파일을 지우지 못했습니다 {recycled}: {ex.Message}");
        }
    }

    /// <summary>휴지통을 윈도우 탐색기로 엽니다.</summary>
    public static bool Open() => ShellLauncher.Open("shell:RecycleBinFolder");

    /// <summary>
    /// 휴지통을 비웁니다. 정말 비울지 묻는 창과 진행률 창은 윈도우가 띄웁니다.
    /// 이미 비어 있으면 실패로 돌아옵니다. 윈도우가 그 둘을 가려 주지 않습니다.
    /// </summary>
    public static bool Empty(IntPtr owner)
    {
        try
        {
            return ShellNative.SHEmptyRecycleBin(owner, null, 0) >= 0;
        }
        catch (Exception ex) when (ex is COMException or EntryPointNotFoundException or DllNotFoundException)
        {
            DiagnosticLog.WriteAlways($"휴지통을 비우지 못했습니다: {ex.GetType().Name} {ex.Message}");
            return false;
        }
    }

    private static bool RestoreOne(string parsingName, string? original, IntPtr owner)
    {
        // 파일 경로로 온 자리는 우리가 직접 옮깁니다. 이 길이 실제로 도는 길입니다.
        if (!string.IsNullOrEmpty(original) && Path.IsPathRooted(parsingName))
        {
            return MoveBack(parsingName, original);
        }

        IShellItem? item = null;
        object? menuObject = null;
        var menu = IntPtr.Zero;
        var verb = IntPtr.Zero;

        try
        {
            var itemId = ShellNative.ShellItemInterfaceId;
            ShellNative.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref itemId, out var created);
            item = created;

            if (item is null)
            {
                return false;
            }

            var handler = ShellNative.HandlerUIObject;
            var menuId = ShellNative.ContextMenuInterfaceId;

            if (item.BindToHandler(IntPtr.Zero, ref handler, ref menuId, out var pointer) < 0
                || pointer == IntPtr.Zero)
            {
                return false;
            }

            menuObject = Marshal.GetObjectForIUnknown(pointer);
            Marshal.Release(pointer);

            if (menuObject is not IContextMenu contextMenu)
            {
                return false;
            }

            // 메뉴를 한 번 만들어 보아야 처리기가 자기 명령을 준비합니다.
            // 그 준비 없이 바로 부르면 아무 일도 일어나지 않는 경우가 있습니다.
            menu = ShellNative.CreatePopupMenu();

            if (menu == IntPtr.Zero)
            {
                return false;
            }

            contextMenu.QueryContextMenu(menu, 0, 1, 0x7FFF, ShellNative.CMF_NORMAL);

            // 명령 이름은 옛 규격이라 ANSI 문자열입니다.
            verb = Marshal.StringToHGlobalAnsi("undelete");

            var info = new ShellNative.InvokeCommandInfo
            {
                Size = Marshal.SizeOf<ShellNative.InvokeCommandInfo>(),
                Owner = owner,
                Verb = verb,
                Show = ShellNative.SW_SHOW
            };

            var invoked = contextMenu.InvokeCommand(ref info);
            DiagnosticLog.WriteAlways($"휴지통 복원 명령 0x{invoked:X8}: {parsingName}");

            if (invoked != 0)
            {
                DiagnosticLog.WriteAlways($"휴지통 복원 실패 0x{invoked:X8}: {parsingName}");
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is COMException
                                       or InvalidCastException
                                       or NotSupportedException
                                       or ArgumentException
                                       or System.IO.FileNotFoundException)
        {
            DiagnosticLog.WriteAlways($"휴지통 복원 중 예외 {parsingName}: {ex.GetType().Name} {ex.Message}");
            return false;
        }
        finally
        {
            if (verb != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(verb);
            }

            if (menu != IntPtr.Zero)
            {
                ShellNative.DestroyMenu(menu);
            }

            if (menuObject is not null)
            {
                Marshal.ReleaseComObject(menuObject);
            }

            if (item is not null)
            {
                Marshal.ReleaseComObject(item);
            }
        }
    }
}
