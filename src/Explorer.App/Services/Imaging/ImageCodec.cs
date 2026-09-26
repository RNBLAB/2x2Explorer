using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Explorer.Core.Services;

namespace Explorer.App.Services.Imaging;

/// <summary>그림 파일의 틀입니다. 메타데이터를 옮겨 담을 수 있는지가 여기서 갈립니다.</summary>
public enum ImageContainer
{
    Other,
    Jpeg,
    Png,
    Bmp,
    Gif,
    Tiff
}

/// <summary>다시 저장할 때 메타데이터를 어떻게 다룰지입니다.</summary>
public enum MetadataPolicy
{
    /// <summary>그대로 둡니다. 방향 값만 1로 고칩니다 (방향은 픽셀에 이미 반영했으므로).</summary>
    Keep,

    /// <summary>위치 정보(GPS)만 지웁니다.</summary>
    StripGps,

    /// <summary>모두 지웁니다. 편집 칸에 넣은 값만 새로 씁니다.</summary>
    StripAll
}

/// <summary>불러온 그림입니다. 픽셀은 방향을 이미 반영했고, 메타데이터는 원본의 복사본입니다.</summary>
public sealed record LoadedImage(
    BitmapSource Pixels,
    BitmapMetadata? Metadata,
    ImageContainer Container,
    string? SourcePath,
    int Orientation);

/// <summary>저장할 때 넘기는 값입니다.</summary>
public sealed record ImageSaveOptions
{
    public int JpegQuality { get; init; } = 90;

    public BitmapMetadata? SourceMetadata { get; init; }

    public ImageContainer SourceContainer { get; init; } = ImageContainer.Other;

    public MetadataPolicy Policy { get; init; } = MetadataPolicy.Keep;

    public ExifEdit? Edit { get; init; }
}

/// <summary>
/// 그림을 읽고 씁니다. 윈도우에 들어 있는 WIC 코덱만 쓰므로 따로 받아 올 꾸러미가 없습니다.
///
/// 읽을 때 EXIF 방향 값을 픽셀에 먼저 반영합니다. 휴대폰 사진은 픽셀을 눕혀 저장하고
/// "이렇게 돌려 보라"는 값만 붙여 두는데, 방향 값을 잃은 채 다시 저장하면 사진이 누워 버립니다.
/// 그래서 픽셀을 바로 세우고, 저장할 때 방향 값을 1(그대로)로 적습니다.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ImageCodec
{
    /// <summary>JPEG·TIFF 방향 값이 있는 자리입니다.</summary>
    private const string JpegOrientation = "/app1/ifd/{ushort=274}";
    private const string TiffOrientation = "/ifd/{ushort=274}";

    public static ImageContainer ContainerOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".jpe" or ".jfif" => ImageContainer.Jpeg,
        ".png" => ImageContainer.Png,
        ".bmp" or ".dib" => ImageContainer.Bmp,
        ".gif" => ImageContainer.Gif,
        ".tif" or ".tiff" => ImageContainer.Tiff,
        _ => ImageContainer.Other
    };

    /// <summary>저장할 수 있는 형식인지입니다. WebP·HEIC는 윈도우 기본 코덱으로 읽기만 됩니다.</summary>
    public static bool CanWrite(ImageContainer container) => container != ImageContainer.Other;

    /// <summary>EXIF를 담을 수 있는 형식인지입니다.</summary>
    public static bool CanHoldExif(ImageContainer container) =>
        container is ImageContainer.Jpeg or ImageContainer.Tiff;

    /// <summary>저장 대화상자에 거는 거르개입니다. 고른 번호로 형식이 정해집니다.</summary>
    public const string SaveFilter =
        "JPEG (*.jpg)|*.jpg|PNG (*.png)|*.png|BMP (*.bmp)|*.bmp|TIFF (*.tif)|*.tif|GIF (*.gif)|*.gif";

    public const string OpenFilter =
        "그림 파일|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.bmp;*.dib;*.gif;*.tif;*.tiff;*.ico;*.wdp;*.jxr;*.heic;*.heif;*.webp|모든 파일|*.*";

    // ── 읽기 ──

    /// <summary>
    /// 파일을 읽어 방향을 바로 세운 픽셀과 메타데이터 복사본을 돌려줍니다.
    /// 끝까지 읽어 둔 뒤 파일을 놓으므로, 읽은 파일을 곧바로 덮어써도 됩니다.
    /// </summary>
    public static LoadedImage Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad);

        var frame = decoder.Frames[0];
        var container = ContainerOf(path);

        BitmapMetadata? metadata = null;

        try
        {
            if (frame.Metadata is BitmapMetadata original)
            {
                metadata = original.Clone();
                metadata.Freeze();
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or COMException or ArgumentException)
        {
            // 메타데이터를 못 읽어도 그림은 엽니다.
            DiagnosticLog.Write($"메타데이터를 읽지 못했습니다: {path} — {ex.Message}");
        }

        var orientation = ReadOrientation(metadata, container);
        var pixels = ImageOps.Normalize(frame);
        pixels = ImageOps.ApplyOrientation(pixels, orientation);

        return new LoadedImage(pixels, metadata, container, path, orientation);
    }

    /// <summary>클립보드처럼 파일 없이 들어온 그림을 편집할 수 있는 모양으로 바꿉니다.</summary>
    public static LoadedImage FromBitmap(BitmapSource source) =>
        new(ImageOps.Normalize(source), null, ImageContainer.Other, null, 1);

    /// <summary>EXIF 방향 값(1~8)입니다. 없거나 읽지 못하면 1입니다.</summary>
    public static int ReadOrientation(BitmapMetadata? metadata, ImageContainer container)
    {
        if (metadata is null || !CanHoldExif(container))
        {
            return 1;
        }

        var query = container == ImageContainer.Jpeg ? JpegOrientation : TiffOrientation;

        try
        {
            var value = metadata.ContainsQuery(query) ? metadata.GetQuery(query) : null;

            var orientation = value switch
            {
                ushort u => u,
                short s => s,
                int i => i,
                uint u => (int)u,
                _ => 1
            };

            return orientation is >= 1 and <= 8 ? orientation : 1;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or COMException or ArgumentException)
        {
            return 1;
        }
    }

    // ── 쓰기 ──

    /// <summary>
    /// 픽셀을 파일로 씁니다. 형식은 확장자로 정합니다.
    /// 먼저 같은 폴더의 임시 파일에 다 쓴 뒤 바꿔치기하므로, 도중에 실패해도 원래 파일이 깨지지 않습니다.
    /// 메타데이터를 넣다가 코덱이 거절하면 공통 항목만 담아 한 번 더, 그래도 안 되면 메타데이터 없이 씁니다.
    /// 돌려주는 목록은 그렇게 물러선 사정입니다. 비어 있으면 뜻대로 된 것입니다.
    /// </summary>
    public static IReadOnlyList<string> Save(BitmapSource pixels, string path, ImageSaveOptions options)
    {
        var target = ContainerOf(path);

        if (!CanWrite(target))
        {
            throw new NotSupportedException($"이 형식으로는 저장할 수 없습니다: {Path.GetExtension(path)}");
        }

        var notes = new List<string>();

        // JPEG·BMP는 투명을 담지 못합니다. 투명한 곳이 검게 나오지 않게 흰 바탕에 얹습니다.
        var prepared = target is ImageContainer.Jpeg or ImageContainer.Bmp
            ? ImageOps.FlattenOnWhite(pixels)
            : pixels;

        var attempts = new List<Func<BitmapMetadata?>>
        {
            () => BuildMetadata(prepared, target, options, carryAll: true),
            () => BuildMetadata(prepared, target, options, carryAll: false),
            () => null
        };

        Exception? last = null;

        for (var i = 0; i < attempts.Count; i++)
        {
            BitmapMetadata? metadata;

            try
            {
                metadata = attempts[i]();
            }
            catch (Exception ex) when (IsCodecError(ex))
            {
                last = ex;
                continue;
            }

            try
            {
                WriteAtomically(path, stream =>
                {
                    var encoder = CreateEncoder(target, options.JpegQuality);
                    encoder.Frames.Add(BitmapFrame.Create(prepared, null, metadata, null));
                    encoder.Save(stream);
                });

                if (i == 1)
                {
                    notes.Add("원본 메타데이터를 통째로 옮기지 못해 촬영 일시·카메라 같은 공통 항목만 옮겼습니다.");
                }
                else if (i == 2 && options.SourceMetadata is not null && CanHoldExif(target))
                {
                    notes.Add("메타데이터를 넣지 못해 픽셀만 저장했습니다.");
                }

                return notes;
            }
            catch (Exception ex) when (IsCodecError(ex))
            {
                last = ex;
                DiagnosticLog.Write($"저장 {i + 1}번째 시도 실패: {path} — {ex.Message}");
            }
        }

        throw new IOException($"저장하지 못했습니다: {last?.Message}", last);
    }

    /// <summary>
    /// 픽셀은 건드리지 않고 메타데이터만 고칩니다. JPEG·TIFF만 됩니다.
    ///
    /// 1) 원본을 <paramref name="destination"/>에 그대로 복사하고, 파일 안의 여유 공간에 바로 써 봅니다 (화질 손실 없음).
    /// 2) 여유 공간이 모자라면 원본 프레임을 디코딩하지 않은 채 새 메타데이터와 함께 다시 담습니다.
    ///    이때 다음번 편집을 위해 여유 공간을 넉넉히 붙여 둡니다.
    /// 돌려주는 글은 어느 길로 갔는지입니다.
    /// </summary>
    public static string RewriteMetadata(string source, string destination, MetadataPolicy policy, ExifEdit? edit)
    {
        var container = ContainerOf(source);

        if (!CanHoldExif(container))
        {
            throw new NotSupportedException("이 형식은 EXIF를 담지 못합니다.");
        }

        File.Copy(source, destination, overwrite: false);

        try
        {
            if (policy != MetadataPolicy.StripAll && TryWriteInPlace(destination, container, policy, edit))
            {
                return "메타데이터만 그 자리에서 고쳤습니다 (화질 그대로)";
            }

            WriteAtomically(destination, output =>
            {
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                var decoder = BitmapDecoder.Create(
                    input,
                    BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                    BitmapCacheOption.None);

                var frame = decoder.Frames[0];

                BitmapMetadata? metadata;

                if (policy == MetadataPolicy.StripAll)
                {
                    metadata = edit is { IsEmpty: false } ? NewMetadata(container) : null;
                }
                else
                {
                    metadata = frame.Metadata is BitmapMetadata original ? original.Clone() : NewMetadata(container);
                    ApplyPolicy(metadata, container, policy);
                }

                if (metadata is not null)
                {
                    edit?.ApplyTo(metadata);
                    AddPadding(metadata, container);
                }

                var encoder = CreateEncoder(container, jpegQuality: 100);
                encoder.Frames.Add(BitmapFrame.Create(frame, frame.Thumbnail, metadata, frame.ColorContexts));
                encoder.Save(output);
            });

            return "메타데이터를 새로 담아 다시 저장했습니다";
        }
        catch
        {
            TryDelete(destination);
            throw;
        }
    }

    private static bool TryWriteInPlace(string path, ImageContainer container, MetadataPolicy policy, ExifEdit? edit)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.Default);

            var writer = decoder.Frames[0].CreateInPlaceBitmapMetadataWriter();

            if (writer is null)
            {
                return false;
            }

            ApplyPolicy(writer, container, policy);
            edit?.ApplyTo(writer);

            return writer.TrySave();
        }
        catch (Exception ex) when (IsCodecError(ex))
        {
            DiagnosticLog.Write($"메타데이터를 그 자리에서 고치지 못했습니다: {path} — {ex.Message}");
            return false;
        }
    }

    // ── 메타데이터 짜기 ──

    private static BitmapMetadata? BuildMetadata(BitmapSource pixels, ImageContainer target, ImageSaveOptions options, bool carryAll)
    {
        if (!CanHoldExif(target))
        {
            return null;
        }

        var source = options.SourceMetadata;
        BitmapMetadata? metadata;

        if (options.Policy == MetadataPolicy.StripAll)
        {
            metadata = options.Edit is { IsEmpty: false } ? NewMetadata(target) : null;
        }
        else if (carryAll && source is not null && options.SourceContainer == target)
        {
            // 같은 틀이면 통째로 옮깁니다. 위치 정보·렌즈 정보처럼 따로 챙기기 어려운 것까지 따라갑니다.
            metadata = source.Clone();
            ApplyPolicy(metadata, target, options.Policy);
            SetQuiet(metadata, target == ImageContainer.Jpeg ? JpegOrientation : TiffOrientation, (ushort)1);
            SetQuiet(metadata, "/xmp/tiff:Orientation", "1");
            RemoveQuiet(metadata, "/app1/thumb");
            UpdateDimensions(metadata, target, pixels);
        }
        else if (source is not null)
        {
            // 틀이 다르면 쿼리 경로가 달라 통째로 옮길 수 없습니다. 공통 항목만 옮겨 적습니다.
            metadata = NewMetadata(target);
            CopyCommon(source, metadata);
        }
        else
        {
            metadata = options.Edit is { IsEmpty: false } ? NewMetadata(target) : null;
        }

        if (metadata is not null)
        {
            options.Edit?.ApplyTo(metadata);
            AddPadding(metadata, target);
        }

        return metadata;
    }

    private static BitmapMetadata NewMetadata(ImageContainer container) =>
        new(container == ImageContainer.Tiff ? "tiff" : "jpg");

    private static void ApplyPolicy(BitmapMetadata metadata, ImageContainer container, MetadataPolicy policy)
    {
        if (policy != MetadataPolicy.StripGps)
        {
            return;
        }

        RemoveQuiet(metadata, container == ImageContainer.Jpeg ? "/app1/ifd/gps" : "/ifd/gps");

        foreach (var name in new[] { "GPSLatitude", "GPSLongitude", "GPSAltitude", "GPSAltitudeRef", "GPSTimeStamp", "GPSDateStamp" })
        {
            RemoveQuiet(metadata, "/xmp/exif:" + name);
        }
    }

    /// <summary>틀이 달라도 옮길 수 있는 공통 항목입니다. 하나씩 따로 시도해 하나가 막혀도 나머지는 옮깁니다.</summary>
    private static void CopyCommon(BitmapMetadata from, BitmapMetadata to)
    {
        Try(() => { if (!string.IsNullOrEmpty(from.DateTaken)) to.DateTaken = from.DateTaken; });
        Try(() => { if (!string.IsNullOrEmpty(from.CameraManufacturer)) to.CameraManufacturer = from.CameraManufacturer; });
        Try(() => { if (!string.IsNullOrEmpty(from.CameraModel)) to.CameraModel = from.CameraModel; });
        Try(() => { if (from.Author is { Count: > 0 }) to.Author = new ReadOnlyCollection<string>(new List<string>(from.Author)); });
        Try(() => { if (!string.IsNullOrEmpty(from.Copyright)) to.Copyright = from.Copyright; });
        Try(() => { if (!string.IsNullOrEmpty(from.Title)) to.Title = from.Title; });
        Try(() => { if (!string.IsNullOrEmpty(from.Subject)) to.Subject = from.Subject; });
        Try(() => { if (!string.IsNullOrEmpty(from.Comment)) to.Comment = from.Comment; });
        Try(() => { if (from.Keywords is { Count: > 0 }) to.Keywords = new ReadOnlyCollection<string>(new List<string>(from.Keywords)); });
        Try(() => { if (from.Rating > 0) to.Rating = from.Rating; });
    }

    /// <summary>EXIF에 적힌 그림 크기를 새 크기로 고칩니다. 없던 값은 만들지 않습니다.</summary>
    private static void UpdateDimensions(BitmapMetadata metadata, ImageContainer container, BitmapSource pixels)
    {
        var exif = container == ImageContainer.Jpeg ? "/app1/ifd/exif" : "/ifd/exif";

        if (ContainsQuiet(metadata, exif + "/{ushort=40962}"))
        {
            SetQuiet(metadata, exif + "/{ushort=40962}", (uint)pixels.PixelWidth);
        }

        if (ContainsQuiet(metadata, exif + "/{ushort=40963}"))
        {
            SetQuiet(metadata, exif + "/{ushort=40963}", (uint)pixels.PixelHeight);
        }
    }

    /// <summary>
    /// 다음번에 메타데이터만 고칠 때 파일을 다시 담지 않고 그 자리에서 쓸 수 있도록 여유 공간을 둡니다.
    /// JPEG에만 뜻이 있습니다.
    /// </summary>
    private static void AddPadding(BitmapMetadata metadata, ImageContainer container)
    {
        if (container != ImageContainer.Jpeg)
        {
            return;
        }

        const uint padding = 4096;
        SetQuiet(metadata, "/app1/ifd/PaddingSchema:Padding", padding);
        SetQuiet(metadata, "/app1/ifd/exif/PaddingSchema:Padding", padding);
        SetQuiet(metadata, "/xmp/PaddingSchema:Padding", padding);
    }

    // ── 도우미 ──

    private static BitmapEncoder CreateEncoder(ImageContainer container, int jpegQuality) => container switch
    {
        ImageContainer.Jpeg => new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 1, 100) },
        ImageContainer.Png => new PngBitmapEncoder(),
        ImageContainer.Bmp => new BmpBitmapEncoder(),
        ImageContainer.Gif => new GifBitmapEncoder(),
        ImageContainer.Tiff => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
        _ => throw new NotSupportedException("이 형식으로는 저장할 수 없습니다.")
    };

    /// <summary>같은 폴더의 임시 파일에 끝까지 쓴 뒤 이름을 바꿉니다.</summary>
    private static void WriteAtomically(string path, Action<Stream> write)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var temp = Path.Combine(folder, "~" + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");

        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write($"임시 파일을 지우지 못했습니다: {path} — {ex.Message}");
        }
    }

    internal static bool IsCodecError(Exception ex) =>
        ex is NotSupportedException or InvalidOperationException or COMException or ArgumentException
            or FileFormatException or OverflowException;

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (IsCodecError(ex))
        {
            // 한 항목이 막혀도 나머지는 옮깁니다.
        }
    }

    private static bool ContainsQuiet(BitmapMetadata metadata, string query)
    {
        try
        {
            return metadata.ContainsQuery(query);
        }
        catch (Exception ex) when (IsCodecError(ex))
        {
            return false;
        }
    }

    private static void SetQuiet(BitmapMetadata metadata, string query, object value) => Try(() => metadata.SetQuery(query, value));

    private static void RemoveQuiet(BitmapMetadata metadata, string query) =>
        Try(() =>
        {
            if (metadata.ContainsQuery(query))
            {
                metadata.RemoveQuery(query);
            }
        });

    /// <summary>화면과 기록에 쓰는 짧은 설명입니다. 예: "4032 × 3024".</summary>
    public static string Describe(BitmapSource pixels) =>
        string.Format(CultureInfo.InvariantCulture, "{0} × {1}", pixels.PixelWidth, pixels.PixelHeight);
}
