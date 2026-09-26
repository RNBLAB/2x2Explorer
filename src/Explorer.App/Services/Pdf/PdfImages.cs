using System;
using System.IO;
using System.Runtime.Versioning;
using System.Windows.Media.Imaging;
using Explorer.App.Services.Imaging;

namespace Explorer.App.Services.Pdf;

/// <summary>PDF 편집기가 그림 파일을 다루는 일입니다. 쪽 썸네일과, PDF 에 넣을 JPEG 준비.</summary>
[SupportedOSPlatform("windows")]
public static class PdfImages
{
    /// <summary>
    /// PDF 에 넣을 모양으로 준비합니다. 방향 보정이 필요 없는 JPEG 은 원래 바이트를 그대로 씁니다.
    /// 그 밖에는 방향을 바로잡고 흰 바탕에 얹어 JPEG(품질 95)으로 다시 담습니다.
    /// </summary>
    public static PdfWriterService.ImageData Prepare(string path)
    {
        var loaded = ImageCodec.Load(path);

        if (loaded.Container == ImageContainer.Jpeg && loaded.Orientation == 1)
        {
            return new PdfWriterService.ImageData(File.ReadAllBytes(path), loaded.Pixels.PixelWidth, loaded.Pixels.PixelHeight);
        }

        var flat = ImageOps.FlattenOnWhite(loaded.Pixels);

        using var buffer = new MemoryStream();
        var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
        encoder.Frames.Add(BitmapFrame.Create(flat));
        encoder.Save(buffer);

        return new PdfWriterService.ImageData(buffer.ToArray(), flat.PixelWidth, flat.PixelHeight);
    }

    /// <summary>
    /// 썸네일·미리보기용으로 가로 <paramref name="width"/> 픽셀에 맞춰 읽습니다.
    /// 읽으면서 줄이므로 큰 사진도 빠릅니다. 사진 방향은 바로잡습니다.
    /// </summary>
    public static BitmapSource LoadScaled(string path, int width)
    {
        int orientation;

        using (var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var frame = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
            orientation = ImageCodec.ReadOrientation(frame.Metadata as BitmapMetadata, ImageCodec.ContainerOf(path));
        }

        // 90도로 눕힌 사진은 픽셀의 세로가 화면의 가로가 됩니다.
        var sideways = orientation is >= 5 and <= 8;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.UriSource = new Uri(path);

        if (sideways)
        {
            image.DecodePixelHeight = Math.Max(1, width);
        }
        else
        {
            image.DecodePixelWidth = Math.Max(1, width);
        }

        image.EndInit();
        image.Freeze();

        return ImageOps.ApplyOrientation(ImageOps.Normalize(image), orientation);
    }
}
