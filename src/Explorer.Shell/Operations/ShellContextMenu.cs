using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Interop;
using Explorer.Core.Services;
using Explorer.Shell.Interop;

namespace Explorer.Shell.Operations;

/// <summary>
/// 윈도우 탐색기의 오른쪽 클릭 메뉴를 그대로 띄웁니다.
///
/// 처음에는 IShellItemArray.BindToHandler(BHID_SFUIObject)로 메뉴를 얻으려 했으나
/// 파일 시스템 항목에서 E_NOINTERFACE(0x80004002)가 돌아옵니다.
/// 그래서 탐색기가 실제로 쓰는 길을 그대로 따릅니다.
/// 부모 폴더의 IShellFolder를 얻고, 항목마다 부모 기준 PIDL을 만든 뒤,
/// GetUIObjectOf로 IContextMenu를 받습니다.
///
/// 그림이 들어간 항목과 하위 메뉴는 메뉴가 떠 있는 동안 오는 윈도우 메시지를
/// IContextMenu2/3에게 넘겨 줘야 제대로 그려집니다.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ShellContextMenu
{
    private const uint CommandIdFirst = 1;
    private const uint CommandIdLast = 0x6FFF;

    /// <summary>우리가 끼워 넣는 항목의 번호입니다. 셸이 쓰는 범위 바깥이어야 합니다.</summary>
    private const uint ExtraCommandId = 0x7000;

    /// <summary>고른 파일·폴더들에 대한 메뉴입니다. 모두 같은 폴더 안에 있어야 합니다.</summary>
    public static bool ShowForItems(IntPtr owner, IReadOnlyList<string> paths, int screenX, int screenY) =>
        ShowForItems(owner, paths, screenX, screenY, extraItemText: null, out _);

    /// <summary>
    /// 윈도우 메뉴 맨 위에 우리 항목 하나를 끼워 넣고 띄웁니다.
    /// 검색 결과에서 "경로 열기"를 붙일 때 씁니다.
    /// </summary>
    /// <param name="extraItemText">끼워 넣을 항목의 글자입니다. null이면 끼우지 않습니다.</param>
    /// <param name="extraChosen">그 항목을 골랐으면 true입니다. 이때 셸 명령은 실행하지 않습니다.</param>
    public static bool ShowForItems(
        IntPtr owner,
        IReadOnlyList<string> paths,
        int screenX,
        int screenY,
        string? extraItemText,
        out bool extraChosen)
    {
        extraChosen = false;

        if (paths.Count == 0)
        {
            return false;
        }

        var folderPath = Path.GetDirectoryName(paths[0]);

        if (string.IsNullOrEmpty(folderPath))
        {
            DiagnosticLog.Write($"ShowForItems: 부모 폴더를 못 구함 ({paths[0]})");
            return false;
        }

        DiagnosticLog.Write($"ShowForItems 시작: {paths.Count}개, 폴더={folderPath}");

        IShellFolder? folder = null;
        var childIds = new List<IntPtr>(paths.Count);
        var childArray = IntPtr.Zero;

        try
        {
            folder = TryGetFolder(folderPath);

            if (folder is null)
            {
                return false;
            }

            foreach (var path in paths)
            {
                var name = Path.GetFileName(path);
                uint attributes = 0;

                var parsed = folder.ParseDisplayName(
                    IntPtr.Zero, IntPtr.Zero, name, out _, out var childId, ref attributes);

                if (parsed < 0 || childId == IntPtr.Zero)
                {
                    DiagnosticLog.Write($"  ParseDisplayName 실패 0x{parsed:X8}: {name}");
                    return false;
                }

                childIds.Add(childId);
            }

            // PCUITEMID_CHILD_ARRAY — 포인터가 죽 늘어선 배열입니다.
            childArray = Marshal.AllocCoTaskMem(IntPtr.Size * childIds.Count);

            for (var i = 0; i < childIds.Count; i++)
            {
                Marshal.WriteIntPtr(childArray, i * IntPtr.Size, childIds[i]);
            }

            var interfaceId = ShellNative.ContextMenuInterfaceId;

            var obtained = folder.GetUIObjectOf(
                owner, (uint)childIds.Count, childArray, ref interfaceId, IntPtr.Zero, out var menuPointer);

            if (obtained < 0 || menuPointer == IntPtr.Zero)
            {
                DiagnosticLog.Write($"  GetUIObjectOf 실패 0x{obtained:X8}");
                return false;
            }

            return ShowMenu(owner, menuPointer, screenX, screenY, extraItemText, out extraChosen);
        }
        catch (Exception ex) when (IsShellFailure(ex))
        {
            DiagnosticLog.Write($"  예외: {ex.GetType().Name} {ex.Message}");
            return false;
        }
        finally
        {
            if (childArray != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(childArray);
            }

            foreach (var childId in childIds)
            {
                Marshal.FreeCoTaskMem(childId);
            }

            if (folder is not null)
            {
                Marshal.ReleaseComObject(folder);
            }
        }
    }

    /// <summary>폴더의 빈 공간에 대한 메뉴입니다. 새로 만들기, 붙여넣기, 보기 같은 것이 들어 있습니다.</summary>
    public static bool ShowForFolderBackground(IntPtr owner, string folderPath, int screenX, int screenY)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return false;
        }

        DiagnosticLog.Write($"ShowForFolderBackground 시작: {folderPath}");

        IShellFolder? folder = null;

        try
        {
            folder = TryGetFolder(folderPath);

            if (folder is null)
            {
                return false;
            }

            var interfaceId = ShellNative.ContextMenuInterfaceId;
            var created = folder.CreateViewObject(owner, ref interfaceId, out var menuPointer);

            if (created < 0 || menuPointer == IntPtr.Zero)
            {
                DiagnosticLog.Write($"  CreateViewObject 실패 0x{created:X8}");
                return false;
            }

            return ShowMenu(owner, menuPointer, screenX, screenY, extraItemText: null, out _);
        }
        catch (Exception ex) when (IsShellFailure(ex))
        {
            DiagnosticLog.Write($"  예외: {ex.GetType().Name} {ex.Message}");
            return false;
        }
        finally
        {
            if (folder is not null)
            {
                Marshal.ReleaseComObject(folder);
            }
        }
    }

    /// <summary>
    /// 폴더 경로로 IShellFolder를 얻습니다.
    /// 바탕화면(셸 이름 공간의 뿌리)에서 시작해 그 폴더까지 파고드는 것이 정석입니다.
    /// </summary>
    private static IShellFolder? TryGetFolder(string folderPath)
    {
        var idList = IntPtr.Zero;
        IShellFolder? desktop = null;

        try
        {
            var parsed = ShellNative.SHParseDisplayName(folderPath, IntPtr.Zero, out idList, 0, out _);

            if (parsed < 0 || idList == IntPtr.Zero)
            {
                DiagnosticLog.Write($"  SHParseDisplayName 실패 0x{parsed:X8}: {folderPath}");
                return null;
            }

            if (ShellNative.SHGetDesktopFolder(out desktop) < 0 || desktop is null)
            {
                DiagnosticLog.Write("  SHGetDesktopFolder 실패");
                return null;
            }

            var interfaceId = ShellNative.ShellFolderInterfaceId;
            var bound = desktop.BindToObject(idList, IntPtr.Zero, ref interfaceId, out var folderPointer);

            if (bound < 0 || folderPointer == IntPtr.Zero)
            {
                DiagnosticLog.Write($"  BindToObject 실패 0x{bound:X8}");
                return null;
            }

            var folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPointer);
            Marshal.Release(folderPointer);
            return folder;
        }
        catch (Exception ex) when (IsShellFailure(ex))
        {
            DiagnosticLog.Write($"  폴더 얻기 예외: {ex.GetType().Name} {ex.Message}");
            return null;
        }
        finally
        {
            if (idList != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(idList);
            }

            if (desktop is not null)
            {
                Marshal.ReleaseComObject(desktop);
            }
        }
    }

    private static bool ShowMenu(
        IntPtr owner,
        IntPtr menuPointer,
        int screenX,
        int screenY,
        string? extraItemText,
        out bool extraChosen)
    {
        extraChosen = false;

        var contextMenu = Marshal.GetObjectForIUnknown(menuPointer);

        // GetObjectForIUnknown이 참조를 하나 더 잡으므로 받아 온 쪽은 여기서 놓습니다.
        Marshal.Release(menuPointer);

        var menu = ShellNative.CreatePopupMenu();
        HwndSourceHook? hook = null;
        HwndSource? source = null;

        try
        {
            if (contextMenu is not IContextMenu queryable)
            {
                DiagnosticLog.Write("  IContextMenu 로 형변환 실패");
                return false;
            }

            var query = queryable.QueryContextMenu(
                menu, 0, CommandIdFirst, CommandIdLast,
                ShellNative.CMF_NORMAL | ShellNative.CMF_EXPLORE);

            var itemCount = query & 0xFFFF;
            DiagnosticLog.Write($"  QueryContextMenu 0x{query:X8} (항목 {itemCount}개)");

            if (query < 0 || itemCount == 0)
            {
                return false;
            }

            // 우리 항목은 셸이 메뉴를 다 채운 뒤에 맨 위로 끼웁니다.
            // 가로줄을 먼저 0번 자리에 넣고 그 앞에 항목을 넣으면 항목·가로줄·셸 항목 순이 됩니다.
            if (!string.IsNullOrEmpty(extraItemText))
            {
                ShellNative.InsertMenu(
                    menu, 0, ShellNative.MF_BYPOSITION | ShellNative.MF_SEPARATOR, UIntPtr.Zero, null);

                ShellNative.InsertMenu(
                    menu,
                    0,
                    ShellNative.MF_BYPOSITION | ShellNative.MF_STRING,
                    new UIntPtr(ExtraCommandId),
                    extraItemText);
            }

            // 'as'는 COM 객체에 QueryInterface를 겁니다. 없으면 null이 돌아옵니다.
            var handler3 = contextMenu as IContextMenu3;
            var handler2 = contextMenu as IContextMenu2;

            if (handler3 is not null || handler2 is not null)
            {
                source = HwndSource.FromHwnd(owner);

                if (source is not null)
                {
                    hook = (IntPtr _, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                        ForwardMenuMessage(handler3, handler2, (uint)message, wParam, lParam, ref handled);

                    source.AddHook(hook);
                }
            }

            // 메뉴를 띄우는 창이 앞에 있지 않으면 윈도우가 메뉴를 바로 닫아 버립니다.
            ShellNative.SetForegroundWindow(owner);

            var selected = ShellNative.TrackPopupMenuEx(
                menu,
                ShellNative.TPM_RETURNCMD | ShellNative.TPM_LEFTALIGN | ShellNative.TPM_RIGHTBUTTON,
                screenX,
                screenY,
                owner,
                IntPtr.Zero);

            DiagnosticLog.Write($"  TrackPopupMenuEx 결과 {selected}");

            if (selected == (int)ExtraCommandId)
            {
                // 우리가 끼운 항목입니다. 셸에는 넘기지 않고 부르는 쪽이 알아서 합니다.
                extraChosen = true;
                return true;
            }

            if (selected < (int)CommandIdFirst)
            {
                // 아무것도 고르지 않고 닫았을 뿐입니다.
                // 메뉴를 띄우는 데는 성공했으므로 대비 메뉴를 또 띄우면 안 됩니다.
                return true;
            }

            var info = new ShellNative.InvokeCommandInfo
            {
                Size = Marshal.SizeOf<ShellNative.InvokeCommandInfo>(),
                Owner = owner,

                // 명령 번호는 포인터 자리에 숫자로 담아 넘깁니다. MAKEINTRESOURCE와 같은 방식입니다.
                Verb = new IntPtr(selected - (int)CommandIdFirst),
                Show = ShellNative.SW_SHOW
            };

            var invoked = queryable.InvokeCommand(ref info);
            DiagnosticLog.Write($"  InvokeCommand 0x{invoked:X8}");

            // 명령이 실패해도 메뉴 자체는 떴습니다.
            return true;
        }
        catch (Exception ex) when (IsShellFailure(ex))
        {
            DiagnosticLog.Write($"  메뉴 표시 중 예외: {ex.GetType().Name} {ex.Message}");
            return false;
        }
        finally
        {
            if (source is not null && hook is not null)
            {
                source.RemoveHook(hook);
            }

            ShellNative.DestroyMenu(menu);
            Marshal.ReleaseComObject(contextMenu);
        }
    }

    private static IntPtr ForwardMenuMessage(
        IContextMenu3? handler3,
        IContextMenu2? handler2,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        switch (message)
        {
            case ShellNative.WM_INITMENUPOPUP:
            case ShellNative.WM_DRAWITEM:
            case ShellNative.WM_MEASUREITEM:
            case ShellNative.WM_MENUCHAR:
                break;

            default:
                return IntPtr.Zero;
        }

        try
        {
            if (handler3 is not null)
            {
                handler3.HandleMenuMsg2(message, wParam, lParam, out var result);
                handled = true;
                return result;
            }

            if (handler2 is not null)
            {
                handler2.HandleMenuMsg(message, wParam, lParam);
                handled = true;
            }
        }
        catch (Exception ex) when (IsShellFailure(ex))
        {
            // 확장 하나가 말썽을 부려도 메뉴 전체가 죽지는 않게 합니다.
        }

        return IntPtr.Zero;
    }

    private static bool IsShellFailure(Exception ex) =>
        ex is COMException
            or InvalidCastException
            or ArgumentException
            or NotSupportedException
            or FileNotFoundException;
}
