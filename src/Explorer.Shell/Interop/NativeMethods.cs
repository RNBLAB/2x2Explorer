using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Explorer.Shell.Interop;

/// <summary>
/// 윈도우 셸 P/Invoke 선언을 모아 두는 곳입니다.
/// DllImport는 이 프로젝트 밖으로 나가지 않습니다.
///
/// 아직 들어오지 않은 것:
///   - IFileOperation  : 복사·이동·삭제·이름 바꾸기 (6단계)
///   - IPreviewHandler : 오피스·PDF 미리보기 (5단계)
///   - IContextMenu    : 우클릭 메뉴 (6단계)
///
/// x64 전용입니다. Everything64.dll과 셸 COM 호출이 모두 64비트를 전제합니다.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NativeMethods
{
    internal static readonly Guid IID_IShellItemImageFactory =
        new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    internal static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindContext,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(IntPtr handle);

    /// <summary>
    /// 바이트 배열을 지정한 코드 페이지로 해석해 유니코드 문자열로 바꿉니다.
    /// .NET Core에는 CP949 같은 옛 코드 페이지가 기본으로 없어서,
    /// NuGet 패키지를 더하는 대신 윈도우 API를 직접 부릅니다.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern int MultiByteToWideChar(
        uint codePage,
        uint flags,
        byte[] multiByteString,
        int multiByteLength,
        char[]? wideCharString,
        int wideCharLength);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    internal static extern int GetObject(IntPtr handle, int bufferSize, ref Bitmap buffer);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Size
    {
        public int Width;
        public int Height;
    }

    /// <summary>gdi32의 BITMAP 구조체입니다.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Bitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;
    }

    /// <summary>IShellItemImageFactory.GetImage에 넘기는 옵션입니다.</summary>
    [Flags]
    internal enum ThumbnailOptions
    {
        ResizeToFit = 0x00000000,
        BiggerSizeOk = 0x00000001,
        MemoryOnly = 0x00000002,
        IconOnly = 0x00000004,
        ThumbnailOnly = 0x00000008,
        InCacheOnly = 0x00000010,
        CropToSquare = 0x00000020,
        WideThumbnails = 0x00000040,
        IconBackground = 0x00000080,
        ScaleUp = 0x00000100
    }
}

[ComImport]
[Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IShellItemImageFactory
{
    [PreserveSig]
    int GetImage(NativeMethods.Size size, NativeMethods.ThumbnailOptions flags, out IntPtr bitmapHandle);
}
