using System;
using System.Collections.Generic;
using System.IO;

namespace Explorer.Core.Services;

/// <summary>자르기 비율 하나입니다. 가로 : 세로.</summary>
public sealed record CropRatio(string Label, double Width, double Height);

/// <summary>일괄 편집에서 크기를 무엇에 맞출지입니다.</summary>
public enum ImageResizeMode
{
    /// <summary>긴 변을 이 픽셀로 맞춥니다.</summary>
    LongSide,

    /// <summary>가로를 이 픽셀로 맞춥니다.</summary>
    Width,

    /// <summary>세로를 이 픽셀로 맞춥니다.</summary>
    Height,

    /// <summary>가로세로를 이 퍼센트로 줄이거나 늘립니다.</summary>
    Percent
}

/// <summary>
/// 그림 편집에서 쓰는 셈만 모았습니다. 창과 WPF를 모르므로 시험에서 바로 돌립니다.
/// 크기 계산, 가운데 기준 비율 자르기, 겹치지 않는 저장 이름이 여기 있습니다.
/// </summary>
public static class ImageMath
{
    /// <summary>편집기가 열 수 있는 확장자입니다. 읽기는 윈도우에 깔린 코덱이 맡습니다.</summary>
    private static readonly HashSet<string> Readable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".dib", ".gif",
        ".tif", ".tiff", ".ico", ".wdp", ".jxr", ".heic", ".heif", ".webp"
    };

    /// <summary>편집할 수 있는 그림 파일인지 확장자로 봅니다.</summary>
    public static bool IsEditable(string path) =>
        !string.IsNullOrEmpty(path) && Readable.Contains(Path.GetExtension(path));

    /// <summary>
    /// 크기 조정 결과를 셉니다. 비율은 늘 유지합니다.
    /// <paramref name="noUpscale"/>가 켜져 있으면 원래보다 커지는 경우 원래 크기를 돌려줍니다.
    /// 값이 0 이하면 원래 크기를 돌려줍니다.
    /// </summary>
    public static (int Width, int Height) Resize(int width, int height, ImageResizeMode mode, double value, bool noUpscale)
    {
        if (width <= 0 || height <= 0 || value <= 0 || double.IsNaN(value) || double.IsInfinity(value))
        {
            return (width, height);
        }

        var scale = mode switch
        {
            ImageResizeMode.LongSide => value / Math.Max(width, height),
            ImageResizeMode.Width => value / width,
            ImageResizeMode.Height => value / height,
            ImageResizeMode.Percent => value / 100.0,
            _ => 1.0
        };

        if (noUpscale && scale > 1.0)
        {
            return (width, height);
        }

        var newWidth = Math.Max(1, (int)Math.Round(width * scale, MidpointRounding.AwayFromZero));
        var newHeight = Math.Max(1, (int)Math.Round(height * scale, MidpointRounding.AwayFromZero));

        // 기준으로 삼은 변은 입력한 값과 정확히 같아야 합니다. 반올림이 한 픽셀 어긋나지 않게 맞춥니다.
        switch (mode)
        {
            case ImageResizeMode.LongSide when width >= height:
            case ImageResizeMode.Width:
                newWidth = Math.Max(1, (int)Math.Round(value, MidpointRounding.AwayFromZero));
                break;
            case ImageResizeMode.LongSide:
            case ImageResizeMode.Height:
                newHeight = Math.Max(1, (int)Math.Round(value, MidpointRounding.AwayFromZero));
                break;
        }

        return (newWidth, newHeight);
    }

    /// <summary>
    /// 가운데를 기준으로 가로:세로 비율에 맞게 자를 영역을 셉니다.
    /// <paramref name="followOrientation"/>가 켜져 있으면 세로 그림에는 비율을 뒤집어 씁니다
    /// (4:3을 고르면 세로 사진은 3:4로 자릅니다).
    /// </summary>
    public static (int X, int Y, int Width, int Height) CenterCrop(
        int width, int height, double ratioWidth, double ratioHeight, bool followOrientation)
    {
        if (width <= 0 || height <= 0 || ratioWidth <= 0 || ratioHeight <= 0)
        {
            return (0, 0, width, height);
        }

        if (followOrientation && (height > width) != (ratioHeight > ratioWidth) && ratioWidth != ratioHeight)
        {
            (ratioWidth, ratioHeight) = (ratioHeight, ratioWidth);
        }

        var target = ratioWidth / ratioHeight;
        var current = (double)width / height;

        int cropWidth;
        int cropHeight;

        if (current > target)
        {
            // 옆으로 더 넓습니다. 세로는 그대로 두고 가로를 줄입니다.
            cropHeight = height;
            cropWidth = Math.Clamp((int)Math.Round(height * target, MidpointRounding.AwayFromZero), 1, width);
        }
        else
        {
            cropWidth = width;
            cropHeight = Math.Clamp((int)Math.Round(width / target, MidpointRounding.AwayFromZero), 1, height);
        }

        return ((width - cropWidth) / 2, (height - cropHeight) / 2, cropWidth, cropHeight);
    }

    /// <summary>
    /// 폴더 안에서 겹치지 않는 파일 이름을 고릅니다. 이미 있으면 "이름 (2).jpg" 처럼 번호를 붙입니다.
    /// <paramref name="taken"/>은 이번 작업에서 이미 쓰기로 한 경로입니다. 같은 이름의 원본이 둘일 때 서로 덮지 않게 합니다.
    /// </summary>
    public static string UniquePath(string folder, string baseName, string extension, Func<string, bool> exists, ISet<string>? taken = null)
    {
        var first = Path.Combine(folder, baseName + extension);

        if (!exists(first) && (taken is null || !taken.Contains(first)))
        {
            taken?.Add(first);
            return first;
        }

        for (var n = 2; n < 10000; n++)
        {
            var candidate = Path.Combine(folder, $"{baseName} ({n}){extension}");

            if (!exists(candidate) && (taken is null || !taken.Contains(candidate)))
            {
                taken?.Add(candidate);
                return candidate;
            }
        }

        throw new IOException($"저장할 이름을 고르지 못했습니다: {first}");
    }

    /// <summary>
    /// 자르기 비율 목록입니다. 단일 편집과 일괄 편집이 같이 씁니다.
    /// 사진 규격은 짧은 변을 앞에 적은 세로형이 많습니다. 가로로 쓸 때는 화면의 [가로세로 바꿈]으로 뒤집습니다.
    /// 용지·명함 비율은 표준 규격 치수에서 왔습니다.
    /// </summary>
    public static IReadOnlyList<CropRatio> CropRatios { get; } = new[]
    {
        new CropRatio("1:1", 1, 1),
        new CropRatio("1:2", 1, 2),
        new CropRatio("2:3", 2, 3),
        new CropRatio("2.5:3 (증명사진)", 2.5, 3),
        new CropRatio("3:4 (반명함)", 3, 4),
        new CropRatio("3:5", 3, 5),
        new CropRatio("3.5:4 (3×4 사진)", 3.5, 4),
        new CropRatio("3.5:4.5 (여권사진)", 3.5, 4.5),
        new CropRatio("3.5:5 (3×5 사진)", 3.5, 5),
        new CropRatio("4:5 (공무원증)", 4, 5),
        new CropRatio("4:6 (4×6 사진)", 4, 6),
        new CropRatio("5:5 (미국비자)", 5, 5),
        new CropRatio("5:6", 5, 6),
        new CropRatio("5:7 (명함사진)", 5, 7),
        new CropRatio("5.2:7 (포켓-소)", 5.2, 7),
        new CropRatio("5.2:7.6 (포켓-중)", 5.2, 7.6),
        new CropRatio("6:9 (포켓-대)", 6, 9),
        new CropRatio("3.75:5 (D3)", 3.75, 5),
        new CropRatio("4:5.33 (D4)", 4, 5.33),
        new CropRatio("8:10 (8×10)", 8, 10),
        new CropRatio("9.5:12.7 (D3)", 9.5, 12.7),
        new CropRatio("10.2:13.6 (D4)", 10.2, 13.6),
        new CropRatio("11:14", 11, 14),
        new CropRatio("12:17", 12, 17),
        new CropRatio("16:9", 16, 9),
        new CropRatio("4:3", 4, 3),
        new CropRatio("3:2", 3, 2),
        new CropRatio("9:16", 9, 16),
        new CropRatio("A·B·C 판형 (1:√2)", 1, Math.Sqrt(2)),
        new CropRatio("Letter (8.5:11)", 8.5, 11),
        new CropRatio("Legal (8.5:14)", 8.5, 14),
        new CropRatio("Ledger·Tabloid (11:17)", 11, 17),
        new CropRatio("국제 명함 (85.6:53.98)", 85.6, 53.98),
        new CropRatio("미국 명함 (3.5:2)", 3.5, 2),
        new CropRatio("일본 명함 (91:55)", 91, 55),
        new CropRatio("헝가리 명함 (90:50)", 90, 50)
    };

    /// <summary>
    /// 임의 각도로 돌린 뒤 빈 귀퉁이 없이 남길 크기를 셉니다. 원래 가로세로 비율을 지킵니다.
    /// 돌린 그림을 다 담는 캔버스 크기와, 그 가운데서 남길 사각형 크기를 돌려줍니다.
    /// 남길 사각형은 돌린 그림 안에 완전히 들어가는 가장 큰 사각형입니다.
    /// </summary>
    public static (int CanvasWidth, int CanvasHeight, int KeepWidth, int KeepHeight, double Scale) RotateToFill(
        int width, int height, double degrees)
    {
        var radians = degrees * Math.PI / 180.0;
        var cos = Math.Abs(Math.Cos(radians));
        var sin = Math.Abs(Math.Sin(radians));

        var canvasWidth = (int)Math.Ceiling((width * cos) + (height * sin) - 1e-9);
        var canvasHeight = (int)Math.Ceiling((width * sin) + (height * cos) - 1e-9);

        // 남길 사각형(sW × sH)을 거꾸로 돌렸을 때 원래 그림 안에 들어가야 합니다.
        var scale = Math.Min(
            width / ((width * cos) + (height * sin)),
            height / ((width * sin) + (height * cos)));

        scale = Math.Min(1.0, scale);

        // 가장자리의 반쯤 걸친 픽셀이 비치지 않도록 한 픽셀씩 안으로 들입니다.
        var keepWidth = Math.Max(1, (int)Math.Floor(width * scale) - (degrees % 90 == 0 ? 0 : 2));
        var keepHeight = Math.Max(1, (int)Math.Floor(height * scale) - (degrees % 90 == 0 ? 0 : 2));

        return (Math.Max(1, canvasWidth), Math.Max(1, canvasHeight), keepWidth, keepHeight, scale);
    }

    /// <summary>
    /// 그은 선의 기울기로 바로잡을 각도를 셉니다. 수평에 가까우면 수평으로, 수직에 가까우면 수직으로 맞춥니다.
    /// 화면 좌표(아래로 갈수록 y 증가) 기준이며, 돌려줄 값은 시계 방향이 양수인 보정 각도(-45~45)입니다.
    /// 선이 너무 짧으면 null 입니다.
    /// </summary>
    public static double? StraightenAngle(double x1, double y1, double x2, double y2)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;

        if ((dx * dx) + (dy * dy) < 25)
        {
            return null;
        }

        var angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;

        // 선 방향(0~180)으로 모읍니다. 오른쪽에서 왼쪽으로 그어도 같은 선입니다.
        if (angle < 0)
        {
            angle += 180;
        }

        double correction;

        if (angle <= 45)
        {
            correction = -angle;
        }
        else if (angle >= 135)
        {
            correction = 180 - angle;
        }
        else
        {
            correction = 90 - angle;
        }

        return Math.Round(correction, 2);
    }

    /// <summary>"4:3" 같은 글을 두 수로 읽습니다. 읽지 못하면 false 입니다.</summary>
    public static bool TryParseRatio(string? text, out double ratioWidth, out double ratioHeight)
    {
        ratioWidth = 0;
        ratioHeight = 0;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split(':', 'x', 'X', '×');

        if (parts.Length != 2)
        {
            return false;
        }

        return double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out ratioWidth)
            && double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out ratioHeight)
            && ratioWidth > 0
            && ratioHeight > 0;
    }
}
