using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Explorer.Core.Services;

/// <summary>
/// PDF 첫 쪽의 가로÷세로를 짐작합니다. 미리보기 칸이 PDF 도 확대·맞춤을 맡게 하려고 씁니다.
///
/// PDF 를 제대로 풀지 않고 파일 앞부분에서 글자만 찾습니다. 빨라야 하기 때문입니다(파일을 고를 때마다 불림).
/// - 첫 번째 "/Type /Page" 객체 안의 /MediaBox(없으면 /CropBox)를 씁니다.
/// - 그 객체에 없으면 파일에서 처음 나오는 /MediaBox 를 씁니다(쪽 묶음에서 물려받는 경우).
/// - /Rotate 90·270 이면 가로세로를 바꿉니다.
/// - 객체를 압축해 담은 PDF 처럼 글자로 찾을 수 없으면 null 을 돌려줍니다. 부르는 쪽이 A4 세로로 봅니다.
/// </summary>
public static class PdfPageShape
{
    /// <summary>앞에서부터 이만큼만 읽습니다. 첫 쪽 정보는 대개 앞쪽에 있습니다.</summary>
    private const int ScanBytes = 4 * 1024 * 1024;

    private static readonly Regex PageObject = new(
        @"/Type\s*/Page(?![A-Za-z])",
        RegexOptions.CultureInvariant);

    private static readonly Regex Box = new(
        @"/(MediaBox|CropBox)\s*\[\s*(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s*\]",
        RegexOptions.CultureInvariant);

    private static readonly Regex Rotate = new(
        @"/Rotate\s+(-?\d+)",
        RegexOptions.CultureInvariant);

    /// <summary>파일을 읽어 첫 쪽의 가로÷세로를 돌려줍니다. 모르면 null 입니다.</summary>
    public static double? ReadAspect(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = (int)Math.Min(stream.Length, ScanBytes);
            var buffer = new byte[length];
            var read = 0;

            while (read < length)
            {
                var n = stream.Read(buffer, read, length - read);

                if (n <= 0)
                {
                    break;
                }

                read += n;
            }

            return AspectFromText(Encoding.Latin1.GetString(buffer, 0, read));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            DiagnosticLog.Write($"PDF 쪽 모양을 읽지 못했습니다: {ex.Message}");
            return null;
        }
    }

    /// <summary>PDF 원문(글자로 푼 것)에서 첫 쪽의 가로÷세로를 찾습니다. 시험에서 직접 부릅니다.</summary>
    public static double? AspectFromText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var page = PageObject.Match(text);
        string? pageBody = null;

        if (page.Success)
        {
            // 쪽 객체의 앞뒤 경계입니다. "obj" 부터 "endobj" 까지로 봅니다.
            var start = text.LastIndexOf(" obj", page.Index, StringComparison.Ordinal);
            var end = text.IndexOf("endobj", page.Index, StringComparison.Ordinal);
            start = start < 0 ? page.Index : start;
            end = end < 0 ? Math.Min(text.Length, page.Index + 4096) : end;
            pageBody = text[start..end];
        }

        var box = FindBox(pageBody) ?? FindBox(text);

        if (box is not { } size || size.Width <= 0 || size.Height <= 0)
        {
            return null;
        }

        var rotate = 0;

        if (pageBody is not null && Rotate.Match(pageBody) is { Success: true } r)
        {
            _ = int.TryParse(r.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out rotate);
        }

        var quarter = ((rotate % 360) + 360) % 360;

        return quarter is 90 or 270
            ? size.Height / size.Width
            : size.Width / size.Height;
    }

    private static (double Width, double Height)? FindBox(string? text)
    {
        if (text is null)
        {
            return null;
        }

        // 한 객체 안에 둘 다 있으면 MediaBox 를 먼저 씁니다.
        (double, double)? crop = null;

        foreach (Match match in Box.Matches(text))
        {
            if (!TryNumber(match.Groups[2].Value, out var x0)
                || !TryNumber(match.Groups[3].Value, out var y0)
                || !TryNumber(match.Groups[4].Value, out var x1)
                || !TryNumber(match.Groups[5].Value, out var y1))
            {
                continue;
            }

            var size = (Math.Abs(x1 - x0), Math.Abs(y1 - y0));

            if (match.Groups[1].Value == "MediaBox")
            {
                return size;
            }

            crop ??= size;
        }

        return crop;
    }

    private static bool TryNumber(string value, out double number) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
}
