using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Explorer.Core.Models;
using Explorer.Core.Services;

namespace Explorer.Shell.Printing;

/// <summary>찍고 난 결과 한 줄입니다.</summary>
public sealed record PrintResult(string Path, bool Succeeded, string Detail);

/// <summary>
/// 목록을 한 대의 프린터로 차례대로 찍습니다.
///
/// 길이 둘로 갈립니다. 그림은 우리가 직접 그려서 A4에 맞춰 찍고,
/// 문서는 연결된 프로그램의 "인쇄"에 맡깁니다.
/// PDF·한글·오피스를 우리가 직접 그릴 방법이 없고, 그러자고 변환 도구를
/// 깔아야 쓸 수 있는 프로그램으로 만들지 않기로 했기 때문입니다.
///
/// 문서 쪽 "인쇄"는 기본 프린터로만 나갑니다. 프린터를 골라 줄 수가 없습니다.
/// 그래서 목록에 문서가 하나라도 있으면 찍는 동안 윈도우 기본 프린터를 잠시 바꾸고
/// 끝나면 되돌립니다. 이 사실은 시작 전에 사람에게 알려야 합니다.
/// </summary>
[SupportedOSPlatform("windows")]
public static class BatchPrinter
{
    /// <summary>문서 하나가 끝나기를 이만큼 기다립니다. 넘으면 로그에 남기고 다음으로 갑니다.</summary>
    public const int DocumentTimeoutSeconds = 120;

    /// <summary>문서와 문서 사이에 두는 틈입니다. 스풀러와 프로그램 기동이 겹치는 것을 줄입니다.</summary>
    private const int DocumentGapMs = 1500;

    /// <summary>A4 크기입니다. 단위는 100분의 1인치라 210×297mm 가 이 수입니다.</summary>
    private const int A4WidthHundredths = 827;

    private const int A4HeightHundredths = 1169;

    private const int ExifOrientationId = 0x0112;

    /// <summary>설치된 프린터 이름들입니다.</summary>
    public static IReadOnlyList<string> InstalledPrinters()
    {
        var names = new List<string>();

        try
        {
            foreach (string? name in PrinterSettings.InstalledPrinters)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
        }
        catch (Exception ex) when (IsPrintFailure(ex))
        {
            DiagnosticLog.WriteAlways($"프린터 목록을 읽지 못했습니다: {ex.Message}");
        }

        return names;
    }

    /// <summary>지금 윈도우 기본 프린터입니다.</summary>
    public static string DefaultPrinter()
    {
        try
        {
            return new PrinterSettings().PrinterName ?? string.Empty;
        }
        catch (Exception ex) when (IsPrintFailure(ex))
        {
            return string.Empty;
        }
    }

    /// <summary>그 프린터를 지금 쓸 수 있는지입니다.</summary>
    public static bool CanUse(string printerName)
    {
        try
        {
            return new PrinterSettings { PrinterName = printerName }.IsValid;
        }
        catch (Exception ex) when (IsPrintFailure(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// 목록을 차례대로 찍습니다. 한 장이 실패해도 멈추지 않고 다음으로 갑니다.
    /// </summary>
    /// <param name="log">한 줄씩 올라오는 진행 상황입니다.</param>
    public static Task<IReadOnlyList<PrintResult>> PrintAsync(
        IReadOnlyList<string> paths,
        string printerName,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        if (paths.Count == 0 || string.IsNullOrWhiteSpace(printerName))
        {
            return Task.FromResult<IReadOnlyList<PrintResult>>(Array.Empty<PrintResult>());
        }

        // 찍는 일은 오래 걸립니다. 화면 쪽 일감을 막지 않도록 딴 실 위에서 돕니다.
        return Task.Run(() => Run(paths, printerName, log, cancellationToken), cancellationToken);
    }

    private static IReadOnlyList<PrintResult> Run(
        IReadOnlyList<string> paths,
        string printerName,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        var results = new List<PrintResult>(paths.Count);

        var settings = new PrinterSettings { PrinterName = printerName };

        if (!settings.IsValid)
        {
            log?.Report($"프린터를 쓸 수 없습니다: {printerName}");
            return results;
        }

        var a4 = GetA4(settings);
        var documents = CountDocuments(paths);

        var originalDefault = string.Empty;
        var swapped = false;

        try
        {
            if (documents > 0)
            {
                originalDefault = DefaultPrinter();

                if (!string.Equals(originalDefault, printerName, StringComparison.Ordinal))
                {
                    swapped = PrinterNative.SetDefaultPrinter(printerName);

                    log?.Report(swapped
                        ? $"기본 프린터를 [{printerName}] 으로 잠시 바꿨습니다. (원래: {originalDefault})"
                        : $"기본 프린터를 바꾸지 못했습니다. 문서는 [{originalDefault}] 으로 나갑니다.");
                }
            }

            log?.Report($"인쇄 시작 — {printerName} / {paths.Count}개");

            for (var i = 0; i < paths.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    log?.Report("멈췄습니다. 이미 보낸 것은 취소되지 않습니다.");
                    break;
                }

                var path = paths[i];
                var name = Path.GetFileName(path);

                try
                {
                    var detail = PrintItems.KindOf(path) == PrintItemKind.Image
                        ? PrintImage(path, printerName, a4)
                        : PrintDocument(path);

                    log?.Report($"[{i + 1}/{paths.Count}] {name}  →  {detail}");
                    results.Add(new PrintResult(path, true, detail));
                }
                catch (Exception ex) when (IsPrintFailure(ex))
                {
                    log?.Report($"[{i + 1}/{paths.Count}] {name}  →  실패: {ex.Message}");
                    results.Add(new PrintResult(path, false, ex.Message));
                }
            }
        }
        finally
        {
            if (swapped)
            {
                if (PrinterNative.SetDefaultPrinter(originalDefault))
                {
                    log?.Report($"기본 프린터를 [{originalDefault}] 로 되돌렸습니다.");
                }
                else
                {
                    log?.Report($"기본 프린터를 되돌리지 못했습니다. 직접 확인해 주십시오. (원래: {originalDefault})");
                    DiagnosticLog.WriteAlways($"기본 프린터 복구 실패. 원래 값: {originalDefault}");
                }
            }
        }

        return results;
    }

    /// <summary>
    /// 그림 한 장을 A4에 찍습니다.
    /// 방향은 픽셀 크기로 정하고, 비율을 지킨 채 용지에 꽉 차게 키워 한가운데 놓습니다.
    /// </summary>
    private static string PrintImage(string path, string printerName, PaperSize a4)
    {
        using var image = Image.FromFile(path);

        ApplyExifOrientation(image);

        var landscape = image.Width > image.Height;

        using var document = new PrintDocument();

        document.PrinterSettings.PrinterName = printerName;
        document.DocumentName = Path.GetFileName(path);

        // 좌표 원점을 인쇄 가능 영역 기준으로 둡니다. 그리는 쪽의 하드 마진 보정과 짝입니다.
        document.OriginAtMargins = false;
        document.DefaultPageSettings.PaperSize = a4;
        document.DefaultPageSettings.Landscape = landscape;
        document.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);

        document.PrintPage += (_, e) => DrawFitted(e, image);
        document.Print();

        return $"이미지 / A4 {(landscape ? "가로" : "세로")} / {image.Width}x{image.Height}px";
    }

    /// <summary>
    /// 이 모듈에서 가장 틀리기 쉬운 자리입니다.
    ///
    /// PageBounds 는 용지 전체 크기이고 가로 방향이 반영되어 있습니다.
    /// PaperSize.Width/Height 는 방향을 반영하지 않으므로 그쪽을 쓰면 안 됩니다.
    /// 그리고 원점이 용지 좌상단이 아니라 인쇄 가능 영역 좌상단이라,
    /// 용지 기준으로 잡은 좌표를 쓰려면 하드 마진만큼 빼 주어야 합니다.
    /// 하드 마진 역시 방향을 반영하지 않아 가로일 때는 가로세로를 뒤바꿔야 합니다.
    /// </summary>
    private static void DrawFitted(PrintPageEventArgs e, Image image)
    {
        if (e.Graphics is not { } graphics)
        {
            return;
        }

        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;

        float pageWidth = e.PageBounds.Width;
        float pageHeight = e.PageBounds.Height;

        var ratio = Math.Min(pageWidth / image.Width, pageHeight / image.Height);
        var width = image.Width * ratio;
        var height = image.Height * ratio;

        var marginX = e.PageSettings.HardMarginX;
        var marginY = e.PageSettings.HardMarginY;

        if (e.PageSettings.Landscape)
        {
            (marginX, marginY) = (marginY, marginX);
        }

        var x = ((pageWidth - width) / 2f) - marginX;
        var y = ((pageHeight - height) / 2f) - marginY;

        graphics.DrawImage(image, new RectangleF(x, y, width, height));
        e.HasMorePages = false;
    }

    /// <summary>
    /// 휴대폰이나 메신저로 받은 사진은 픽셀은 가로로 두고 "돌려서 보여라"라는 표시만 붙여 둡니다.
    /// 그 표시를 픽셀에 먼저 반영하지 않으면 방향 판정이 거꾸로 됩니다.
    /// </summary>
    private static void ApplyExifOrientation(Image image)
    {
        if (Array.IndexOf(image.PropertyIdList, ExifOrientationId) < 0)
        {
            return;
        }

        var property = image.GetPropertyItem(ExifOrientationId);

        if (property?.Value is not { Length: > 0 } value)
        {
            return;
        }

        switch (value[0])
        {
            case 2:
                image.RotateFlip(RotateFlipType.RotateNoneFlipX);
                break;

            case 3:
                image.RotateFlip(RotateFlipType.Rotate180FlipNone);
                break;

            case 4:
                image.RotateFlip(RotateFlipType.Rotate180FlipX);
                break;

            case 5:
                image.RotateFlip(RotateFlipType.Rotate90FlipX);
                break;

            case 6:
                image.RotateFlip(RotateFlipType.Rotate90FlipNone);
                break;

            case 7:
                image.RotateFlip(RotateFlipType.Rotate270FlipX);
                break;

            case 8:
                image.RotateFlip(RotateFlipType.Rotate270FlipNone);
                break;

            default:
                return;
        }

        image.RemovePropertyItem(ExifOrientationId);
    }

    /// <summary>
    /// 문서는 연결된 프로그램의 "인쇄"에 맡깁니다.
    /// 그 프로그램이 정말 스풀에 넣었는지는 알 수 없습니다. 보냈다는 것까지만 압니다.
    /// 한글처럼 보안 승인 창을 띄우는 프로그램이 있어 기다리는 시간에 한계를 둡니다.
    /// </summary>
    private static string PrintDocument(string path)
    {
        var info = new ProcessStartInfo(path)
        {
            Verb = "print",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Minimized
        };

        using var process = Process.Start(info);

        if (process is null)
        {
            Thread.Sleep(DocumentGapMs);
            return "기본 인쇄로 보냄 (프로그램을 따라갈 수 없음)";
        }

        var finished = process.WaitForExit(DocumentTimeoutSeconds * 1000);
        Thread.Sleep(DocumentGapMs);

        return finished
            ? "기본 인쇄로 보냄"
            : $"기본 인쇄로 보냄 (프로그램이 {DocumentTimeoutSeconds}초 안에 끝나지 않아 다음으로 넘어감)";
    }

    /// <summary>프린터가 A4를 내놓지 않으면 직접 만들어 씁니다.</summary>
    private static PaperSize GetA4(PrinterSettings settings)
    {
        try
        {
            foreach (PaperSize size in settings.PaperSizes)
            {
                if (size.Kind == PaperKind.A4)
                {
                    return size;
                }
            }
        }
        catch (Exception ex) when (IsPrintFailure(ex))
        {
            DiagnosticLog.WriteAlways($"용지 목록을 읽지 못했습니다: {ex.Message}");
        }

        return new PaperSize("A4", A4WidthHundredths, A4HeightHundredths)
        {
            RawKind = (int)PaperKind.A4
        };
    }

    private static int CountDocuments(IReadOnlyList<string> paths)
    {
        var count = 0;

        foreach (var path in paths)
        {
            if (PrintItems.KindOf(path) == PrintItemKind.Document)
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsPrintFailure(Exception ex) =>
        ex is InvalidPrinterException
            or Win32Exception
            or IOException
            or UnauthorizedAccessException
            or OutOfMemoryException
            or ArgumentException
            or InvalidOperationException
            or ExternalException
            or NotSupportedException
            or PlatformNotSupportedException;

    /// <summary>기본 프린터를 바꾸는 일만 하는 자리입니다.</summary>
    private static class PrinterNative
    {
        public static bool SetDefaultPrinter(string printerName)
        {
            if (string.IsNullOrWhiteSpace(printerName))
            {
                return false;
            }

            try
            {
                return Native(printerName);
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
            {
                return false;
            }
        }

        [DllImport("winspool.drv", EntryPoint = "SetDefaultPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Native([MarshalAs(UnmanagedType.LPWStr)] string printerName);
    }
}
