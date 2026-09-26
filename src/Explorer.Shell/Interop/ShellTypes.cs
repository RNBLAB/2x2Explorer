using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Explorer.Shell.Interop;

/// <summary>셸이 다루는 항목 하나입니다. 파일이든 폴더든 이 모양으로 넘깁니다.</summary>
[ComImport]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IShellItem
{
    [PreserveSig]
    int BindToHandler(IntPtr bindContext, ref Guid handlerId, ref Guid interfaceId, out IntPtr result);

    [PreserveSig]
    int GetParent(out IShellItem parent);

    [PreserveSig]
    int GetDisplayName(uint nameKind, out IntPtr name);

    [PreserveSig]
    int GetAttributes(uint mask, out uint attributes);

    [PreserveSig]
    int Compare(IShellItem other, uint hint, out int order);
}

/// <summary>
/// 윈도우 탐색기가 쓰는 파일 조작기입니다.
/// 진행률 창, 충돌 대화상자, 휴지통, 되돌리기를 전부 윈도우가 처리합니다.
///
/// 선언 순서가 곧 함수 표의 순서입니다. 중간에 끼워 넣거나 빼면 엉뚱한 함수가 불립니다.
/// 쓰지 않는 함수도 자리를 채워 두어야 하는 이유입니다.
/// </summary>
[ComImport]
[Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IFileOperation
{
    [PreserveSig] int Advise(IntPtr sink, out uint cookie);

    [PreserveSig] int Unadvise(uint cookie);

    [PreserveSig] int SetOperationFlags(uint flags);

    [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);

    [PreserveSig] int SetProgressDialog(IntPtr popupWindow);

    [PreserveSig] int SetProperties(IntPtr propertyArray);

    [PreserveSig] int SetOwnerWindow(IntPtr owner);

    [PreserveSig] int ApplyPropertiesToItem(IShellItem item);

    [PreserveSig] int ApplyPropertiesToItems(IntPtr items);

    [PreserveSig] int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr sink);

    [PreserveSig] int RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string newName);

    [PreserveSig] int MoveItem(
        IShellItem item,
        IShellItem destinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string? newName,
        IntPtr sink);

    [PreserveSig] int MoveItems(IntPtr items, IShellItem destinationFolder);

    [PreserveSig] int CopyItem(
        IShellItem item,
        IShellItem destinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string? copyName,
        IntPtr sink);

    [PreserveSig] int CopyItems(IntPtr items, IShellItem destinationFolder);

    [PreserveSig] int DeleteItem(IShellItem item, IntPtr sink);

    [PreserveSig] int DeleteItems(IntPtr items);

    [PreserveSig] int NewItem(
        IShellItem destinationFolder,
        uint fileAttributes,
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        [MarshalAs(UnmanagedType.LPWStr)] string templateName,
        IntPtr sink);

    [PreserveSig] int PerformOperations();

    [PreserveSig] int GetAnyOperationsAborted(out int aborted);
}

/// <summary>여러 항목을 한 덩어리로 넘길 때 씁니다.</summary>
[ComImport]
[Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IShellItemArray
{
    [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handlerId, ref Guid interfaceId, out IntPtr result);

    [PreserveSig] int GetPropertyStore(uint flags, ref Guid interfaceId, out IntPtr result);

    [PreserveSig] int GetPropertyDescriptionList(IntPtr keyType, ref Guid interfaceId, out IntPtr result);

    [PreserveSig] int GetAttributes(uint attributeFlags, uint mask, out uint attributes);

    [PreserveSig] int GetCount(out uint count);

    [PreserveSig] int GetItemAt(uint index, out IShellItem item);

    [PreserveSig] int EnumItems(out IntPtr enumerator);
}

/// <summary>
/// 폴더 하나를 다루는 셸 객체입니다. 빈 공간 오른쪽 클릭 메뉴를 얻는 데 씁니다.
/// 쓰는 함수는 CreateViewObject 하나지만, 함수 표의 자리를 맞추려면 앞의 것들도 선언해야 합니다.
/// </summary>
[ComImport]
[Guid("000214E6-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IShellFolder
{
    [PreserveSig] int ParseDisplayName(
        IntPtr owner,
        IntPtr bindContext,
        [MarshalAs(UnmanagedType.LPWStr)] string displayName,
        out uint eaten,
        out IntPtr idList,
        ref uint attributes);

    [PreserveSig] int EnumObjects(IntPtr owner, int flags, out IntPtr enumerator);

    [PreserveSig] int BindToObject(IntPtr idList, IntPtr bindContext, ref Guid interfaceId, out IntPtr result);

    [PreserveSig] int BindToStorage(IntPtr idList, IntPtr bindContext, ref Guid interfaceId, out IntPtr result);

    [PreserveSig] int CompareIDs(IntPtr parameter, IntPtr first, IntPtr second);

    [PreserveSig] int CreateViewObject(IntPtr owner, ref Guid interfaceId, out IntPtr result);

    [PreserveSig] int GetAttributesOf(uint count, IntPtr idLists, ref uint inOut);

    [PreserveSig] int GetUIObjectOf(
        IntPtr owner,
        uint count,
        IntPtr idLists,
        ref Guid interfaceId,
        IntPtr reserved,
        out IntPtr result);

    [PreserveSig] int GetDisplayNameOf(IntPtr idList, uint flags, IntPtr name);

    [PreserveSig] int SetNameOf(
        IntPtr owner,
        IntPtr idList,
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        uint flags,
        out IntPtr newIdList);
}

/// <summary>오른쪽 클릭 메뉴를 만들고 실행하는 인터페이스입니다.</summary>
[ComImport]
[Guid("000214e4-0000-0000-c000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IContextMenu
{
    [PreserveSig] int QueryContextMenu(IntPtr menu, uint indexMenu, uint idFirst, uint idLast, uint flags);

    [PreserveSig] int InvokeCommand(ref ShellNative.InvokeCommandInfo info);

    [PreserveSig] int GetCommandString(UIntPtr id, uint type, IntPtr reserved, IntPtr name, uint max);
}

/// <summary>그림이 들어간 메뉴 항목을 그리기 위해 메시지를 넘겨받습니다.</summary>
[ComImport]
[Guid("000214f4-0000-0000-c000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IContextMenu2
{
    [PreserveSig] int QueryContextMenu(IntPtr menu, uint indexMenu, uint idFirst, uint idLast, uint flags);

    [PreserveSig] int InvokeCommand(ref ShellNative.InvokeCommandInfo info);

    [PreserveSig] int GetCommandString(UIntPtr id, uint type, IntPtr reserved, IntPtr name, uint max);

    [PreserveSig] int HandleMenuMsg(uint message, IntPtr wParam, IntPtr lParam);
}

/// <summary>하위 메뉴가 열릴 때까지 채워 주는 최신 버전입니다.</summary>
[ComImport]
[Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IContextMenu3
{
    [PreserveSig] int QueryContextMenu(IntPtr menu, uint indexMenu, uint idFirst, uint idLast, uint flags);

    [PreserveSig] int InvokeCommand(ref ShellNative.InvokeCommandInfo info);

    [PreserveSig] int GetCommandString(UIntPtr id, uint type, IntPtr reserved, IntPtr name, uint max);

    [PreserveSig] int HandleMenuMsg(uint message, IntPtr wParam, IntPtr lParam);

    [PreserveSig] int HandleMenuMsg2(uint message, IntPtr wParam, IntPtr lParam, out IntPtr result);
}

/// <summary>셸 조작에 필요한 나머지 선언입니다.</summary>
[SupportedOSPlatform("windows")]
internal static class ShellNative
{
    internal static readonly Guid FileOperationClassId = new("3ad05575-8857-4850-9277-11b85bdb8e09");

    internal static Guid ShellItemInterfaceId = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    /// <summary>같은 이름이 있으면 묻지 않고 "- 복사본"처럼 이름을 붙입니다.</summary>
    internal const uint FOF_RENAMEONCOLLISION = 0x0008;

    /// <summary>되돌리기를 남깁니다. 삭제는 휴지통으로 갑니다.</summary>
    internal const uint FOF_ALLOWUNDO = 0x0040;

    /// <summary>폴더를 만들 때 확인을 묻지 않습니다.</summary>
    internal const uint FOF_NOCONFIRMMKDIR = 0x0200;

    /// <summary>되돌리기 기록을 남깁니다.</summary>
    internal const uint FOFX_ADDUNDORECORD = 0x20000000;

    internal const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;

    internal const int SW_SHOW = 5;

    /// <summary>메뉴 항목을 번호가 아니라 자리로 가리킵니다.</summary>
    internal const uint MF_BYPOSITION = 0x00000400;

    /// <summary>글자로 된 보통 항목입니다.</summary>
    internal const uint MF_STRING = 0x00000000;

    /// <summary>가로줄입니다.</summary>
    internal const uint MF_SEPARATOR = 0x00000800;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InsertMenu(
        IntPtr menu,
        uint position,
        uint flags,
        UIntPtr newItemId,
        string? newItemText);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    internal static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShellExecuteEx(ref ShellExecuteInfo info);

    // ── 오른쪽 클릭 메뉴 ──

    internal static Guid ShellItemArrayInterfaceId = new("b63ea76d-1f85-456f-a19c-48159efa858b");

    internal static Guid ShellFolderInterfaceId = new("000214E6-0000-0000-C000-000000000046");

    internal static Guid ContextMenuInterfaceId = new("000214e4-0000-0000-c000-000000000046");

    /// <summary>BHID_SFUIObject — 항목들에 대한 UI 객체를 달라는 뜻입니다.</summary>
    internal static Guid HandlerUIObject = new("3981e224-f559-11d3-8e3a-00c04f6837d5");

    /// <summary>BHID_SFObject — 항목 자체에 해당하는 셸 객체를 달라는 뜻입니다.</summary>
    internal static Guid HandlerShellObject = new("3981e225-f559-11d3-8e3a-00c04f6837d5");

    internal const uint CMF_NORMAL = 0x00000000;
    internal const uint CMF_EXPLORE = 0x00000004;

    internal const uint TPM_LEFTALIGN = 0x0000;
    internal const uint TPM_RETURNCMD = 0x0100;
    internal const uint TPM_RIGHTBUTTON = 0x0002;

    internal const uint WM_INITMENUPOPUP = 0x0117;
    internal const uint WM_DRAWITEM = 0x002B;
    internal const uint WM_MEASUREITEM = 0x002C;
    internal const uint WM_MENUCHAR = 0x0120;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHParseDisplayName(
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        IntPtr bindContext,
        out IntPtr idList,
        uint attributesIn,
        out uint attributesOut);

    [DllImport("shell32.dll")]
    internal static extern int SHCreateShellItemArrayFromIDLists(
        uint count,
        [MarshalAs(UnmanagedType.LPArray)] IntPtr[] idLists,
        out IShellItemArray array);

    [DllImport("shell32.dll")]
    internal static extern int SHGetDesktopFolder(out IShellFolder folder);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr window);

    /// <summary>
    /// 휴지통을 비웁니다. 정말 비울지 묻는 창과 진행률 창은 윈도우가 띄워 줍니다.
    /// 플래그를 0으로 주면 그 둘이 다 나옵니다.
    /// </summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHEmptyRecycleBin(
        IntPtr owner,
        [MarshalAs(UnmanagedType.LPWStr)] string? root,
        uint flags);

    [DllImport("user32.dll")]
    internal static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    internal static extern int TrackPopupMenuEx(
        IntPtr menu,
        uint flags,
        int x,
        int y,
        IntPtr owner,
        IntPtr parameters);

    /// <summary>InvokeCommand에 넘기는 구조체입니다. 명령 번호는 lpVerb 자리에 숫자로 담습니다.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct InvokeCommandInfo
    {
        public int Size;
        public uint Mask;
        public IntPtr Owner;
        public IntPtr Verb;
        public IntPtr Parameters;
        public IntPtr Directory;
        public int Show;
        public uint HotKey;
        public IntPtr Icon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ShellExecuteInfo
    {
        public int Size;
        public uint Mask;
        public IntPtr Owner;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Verb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Parameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Directory;
        public int Show;
        public IntPtr InstanceHandle;
        public IntPtr IdList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public IntPtr ClassKey;
        public uint HotKey;
        public IntPtr Icon;
        public IntPtr Process;
    }
}
