using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Explorer.Shell.Interop;

namespace Explorer.Shell.Thumbnails;

/// <summary>
/// 윈도우 셸에서 파일의 썸네일이나 아이콘을 받아 옵니다.
/// 이미지·동영상·PDF·오피스 파일의 미리보기 그림을 탐색기와 똑같이 얻습니다.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ShellThumbnailProvider
{
    /// <summary>
    /// 썸네일을 얻습니다. 실패하면 null을 돌려줍니다. 호출한 쪽에서 예외를 신경 쓸 필요가 없습니다.
    /// 반환된 이미지는 Freeze된 상태라 다른 스레드로 넘겨도 됩니다.
    /// </summary>
    /// <param name="path">파일이나 폴더의 전체 경로입니다.</param>
    /// <param name="size">한 변의 길이(픽셀)입니다.</param>
    /// <param name="iconOnly">true이면 썸네일을 만들지 않고 형식 아이콘만 가져옵니다.</param>
    public static BitmapSource? TryGet(string path, int size, bool iconOnly = false)
    {
        if (string.IsNullOrWhiteSpace(path) || size <= 0)
        {
            return null;
        }

        IShellItemImageFactory? factory = null;
        var bitmapHandle = IntPtr.Zero;

        try
        {
            var interfaceId = NativeMethods.IID_IShellItemImageFactory;
            NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, ref interfaceId, out factory);

            if (factory is null)
            {
                return null;
            }

            // IconBackground를 주면 셸이 아이콘 뒤에 배경을 칠해 줍니다.
            // 그 배경색이 우리 화면과 맞지 않아 파란 사각형처럼 보입니다. 쓰지 않습니다.
            var flags = NativeMethods.ThumbnailOptions.ResizeToFit;

            if (iconOnly)
            {
                flags |= NativeMethods.ThumbnailOptions.IconOnly;
            }

            var requestedSize = new NativeMethods.Size { Width = size, Height = size };
            var result = factory.GetImage(requestedSize, flags, out bitmapHandle);

            if (result != 0 || bitmapHandle == IntPtr.Zero)
            {
                // 썸네일을 만들 수 없는 형식이면 형식 아이콘으로 한 번 더 시도합니다.
                return iconOnly ? null : TryGet(path, size, iconOnly: true);
            }

            return ToBitmapSource(bitmapHandle);
        }
        catch (Exception ex) when (ex is COMException
                                       or FileNotFoundException
                                       or UnauthorizedAccessException
                                       or ArgumentException
                                       or NotSupportedException)
        {
            return null;
        }
        finally
        {
            if (bitmapHandle != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(bitmapHandle);
            }

            if (factory is not null)
            {
                Marshal.ReleaseComObject(factory);
            }
        }
    }

    /// <summary>
    /// DIB 가 아래에서 위로 쌓였는지 봅니다.
    /// 머리 정보의 높이가 음수이면 위에서 아래로, 양수이면 아래에서 위로 쌓인 것입니다.
    /// 머리 정보를 읽지 못하면 예전처럼 아래에서 위로 본 것으로 칩니다.
    /// </summary>
    private static bool IsBottomUp(IntPtr bitmapHandle)
    {
        var section = default(DibSection);

        if (GetDibSection(bitmapHandle, Marshal.SizeOf<DibSection>(), ref section) != Marshal.SizeOf<DibSection>())
        {
            return true;
        }

        return section.HeaderHeight >= 0;
    }

    /// <summary>
    /// DIBSECTION 입니다. 앞은 BITMAP, 이어서 BITMAPINFOHEADER 가 옵니다.
    /// 여기서 쓰는 것은 머리 정보의 높이(부호로 쌓인 방향을 알립니다)뿐입니다.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DibSection
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;

        public uint HeaderSize;
        public int HeaderWidth;
        public int HeaderHeight;
        public ushort HeaderPlanes;
        public ushort HeaderBitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;

        public uint RedMask;
        public uint GreenMask;
        public uint BlueMask;
        public IntPtr Section;
        public uint Offset;
    }

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetDibSection(IntPtr handle, int size, ref DibSection section);

    private static BitmapSource? ToBitmapSource(IntPtr bitmapHandle)
    {
        var bitmap = default(NativeMethods.Bitmap);

        if (NativeMethods.GetObject(bitmapHandle, Marshal.SizeOf<NativeMethods.Bitmap>(), ref bitmap) == 0
            || bitmap.Width <= 0
            || bitmap.Height <= 0)
        {
            return null;
        }

        BitmapSource source;

        if (bitmap.BitsPixel == 32 && bitmap.Bits != IntPtr.Zero)
        {
            // 픽셀을 직접 읽어 알파 채널을 살립니다.
            // Imaging.CreateBitmapSourceFromHBitmap은 투명한 부분을 검게 만듭니다.
            //
            // 셸이 주는 32비트 DIB는 알파가 미리 곱해진(premultiplied) 값입니다.
            // Bgra32로 읽으면 색이 어긋나므로 Pbgra32로 읽어야 합니다.
            //
            // 줄이 쌓인 방향은 썸네일을 만든 쪽마다 다릅니다.
            // 사진·문서는 대개 아래에서 위로(메모리의 마지막 줄이 그림의 첫 줄) 쌓아 주고,
            // 동영상 썸네일은 위에서 아래로 쌓아 주는 일이 많습니다.
            // 한쪽으로 못박고 뒤집으면 다른 쪽이 거꾸로 나옵니다. 머리 정보를 읽어 방향을 가립니다.
            var stride = bitmap.WidthBytes;
            var rows = bitmap.Height;
            var pixels = new byte[stride * rows];
            var bottomUp = IsBottomUp(bitmapHandle);

            for (var row = 0; row < rows; row++)
            {
                var sourceRow = bottomUp ? rows - 1 - row : row;

                Marshal.Copy(
                    IntPtr.Add(bitmap.Bits, sourceRow * stride),
                    pixels,
                    row * stride,
                    stride);
            }

            source = BitmapSource.Create(
                bitmap.Width,
                rows,
                96,
                96,
                PixelFormats.Pbgra32,
                null,
                pixels,
                stride);
        }
        else
        {
            source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmapHandle,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }

        source.Freeze();
        return source;
    }
}
