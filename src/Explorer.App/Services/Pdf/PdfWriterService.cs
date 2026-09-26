using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Explorer.App.Services.Pdf;

/// <summary>
/// 쪽 목록으로 새 PDF 를 만듭니다. PDFsharp(MIT)를 씁니다.
///
/// PDF 쪽은 원본에서 그대로 옮겨 오므로(가져오기 모드) 글자·벡터가 그림으로 바뀌지 않습니다.
/// 그림은 JPEG 로 담습니다. 원본이 방향 보정이 필요 없는 JPEG 이면 원래 바이트를 그대로 넣어 화질 손실이 없습니다.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PdfWriterService
{
    /// <summary>그림 한 장을 PDF 에 넣을 모양으로 준비한 것입니다. JPEG 바이트와 픽셀 크기입니다.</summary>
    public sealed record ImageData(byte[] Jpeg, int PixelWidth, int PixelHeight);

    /// <summary>A4 크기(포인트)입니다.</summary>
    private const double A4Width = 595.276;
    private const double A4Height = 841.89;

    /// <summary>
    /// 쪽 목록을 PDF 로 저장합니다. 같은 폴더의 임시 파일에 다 쓴 뒤 이름을 바꿉니다.
    /// <paramref name="fitImagesToA4"/>가 켜져 있으면 그림 쪽을 A4(그림 방향에 맞춰 가로·세로)에 비율 유지로 맞춥니다.
    /// 꺼져 있으면 그림을 96 DPI 로 보고 그 크기의 쪽을 만듭니다.
    /// </summary>
    public static void Save(IReadOnlyList<PdfPageRef> pages, string output, bool fitImagesToA4, Func<string, ImageData> loadImage)
    {
        var inputs = new Dictionary<string, PdfDocument>(StringComparer.OrdinalIgnoreCase);
        var streams = new List<Stream>();
        var folder = Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".";
        var temp = Path.Combine(folder, "~" + Path.GetFileName(output) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");

        try
        {
            using (var result = new PdfDocument())
            {
                foreach (var page in pages)
                {
                    if (!page.IsImage)
                    {
                        if (!inputs.TryGetValue(page.Path, out var input))
                        {
                            input = PdfReader.Open(page.Path, PdfDocumentOpenMode.Import);
                            inputs[page.Path] = input;
                        }

                        result.AddPage(input.Pages[page.PageIndex]);
                        continue;
                    }

                    var data = loadImage(page.Path);
                    var stream = new MemoryStream(data.Jpeg, writable: false);
                    streams.Add(stream);

                    var image = XImage.FromStream(stream);
                    var target = result.AddPage();

                    double pageWidth, pageHeight, drawX, drawY, drawWidth, drawHeight;
                    var natural = (Width: data.PixelWidth * 72.0 / 96.0, Height: data.PixelHeight * 72.0 / 96.0);

                    if (fitImagesToA4)
                    {
                        var landscape = data.PixelWidth > data.PixelHeight;
                        pageWidth = landscape ? A4Height : A4Width;
                        pageHeight = landscape ? A4Width : A4Height;

                        var scale = Math.Min(pageWidth / natural.Width, pageHeight / natural.Height);
                        drawWidth = natural.Width * scale;
                        drawHeight = natural.Height * scale;
                        drawX = (pageWidth - drawWidth) / 2;
                        drawY = (pageHeight - drawHeight) / 2;
                    }
                    else
                    {
                        pageWidth = natural.Width;
                        pageHeight = natural.Height;
                        drawX = 0;
                        drawY = 0;
                        drawWidth = pageWidth;
                        drawHeight = pageHeight;
                    }

                    target.Width = XUnit.FromPoint(pageWidth);
                    target.Height = XUnit.FromPoint(pageHeight);

                    using var graphics = XGraphics.FromPdfPage(target);
                    graphics.DrawImage(image, drawX, drawY, drawWidth, drawHeight);
                }

                result.Save(temp);
            }

            File.Move(temp, output, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (IOException)
            {
                // 임시 파일을 못 지워도 원래 실패를 알리는 것이 먼저입니다.
            }

            throw;
        }
        finally
        {
            foreach (var input in inputs.Values)
            {
                input.Dispose();
            }

            foreach (var stream in streams)
            {
                stream.Dispose();
            }
        }
    }
}
