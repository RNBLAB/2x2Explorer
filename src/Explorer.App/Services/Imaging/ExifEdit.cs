using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.Versioning;
using System.Windows.Media.Imaging;
using Explorer.Core.Services;

namespace Explorer.App.Services.Imaging;

/// <summary>일괄 편집에서 고칠 수 있는 EXIF 항목입니다.</summary>
public enum ExifField
{
    DateTaken,
    Author,
    Copyright,
    Title,
    Subject,
    Comment,
    Keywords,
    CameraMaker,
    CameraModel
}

/// <summary>
/// EXIF 고칠 거리입니다. 체크한 항목만 담기므로, 담기지 않은 항목은 원래 값이 그대로 남습니다.
/// 값을 비워 두고 체크하면 그 항목을 빈 값으로 씁니다.
///
/// 항목은 WPF의 BitmapMetadata 속성으로 씁니다. 이 속성들은 윈도우의 사진 속성 체계를 거쳐
/// EXIF와 XMP 양쪽에 알맞게 들어가므로, 탐색기 속성 창에서도 같은 값으로 보입니다.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ExifEdit
{
    private readonly Dictionary<ExifField, string> _changes = new();

    public bool IsEmpty => _changes.Count == 0;

    public IReadOnlyDictionary<ExifField, string> Changes => _changes;

    public void Set(ExifField field, string value) => _changes[field] = value?.Trim() ?? string.Empty;

    /// <summary>화면에 보이는 이름입니다.</summary>
    public static string Label(ExifField field) => field switch
    {
        ExifField.DateTaken => "촬영 일시",
        ExifField.Author => "작성자",
        ExifField.Copyright => "저작권",
        ExifField.Title => "제목",
        ExifField.Subject => "주제",
        ExifField.Comment => "설명",
        ExifField.Keywords => "태그",
        ExifField.CameraMaker => "카메라 제조사",
        ExifField.CameraModel => "카메라 모델",
        _ => field.ToString()
    };

    /// <summary>촬영 일시 칸의 글을 읽습니다. "2026-09-25 14:30" 이나 "2026.9.25 14:30:05" 처럼 씁니다.</summary>
    public static bool TryParseDate(string text, out DateTime value)
    {
        var normalized = (text ?? string.Empty).Trim().Replace('.', '-').Replace('/', '-');

        string[] formats =
        {
            "yyyy-M-d H:m:s", "yyyy-M-d H:m", "yyyy-M-d", "yyyy:M:d H:m:s", "yyyy-M-dTH:m:s"
        };

        return DateTime.TryParseExact(normalized, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out value);
    }

    /// <summary>
    /// 메타데이터에 고칠 거리를 적습니다. 항목 하나가 코덱에 막혀도 나머지는 적습니다.
    /// 막힌 항목의 이름을 돌려줍니다.
    /// </summary>
    public IReadOnlyList<string> ApplyTo(BitmapMetadata metadata)
    {
        var failed = new List<string>();

        foreach (var (field, value) in _changes)
        {
            try
            {
                Apply(metadata, field, value);
            }
            catch (Exception ex) when (ImageCodec.IsCodecError(ex) || ex is FormatException)
            {
                failed.Add(Label(field));
                DiagnosticLog.Write($"EXIF {Label(field)} 을(를) 쓰지 못했습니다: {ex.Message}");
            }
        }

        return failed;
    }

    private static void Apply(BitmapMetadata metadata, ExifField field, string value)
    {
        switch (field)
        {
            case ExifField.DateTaken:
                if (TryParseDate(value, out var date))
                {
                    // BitmapMetadata 는 글로 받아 스스로 날짜로 읽습니다. 어느 지역 설정에서도 같게 읽히는 ISO 꼴로 넘깁니다.
                    metadata.DateTaken = date.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                }

                break;
            case ExifField.Author:
                metadata.Author = SplitList(value);
                break;
            case ExifField.Keywords:
                metadata.Keywords = SplitList(value);
                break;
            case ExifField.Copyright:
                metadata.Copyright = value;
                break;
            case ExifField.Title:
                metadata.Title = value;
                break;
            case ExifField.Subject:
                metadata.Subject = value;
                break;
            case ExifField.Comment:
                metadata.Comment = value;
                break;
            case ExifField.CameraMaker:
                metadata.CameraManufacturer = value;
                break;
            case ExifField.CameraModel:
                metadata.CameraModel = value;
                break;
        }
    }

    /// <summary>작성자·태그는 세미콜론으로 나눠 여럿을 적습니다. 윈도우 속성 창과 같은 규칙입니다.</summary>
    private static ReadOnlyCollection<string> SplitList(string value)
    {
        var items = new List<string>();

        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            items.Add(part);
        }

        return new ReadOnlyCollection<string>(items);
    }
}
