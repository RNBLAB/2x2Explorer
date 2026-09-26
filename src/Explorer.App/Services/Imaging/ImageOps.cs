using System;
using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Explorer.App.Services.Imaging;

/// <summary>
/// 픽셀을 바꾸는 일입니다. 자르기, 크기 조정, 회전, 반전, 흰 바탕에 얹기.
///
/// 결과는 늘 새 픽셀 배열로 굳혀(Freeze) 돌려줍니다. WPF의 CroppedBitmap·TransformedBitmap은
/// 앞 단계 그림을 붙들고 게으르게 계산하므로, 그대로 쌓으면 되돌리기 단계마다 사슬이 길어집니다.
/// 굳힌 그림은 어느 스레드에서든 읽을 수 있어 일괄 작업에도 그대로 씁니다.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ImageOps
{
    /// <summary>
    /// 편집하기 좋은 픽셀 형식으로 맞춥니다. 투명이 있으면 Bgra32, 없으면 Bgr24 입니다.
    /// CMYK JPEG, 16비트 PNG, 팔레트 GIF 같은 것이 모두 이 둘 가운데 하나가 됩니다.
    /// </summary>
    public static BitmapSource Normalize(BitmapSource source)
    {
        var target = HasAlpha(source) ? PixelFormats.Bgra32 : PixelFormats.Bgr24;

        BitmapSource converted = source.Format == target
            ? source
            : new FormatConvertedBitmap(source, target, null, 0);

        return Materialize(converted);
    }

    public static bool HasAlpha(BitmapSource source)
    {
        var format = source.Format;

        if (format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32
            || format == PixelFormats.Rgba64 || format == PixelFormats.Prgba64
            || format == PixelFormats.Rgba128Float || format == PixelFormats.Prgba128Float)
        {
            return true;
        }

        if (source.Palette is { } palette)
        {
            foreach (var color in palette.Colors)
            {
                if (color.A < 255)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>EXIF 방향 값(1~8)대로 픽셀을 바로 세웁니다.</summary>
    public static BitmapSource ApplyOrientation(BitmapSource source, int orientation) => orientation switch
    {
        2 => Flip(source, horizontal: true),
        3 => Rotate(source, 180),
        4 => Flip(source, horizontal: false),
        5 => Rotate(Flip(source, horizontal: true), 270),
        6 => Rotate(source, 90),
        7 => Rotate(Flip(source, horizontal: true), 90),
        8 => Rotate(source, 270),
        _ => source
    };

    /// <summary>픽셀 좌표로 자릅니다. 그림 밖으로 나간 부분은 잘라 냅니다.</summary>
    public static BitmapSource Crop(BitmapSource source, Int32Rect rect)
    {
        var x = Math.Clamp(rect.X, 0, source.PixelWidth - 1);
        var y = Math.Clamp(rect.Y, 0, source.PixelHeight - 1);
        var width = Math.Clamp(rect.Width, 1, source.PixelWidth - x);
        var height = Math.Clamp(rect.Height, 1, source.PixelHeight - y);

        return Materialize(new CroppedBitmap(source, new Int32Rect(x, y, width, height)));
    }

    /// <summary>시계 방향으로 90·180·270도 돌립니다.</summary>
    public static BitmapSource Rotate(BitmapSource source, int degrees)
    {
        var normalized = ((degrees % 360) + 360) % 360;

        if (normalized == 0)
        {
            return source;
        }

        return Materialize(new TransformedBitmap(source, new RotateTransform(normalized)));
    }

    /// <summary>
    /// 임의 각도(시계 방향이 양수)로 돌리고, 빈 귀퉁이가 없도록 원래 비율의 가장 큰 사각형만 남깁니다.
    /// WPF 로 그려서 만들므로 창과 같은 스레드(STA)에서 불러야 합니다.
    /// </summary>
    public static BitmapSource RotateToFill(BitmapSource source, double degrees)
    {
        if (Math.Abs(degrees) < 0.005)
        {
            return source;
        }

        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var (_, _, keepWidth, keepHeight, _) = Explorer.Core.Services.ImageMath.RotateToFill(width, height, degrees);

        // 남길 사각형 크기의 캔버스 한가운데에 그림 중심을 두고 돌려 그립니다. 캔버스 밖은 저절로 잘립니다.
        // 픽셀과 화면 단위를 1:1 로 맞추려고 96 DPI 로 그리고, 해상도 값은 끝에 원래대로 돌려놓습니다.
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);

        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(keepWidth / 2.0, keepHeight / 2.0));
            dc.PushTransform(new RotateTransform(degrees));
            dc.DrawImage(source, new Rect(-width / 2.0, -height / 2.0, width, height));
        }

        var target = new RenderTargetBitmap(keepWidth, keepHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();

        // 귀퉁이까지 그림이 차 있으므로 투명은 없습니다. 원본에 투명이 없었으면 투명 없는 형식으로 돌립니다.
        BitmapSource result = HasAlpha(source)
            ? new FormatConvertedBitmap(target, PixelFormats.Bgra32, null, 0)
            : new FormatConvertedBitmap(target, PixelFormats.Bgr24, null, 0);

        return WithDpi(Materialize(result), source.DpiX, source.DpiY);
    }

    /// <summary>좌우(horizontal) 또는 상하로 뒤집습니다.</summary>
    public static BitmapSource Flip(BitmapSource source, bool horizontal) =>
        Materialize(new TransformedBitmap(source, new ScaleTransform(horizontal ? -1 : 1, horizontal ? 1 : -1)));

    /// <summary>
    /// 정확히 이 픽셀 크기로 바꿉니다.
    ///
    /// 압축하지 않은 TIFF로 메모리에 한 번 담은 뒤, 읽으면서 줄이는 WIC의 크기 조정(DecodePixelWidth·Height)을 씁니다.
    /// 이 길은 가로세로를 둘 다 지정하면 그 크기가 정확히 나오고, 크게 줄일 때 계단 현상이 적습니다.
    /// </summary>
    public static BitmapSource Resize(BitmapSource source, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        if (width == source.PixelWidth && height == source.PixelHeight)
        {
            return source;
        }

        using var buffer = new MemoryStream();

        var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.None };
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(buffer);
        buffer.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile;
        image.DecodePixelWidth = width;
        image.DecodePixelHeight = height;
        image.StreamSource = buffer;
        image.EndInit();

        // TIFF를 거치며 형식이 바뀔 수 있어 다시 맞춥니다.
        var result = Normalize(image);

        // 해상도(DPI) 값은 원본을 따릅니다. 인쇄할 때 크기 계산에 쓰입니다.
        return WithDpi(result, source.DpiX, source.DpiY);
    }

    /// <summary>투명한 곳을 흰색으로 채워 Bgr24로 바꿉니다. 투명이 없으면 형식만 맞춥니다.</summary>
    public static BitmapSource FlattenOnWhite(BitmapSource source)
    {
        if (!HasAlpha(source))
        {
            return source.Format == PixelFormats.Bgr24
                ? source
                : Materialize(new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0));
        }

        var bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var width = bgra.PixelWidth;
        var height = bgra.PixelHeight;
        var inStride = width * 4;
        var input = new byte[inStride * height];
        bgra.CopyPixels(input, inStride, 0);

        var outStride = ((width * 3) + 3) & ~3;
        var output = new byte[outStride * height];

        for (var y = 0; y < height; y++)
        {
            var i = y * inStride;
            var o = y * outStride;

            for (var x = 0; x < width; x++, i += 4, o += 3)
            {
                var alpha = input[i + 3];
                var rest = 255 - alpha;

                output[o] = (byte)(((input[i] * alpha) + (255 * rest) + 127) / 255);
                output[o + 1] = (byte)(((input[i + 1] * alpha) + (255 * rest) + 127) / 255);
                output[o + 2] = (byte)(((input[i + 2] * alpha) + (255 * rest) + 127) / 255);
            }
        }

        var flat = BitmapSource.Create(width, height, source.DpiX, source.DpiY, PixelFormats.Bgr24, null, output, outStride);
        flat.Freeze();
        return flat;
    }

    /// <summary>게으르게 계산되는 그림을 픽셀 배열로 굳힙니다.</summary>
    public static BitmapSource Materialize(BitmapSource source)
    {
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var stride = ((width * source.Format.BitsPerPixel) + 31) / 32 * 4;
        var pixels = new byte[(long)stride * height];

        source.CopyPixels(pixels, stride, 0);

        var result = BitmapSource.Create(
            width,
            height,
            ValidDpi(source.DpiX),
            ValidDpi(source.DpiY),
            source.Format,
            source.Palette,
            pixels,
            stride);

        result.Freeze();
        return result;
    }

    /// <summary>픽셀은 그대로 두고 해상도(DPI) 값만 바꿉니다. 인쇄 크기 계산에 쓰입니다.</summary>
    public static BitmapSource WithDpi(BitmapSource source, double dpiX, double dpiY)
    {
        dpiX = ValidDpi(dpiX);
        dpiY = ValidDpi(dpiY);

        if (Math.Abs(source.DpiX - dpiX) < 0.01 && Math.Abs(source.DpiY - dpiY) < 0.01)
        {
            return source;
        }

        var stride = ((source.PixelWidth * source.Format.BitsPerPixel) + 31) / 32 * 4;
        var pixels = new byte[(long)stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);

        var result = BitmapSource.Create(source.PixelWidth, source.PixelHeight, dpiX, dpiY, source.Format, source.Palette, pixels, stride);
        result.Freeze();
        return result;
    }

    /// <summary>클립보드 그림은 해상도 값이 0이나 엉뚱한 값으로 오기도 합니다. 그때는 96으로 봅니다.</summary>
    private static double ValidDpi(double dpi) => dpi is > 1 and < 10000 && !double.IsNaN(dpi) ? dpi : 96;
}
