using System;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using Explorer.Core.Services;

namespace Explorer.App.Services.Imaging;

/// <summary>
/// 클립보드에서 그림을 꺼냅니다.
///
/// WPF의 Clipboard.GetImage()는 캡처 도구가 넣은 투명도 있는 그림을 검거나 비어 보이게 읽는 일이 있습니다.
/// 그래서 다음 차례로 봅니다.
///   1) "PNG" 형식 — 캡처 도구·브라우저가 함께 넣어 주는 경우가 많고, 투명과 색이 그대로입니다.
///   2) 복사한 그림 파일 — 탐색기에서 파일을 Ctrl+C 한 경우입니다. 파일을 그대로 엽니다(EXIF 포함).
///   3) DIB — 비트맵 머리를 붙여 BMP로 읽습니다.
///   4) 마지막으로 Clipboard.GetImage().
/// </summary>
[SupportedOSPlatform("windows")]
public static class ClipboardImage
{
    /// <summary>클립보드에 편집할 만한 그림이 있는지 가볍게 봅니다.</summary>
    public static bool HasImage()
    {
        try
        {
            return Clipboard.ContainsData("PNG")
                || Clipboard.ContainsImage()
                || Clipboard.ContainsData(DataFormats.Dib)
                || (Clipboard.ContainsFileDropList() && FirstImageFile(Clipboard.GetFileDropList()) is not null);
        }
        catch (Exception ex) when (ex is COMException or ExternalException)
        {
            return false;
        }
    }

    /// <summary>
    /// 그림을 꺼냅니다. 없으면 null 입니다. <paramref name="how"/>에는 어느 형식으로 읽었는지 적습니다.
    /// 다른 프로그램이 클립보드를 잠깐 쥐고 있으면 몇 번 다시 시도합니다.
    /// </summary>
    public static LoadedImage? Read(out string how)
    {
        how = string.Empty;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                return ReadOnce(out how);
            }
            catch (Exception ex) when (ex is COMException or ExternalException)
            {
                DiagnosticLog.Write($"클립보드가 바빠 다시 시도합니다 ({attempt + 1}): {ex.Message}");
                Thread.Sleep(60);
            }
        }

        how = "클립보드를 열지 못했습니다";
        return null;
    }

    private static LoadedImage? ReadOnce(out string how)
    {
        if (Clipboard.ContainsData("PNG") && TryDecode(Clipboard.GetData("PNG"), out var png))
        {
            how = "클립보드(PNG)";
            return ImageCodec.FromBitmap(png);
        }

        if (Clipboard.ContainsFileDropList() && FirstImageFile(Clipboard.GetFileDropList()) is { } file)
        {
            how = "클립보드(복사한 파일)";
            return ImageCodec.Load(file);
        }

        if (Clipboard.ContainsData(DataFormats.Dib) && Clipboard.GetData(DataFormats.Dib) is MemoryStream dib
            && TryDecodeDib(dib, out var bitmap))
        {
            how = "클립보드(DIB)";
            return ImageCodec.FromBitmap(bitmap);
        }

        if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
        {
            how = "클립보드";
            return ImageCodec.FromBitmap(image);
        }

        how = "클립보드에 그림이 없습니다";
        return null;
    }

    private static string? FirstImageFile(StringCollection files)
    {
        foreach (var path in files)
        {
            if (path is not null && ImageMath.IsEditable(path) && File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static bool TryDecode(object? data, out BitmapSource bitmap)
    {
        bitmap = null!;

        if (data is not Stream stream)
        {
            return false;
        }

        try
        {
            stream.Position = 0;

            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad);

            bitmap = decoder.Frames[0];
            return true;
        }
        catch (Exception ex) when (ImageCodec.IsCodecError(ex) || ex is IOException)
        {
            DiagnosticLog.Write($"클립보드 PNG를 읽지 못했습니다: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// DIB는 BMP 파일에서 맨 앞 14바이트 머리만 뺀 것입니다. 머리를 붙여 BMP 코덱에 맡깁니다.
    /// 픽셀이 시작하는 자리는 정보 머리 크기, 색 표, 비트 마스크를 더해 셉니다.
    /// </summary>
    private static bool TryDecodeDib(MemoryStream dib, out BitmapSource bitmap)
    {
        bitmap = null!;

        try
        {
            var bytes = dib.ToArray();

            if (bytes.Length < 40)
            {
                return false;
            }

            var headerSize = BitConverter.ToInt32(bytes, 0);
            var bitCount = BitConverter.ToInt16(bytes, 14);
            var compression = BitConverter.ToInt32(bytes, 16);
            var colorsUsed = BitConverter.ToInt32(bytes, 32);

            var colorTable = colorsUsed > 0 ? colorsUsed : bitCount <= 8 ? 1 << bitCount : 0;
            const int BiBitfields = 3;
            var masks = headerSize == 40 && compression == BiBitfields ? 12 : 0;

            var pixelOffset = 14 + headerSize + masks + (colorTable * 4);

            using var bmp = new MemoryStream(bytes.Length + 14);
            using (var writer = new BinaryWriter(bmp, System.Text.Encoding.ASCII, leaveOpen: true))
            {
                writer.Write((byte)'B');
                writer.Write((byte)'M');
                writer.Write(bytes.Length + 14);
                writer.Write(0);
                writer.Write(pixelOffset);
                writer.Write(bytes);
            }

            bmp.Position = 0;

            var decoder = new BmpBitmapDecoder(bmp, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            bitmap = decoder.Frames[0];
            return true;
        }
        catch (Exception ex) when (ImageCodec.IsCodecError(ex) || ex is IOException)
        {
            DiagnosticLog.Write($"클립보드 DIB를 읽지 못했습니다: {ex.Message}");
            return false;
        }
    }
}
