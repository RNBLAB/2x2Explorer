using System;
using System.IO;
using System.Runtime.Versioning;
using System.Windows.Media.Imaging;

namespace Explorer.App.Services;

/// <summary>미리보기용 이미지를 안전하게 읽어 옵니다.</summary>
[SupportedOSPlatform("windows")]
public static class PreviewImageLoader
{
    /// <summary>
    /// 이미지를 읽습니다. 실패하면 null입니다. 돌려주는 이미지는 Freeze된 상태입니다.
    ///
    /// 큰 사진을 원본 해상도로 펼치면 8000x6000 한 장이 180MB를 넘게 씁니다.
    /// 그래서 가로가 <paramref name="maxWidth"/>보다 크면 그 폭으로 줄여서 디코딩합니다.
    /// 작은 그림은 손대지 않습니다. 강제로 늘리면 흐려집니다.
    /// </summary>
    public static BitmapSource? TryLoad(string path, int maxWidth, out int pixelWidth, out int pixelHeight)
    {
        pixelWidth = 0;
        pixelHeight = 0;

        try
        {
            using (var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var decoder = BitmapDecoder.Create(
                    probe,
                    BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation,
                    BitmapCacheOption.None);

                if (decoder.Frames.Count == 0)
                {
                    return null;
                }

                pixelWidth = decoder.Frames[0].PixelWidth;
                pixelHeight = decoder.Frames[0].PixelHeight;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();

            // OnLoad로 읽어야 EndInit 직후 파일 잠금이 풀립니다.
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.StreamSource = stream;

            if (pixelWidth > maxWidth)
            {
                bitmap.DecodePixelWidth = maxWidth;
            }

            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or NotSupportedException
                                       or ArgumentException
                                       or OverflowException
                                       or OutOfMemoryException)
        {
            return null;
        }
    }
}
