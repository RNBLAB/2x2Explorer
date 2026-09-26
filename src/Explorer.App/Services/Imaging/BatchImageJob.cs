using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Explorer.Core.Services;

namespace Explorer.App.Services.Imaging;

/// <summary>일괄 편집의 설정입니다. 창에서 채워 넘깁니다.</summary>
public sealed record BatchImageOptions
{
    public bool Resize { get; init; }

    public ImageResizeMode ResizeMode { get; init; } = ImageResizeMode.LongSide;

    public double ResizeValue { get; init; }

    public bool NoUpscale { get; init; } = true;

    public bool Crop { get; init; }

    public double RatioWidth { get; init; } = 4;

    public double RatioHeight { get; init; } = 3;

    public bool CropFollowsOrientation { get; init; } = true;

    /// <summary>시계 방향 회전 각도입니다. 0·90·180·270.</summary>
    public int Rotate { get; init; }

    /// <summary>바꿀 형식의 확장자(".jpg" 따위)입니다. null 이면 원래 형식을 따릅니다.</summary>
    public string? Format { get; init; }

    public int JpegQuality { get; init; } = 90;

    public MetadataPolicy Policy { get; init; } = MetadataPolicy.Keep;

    public ExifEdit? Edit { get; init; }

    /// <summary>
    /// 저장할 폴더입니다. 절대 경로면 그 폴더에 모두 모으고,
    /// 상대 이름("resized" 따위)이면 원본마다 그 폴더 아래에 만듭니다.
    /// </summary>
    public string OutputFolder { get; init; } = "resized";

    /// <summary>파일 이름 끝에 붙일 말입니다. 비워 두면 원래 이름 그대로입니다.</summary>
    public string Suffix { get; init; } = string.Empty;

    /// <summary>픽셀을 건드리는 일이 하나라도 있는지입니다. 없으면 메타데이터만 고치는 길로 갑니다.</summary>
    public bool ChangesPixels => Resize || Crop || Rotate % 360 != 0;
}

/// <summary>파일 하나를 처리한 결과입니다.</summary>
public sealed record BatchImageResult(string Source, string? Output, bool Succeeded, string Detail);

/// <summary>
/// 여러 그림에 같은 편집을 걸어 새 파일로 저장합니다. 원본은 절대 덮어쓰지 않습니다.
///
/// 처리는 오래 사는 STA 스레드 하나에서 차례로 합니다. WIC 코덱 가운데 일부는 STA에서만 제대로 돈다는
/// 보고가 있어 셸 썸네일과 같은 방식을 따랐습니다. 파일 하나가 실패해도 기록만 남기고 다음으로 갑니다.
/// </summary>
[SupportedOSPlatform("windows")]
public static class BatchImageJob
{
    public static Task<IReadOnlyList<BatchImageResult>> RunAsync(
        IReadOnlyList<string> sources,
        BatchImageOptions options,
        IProgress<string> progress,
        CancellationToken cancellation)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<BatchImageResult>>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(Run(sources, options, progress, cancellation));
            }
            catch (OperationCanceledException)
            {
                completion.SetCanceled(cancellation);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "BatchImage"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return completion.Task;
    }

    private static IReadOnlyList<BatchImageResult> Run(
        IReadOnlyList<string> sources,
        BatchImageOptions options,
        IProgress<string> progress,
        CancellationToken cancellation)
    {
        var results = new List<BatchImageResult>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < sources.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();

            var source = sources[i];
            var name = Path.GetFileName(source);
            var head = $"[{i + 1}/{sources.Count}] {name}";

            try
            {
                var result = ProcessOne(source, options, taken);
                results.Add(result);
                progress.Report(result.Succeeded ? $"{head} → {Path.GetFileName(result.Output)}  {result.Detail}" : $"{head}  건너뜀: {result.Detail}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException || ImageCodec.IsCodecError(ex))
            {
                results.Add(new BatchImageResult(source, null, false, ex.Message));
                progress.Report($"{head}  실패: {ex.Message}");
                DiagnosticLog.Write($"일괄 편집 실패: {source} — {ex}");
            }
        }

        return results;
    }

    private static BatchImageResult ProcessOne(string source, BatchImageOptions options, ISet<string> taken)
    {
        if (!File.Exists(source))
        {
            return new BatchImageResult(source, null, false, "파일이 없습니다");
        }

        var sourceContainer = ImageCodec.ContainerOf(source);
        var extension = options.Format ?? NormalizedExtension(source, sourceContainer);
        var targetContainer = ImageCodec.ContainerOf("x" + extension);

        if (!ImageCodec.CanWrite(targetContainer))
        {
            return new BatchImageResult(source, null, false, $"{Path.GetExtension(source)} 형식은 저장할 수 없습니다. 형식을 JPEG·PNG로 바꾸십시오");
        }

        var folder = ResolveFolder(source, options.OutputFolder);
        Directory.CreateDirectory(folder);

        var output = ImageMath.UniquePath(
            folder,
            Path.GetFileNameWithoutExtension(source) + options.Suffix,
            extension,
            File.Exists,
            taken);

        if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
        {
            // UniquePath 가 원본을 피하므로 오지 않을 자리지만, 원본을 덮지 않는다는 약속을 한 번 더 지킵니다.
            return new BatchImageResult(source, null, false, "원본과 같은 자리라 건너뜁니다");
        }

        var exifNote = options.Edit is { IsEmpty: false } && !ImageCodec.CanHoldExif(targetContainer)
            ? " (이 형식은 EXIF를 담지 못해 EXIF 편집은 빠졌습니다)"
            : string.Empty;

        // 픽셀을 건드리지 않고 형식도 그대로면 메타데이터만 고칩니다. JPEG을 다시 압축하지 않아 화질이 그대로입니다.
        var metadataOnly = !options.ChangesPixels
            && targetContainer == sourceContainer
            && ImageCodec.CanHoldExif(sourceContainer)
            && (options.Policy != MetadataPolicy.Keep || options.Edit is { IsEmpty: false });

        if (metadataOnly)
        {
            var how = ImageCodec.RewriteMetadata(source, output, options.Policy, options.Edit);
            return new BatchImageResult(source, output, true, how);
        }

        var loaded = ImageCodec.Load(source);
        var pixels = loaded.Pixels;
        var before = ImageCodec.Describe(pixels);

        if (options.Rotate % 360 != 0)
        {
            pixels = ImageOps.Rotate(pixels, options.Rotate);
        }

        if (options.Crop)
        {
            var (x, y, w, h) = ImageMath.CenterCrop(
                pixels.PixelWidth, pixels.PixelHeight, options.RatioWidth, options.RatioHeight, options.CropFollowsOrientation);

            if (w != pixels.PixelWidth || h != pixels.PixelHeight)
            {
                pixels = ImageOps.Crop(pixels, new Int32Rect(x, y, w, h));
            }
        }

        if (options.Resize)
        {
            var (w, h) = ImageMath.Resize(pixels.PixelWidth, pixels.PixelHeight, options.ResizeMode, options.ResizeValue, options.NoUpscale);
            pixels = ImageOps.Resize(pixels, w, h);
        }

        var notes = ImageCodec.Save(pixels, output, new ImageSaveOptions
        {
            JpegQuality = options.JpegQuality,
            SourceMetadata = loaded.Metadata,
            SourceContainer = loaded.Container,
            Policy = options.Policy,
            Edit = options.Edit
        });

        var detail = $"{before} → {ImageCodec.Describe(pixels)}";

        if (loaded.Orientation != 1)
        {
            detail += " · 방향 바로잡음";
        }

        if (notes.Count > 0)
        {
            detail += " · " + string.Join(" ", notes);
        }

        return new BatchImageResult(source, output, true, detail + exifNote);
    }

    /// <summary>원래 형식을 따를 때 쓸 확장자입니다. ".jpeg" 같은 변형은 ".jpg"로 모읍니다. 쓸 수 없는 형식은 그대로 돌려줍니다.</summary>
    private static string NormalizedExtension(string source, ImageContainer container) => container switch
    {
        ImageContainer.Jpeg => ".jpg",
        ImageContainer.Tiff => ".tif",
        ImageContainer.Bmp => ".bmp",
        ImageContainer.Png => ".png",
        ImageContainer.Gif => ".gif",
        _ => Path.GetExtension(source).ToLowerInvariant()
    };

    /// <summary>저장 폴더를 정합니다. 상대 이름이면 원본 폴더 아래입니다.</summary>
    public static string ResolveFolder(string source, string outputFolder)
    {
        if (Path.IsPathFullyQualified(outputFolder))
        {
            return outputFolder;
        }

        var parent = Path.GetDirectoryName(source) ?? ".";
        return string.IsNullOrWhiteSpace(outputFolder) ? parent : Path.Combine(parent, outputFolder);
    }
}
