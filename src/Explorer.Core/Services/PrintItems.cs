using System;
using System.Collections.Generic;
using System.IO;
using Explorer.Core.Models;

namespace Explorer.Core.Services;

/// <summary>
/// 일괄 인쇄 목록을 만드는 규칙입니다.
///
/// 어떤 확장자를 어떻게 찍을지, 폴더를 놓았을 때 무엇을 담을지, 중복을 어떻게 거를지가 여기 있습니다.
/// 실제로 찍는 일은 <c>Explorer.Shell</c> 쪽이 하고, 여기는 목록만 다룹니다.
/// </summary>
public static class PrintItems
{
    /// <summary>우리가 직접 그려서 찍는 그림입니다.</summary>
    public static IReadOnlySet<string> ImageExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff"
    };

    /// <summary>연결된 프로그램에 맡기는 문서입니다.</summary>
    public static IReadOnlySet<string> DocumentExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".rtf", ".txt", ".ppt", ".pptx",
        ".xls", ".xlsx", ".csv", ".hwp", ".hwpx", ".odt", ".ods", ".odp"
    };

    /// <summary>이 파일을 어떻게 찍을지입니다.</summary>
    public static PrintItemKind KindOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return PrintItemKind.Unsupported;
        }

        var extension = Path.GetExtension(path);

        if (ImageExtensions.Contains(extension))
        {
            return PrintItemKind.Image;
        }

        return DocumentExtensions.Contains(extension) ? PrintItemKind.Document : PrintItemKind.Unsupported;
    }

    /// <summary>화면에 보일 종류 이름입니다.</summary>
    public static string Describe(PrintItemKind kind) => kind switch
    {
        PrintItemKind.Image => "이미지",
        PrintItemKind.Document => "문서",
        _ => "지원 안 함"
    };

    /// <summary>파일 고르기 대화상자에 쓸 거르개입니다.</summary>
    public static string DialogFilter()
    {
        var images = Join(ImageExtensions);
        var documents = Join(DocumentExtensions);

        return $"인쇄할 수 있는 파일|{images};{documents}|이미지|{images}|문서|{documents}|모든 파일|*.*";
    }

    /// <summary>
    /// 끌어다 놓은 것들을 목록에 담을 경로로 펼칩니다.
    ///
    /// 폴더는 바로 아래만 봅니다. 아래로 끝없이 들어가면 폴더 하나를 놓았을 때
    /// 무엇이 담길지 사람이 짐작할 수 없습니다.
    /// 폴더 안의 것은 이름순으로 담습니다. 숫자는 수로 읽으므로 2가 10보다 앞입니다.
    /// </summary>
    /// <param name="paths">끌어다 놓았거나 골라 온 경로들입니다.</param>
    /// <param name="already">이미 목록에 있는 경로들입니다. 겹치는 것은 담지 않습니다.</param>
    /// <param name="skipped">찍을 수 없어 뺀 개수입니다.</param>
    public static IReadOnlyList<string> Expand(
        IEnumerable<string> paths,
        IEnumerable<string> already,
        out int skipped)
    {
        skipped = 0;

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in already)
        {
            known.Add(path);
        }

        var added = new List<string>();

        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var path = raw.Trim().Trim('"');

            if (Directory.Exists(path))
            {
                var inside = ReadFolder(path);

                if (inside.Count == 0)
                {
                    skipped++;
                    continue;
                }

                foreach (var file in inside)
                {
                    if (known.Add(file))
                    {
                        added.Add(file);
                    }
                }

                continue;
            }

            if (!File.Exists(path))
            {
                skipped++;
                continue;
            }

            if (KindOf(path) == PrintItemKind.Unsupported)
            {
                skipped++;
                continue;
            }

            if (known.Add(path))
            {
                added.Add(path);
            }
        }

        return added;
    }

    /// <summary>이름순으로 다시 늘어놓습니다. 숫자는 수로 읽습니다.</summary>
    public static List<string> SortByName(IEnumerable<string> paths)
    {
        var sorted = new List<string>(paths);

        sorted.Sort((left, right) =>
            NaturalStringComparer.Instance.Compare(Path.GetFileName(left), Path.GetFileName(right)));

        return sorted;
    }

    /// <summary>한 칸 위나 아래로 옮깁니다. 끝에서 더 가려 하면 아무 일도 하지 않습니다.</summary>
    public static bool Move(IList<string> paths, int index, int delta)
    {
        var target = index + delta;

        if (index < 0 || index >= paths.Count || target < 0 || target >= paths.Count)
        {
            return false;
        }

        (paths[index], paths[target]) = (paths[target], paths[index]);
        return true;
    }

    private static List<string> ReadFolder(string folder)
    {
        try
        {
            var found = new List<string>();

            foreach (var file in Directory.GetFiles(folder))
            {
                if (KindOf(file) != PrintItemKind.Unsupported)
                {
                    found.Add(file);
                }
            }

            return SortByName(found);
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or ArgumentException
                                       or NotSupportedException)
        {
            return new List<string>();
        }
    }

    private static string Join(IReadOnlySet<string> extensions)
    {
        var parts = new List<string>(extensions.Count);

        foreach (var extension in extensions)
        {
            parts.Add("*" + extension);
        }

        parts.Sort(StringComparer.OrdinalIgnoreCase);
        return string.Join(";", parts);
    }
}
