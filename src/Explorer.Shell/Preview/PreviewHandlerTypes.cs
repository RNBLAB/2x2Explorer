using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Explorer.Shell.Preview;

/// <summary>
/// 윈도우 미리보기 핸들러에 필요한 선언입니다.
///
/// 오피스·한글·PDF 같은 문서는 만든 회사가 "미리보기 핸들러"를 등록해 둡니다.
/// 탐색기 오른쪽 칸에 문서가 보이는 것이 이 구조입니다. 우리가 같은 것을 불러 쓰면 됩니다.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct HandlerRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[ComImport]
[Guid("8895b1c6-b41f-4c1c-a562-0d564250836f")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPreviewHandler
{
    void SetWindow(IntPtr window, ref HandlerRect bounds);

    void SetRect(ref HandlerRect bounds);

    void DoPreview();

    void Unload();

    void SetFocus();

    void QueryFocus(out IntPtr window);

    [PreserveSig]
    int TranslateAccelerator(IntPtr message);
}

[ComImport]
[Guid("b7d14566-0509-4cce-a71f-0a554233bd9b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithFile
{
    void Initialize([MarshalAs(UnmanagedType.LPWStr)] string filePath, uint mode);
}

[ComImport]
[Guid("7f73be3f-fb79-493c-a6c7-7ee14e245841")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithItem
{
    void Initialize(IntPtr item, uint mode);
}

/// <summary>
/// 파일 내용을 흐름(스트림)으로 받는 초기화입니다.
/// 다른 프로세스에서 도는 핸들러 가운데 이것만 받는 것이 있습니다.
/// </summary>
[ComImport]
[Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithStream
{
    void Initialize(IntPtr stream, uint mode);
}

/// <summary>핸들러에게 "너를 부른 쪽"을 알려 주는 통로입니다. 탐색기는 여기에 틀(frame)을 건넵니다.</summary>
[ComImport]
[Guid("fc4801a3-2ba9-11cf-a229-00aa003d7352")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IObjectWithSite
{
    void SetSite([MarshalAs(UnmanagedType.IUnknown)] object? site);

    void GetSite(ref Guid interfaceId, out IntPtr site);
}

/// <summary>
/// 미리보기 핸들러가 부른 쪽에 묻는 창구입니다(단축키 넘기기 등).
/// 탐색기는 이것을 늘 건넵니다. 받지 못하면 제대로 그리지 않는 핸들러가 있어 같은 모양을 갖춥니다.
/// </summary>
[ComImport]
[Guid("fec87aaf-35f9-447a-adb7-20234491401a")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPreviewHandlerFrame
{
    [PreserveSig]
    int GetWindowContext(IntPtr info);

    [PreserveSig]
    int TranslateAccelerator(IntPtr message);
}

/// <summary>
/// 우리 쪽 틀입니다. 단축키는 넘겨받지 않고, 메뉴 정보도 없다고 답합니다.
/// 핸들러가 창을 그리는 데 필요한 것은 "틀이 있다"는 사실뿐입니다.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class PreviewHandlerFrame : IPreviewHandlerFrame
{
    private const int NotImplemented = unchecked((int)0x80004001);
    private const int False = 1;

    public int GetWindowContext(IntPtr info) => NotImplemented;

    public int TranslateAccelerator(IntPtr message) => False;
}

/// <summary>미리보기 핸들러를 찾고 창을 만드는 잔심부름입니다.</summary>
[SupportedOSPlatform("windows")]
internal static class PreviewHandlerNative
{
    /// <summary>미리보기 핸들러를 가리키는 등록 열쇠입니다.</summary>
    internal const string HandlerKey = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";

    internal const uint StorageRead = 0;

    // WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS
    // CLIPCHILDREN 이 없으면 이 창이 다시 그려질 때 핸들러가 그린 자식 창 위를 흰 바탕으로 덮습니다.
    // 핸들러가 다른 프로세스에서 그리면 그 뒤에 다시 그려 준다는 보장이 없어 빈 화면으로 남습니다.
    internal const int ChildWindowStyle = 0x40000000 | 0x10000000 | 0x02000000 | 0x04000000;

    /// <summary>읽기 전용, 다른 프로그램이 열고 있어도 엶(STGM_READ | STGM_SHARE_DENY_NONE).</summary>
    internal const uint StreamReadShared = 0x00000000 | 0x00000040;

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int SHCreateStreamOnFileEx(
        string file,
        uint mode,
        uint attributes,
        [MarshalAs(UnmanagedType.Bool)] bool create,
        IntPtr template,
        out IntPtr stream);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int SHCreateItemFromParsingName(
        string path,
        IntPtr bindContext,
        ref Guid interfaceId,
        out IntPtr item);

    internal static Guid ShellItemId = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    /// <summary>ASSOCSTR_SHELLEXTENSION: 확장자에 걸린 셸 확장의 클래스 번호.</summary>
    private const int AssocShellExtension = 16;

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "AssocQueryStringW")]
    private static extern int AssocQueryString(
        int flags,
        int kind,
        string association,
        string? extra,
        System.Text.StringBuilder? output,
        ref uint length);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowEx(
        int exStyle,
        string className,
        string? windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr window, out HandlerRect rect);

    /// <summary>
    /// 그 확장자를 맡은 미리보기 핸들러의 클래스 번호를 찾습니다.
    /// 확장자에 바로 걸려 있기도 하고, 파일 종류를 한 번 거쳐 걸려 있기도 합니다.
    /// </summary>
    internal static Guid? FindHandler(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        // 탐색기와 같은 길로 찾습니다. 사용자가 고른 연결 프로그램(UserChoice)과
        // SystemFileAssociations 까지 윈도우가 알아서 따져 줍니다.
        var associated = QueryAssociation(extension);

        if (associated is not null)
        {
            return associated;
        }

        var direct = ReadHandler($@"{extension}\shellex\{HandlerKey}")
            ?? ReadHandler($@"SystemFileAssociations\{extension}\shellex\{HandlerKey}");

        if (direct is not null)
        {
            return direct;
        }

        var kind = ReadDefault(extension);

        return string.IsNullOrEmpty(kind)
            ? null
            : ReadHandler($@"{kind}\shellex\{HandlerKey}");
    }

    private static Guid? QueryAssociation(string extension)
    {
        try
        {
            uint length = 64;
            var buffer = new System.Text.StringBuilder((int)length);
            var result = AssocQueryString(0, AssocShellExtension, extension, HandlerKey, buffer, ref length);

            return result == 0 && Guid.TryParse(buffer.ToString(), out var id) ? id : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static Guid? ReadHandler(string path)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(path);

            if (key?.GetValue(null) is not string value || value.Length == 0)
            {
                return null;
            }

            return Guid.TryParse(value, out var id) ? id : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadDefault(string extension)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(extension);
            return key?.GetValue(null) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
