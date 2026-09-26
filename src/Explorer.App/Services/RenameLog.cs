using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Explorer.Core.Services;

namespace Explorer.App.Services;

/// <summary>이름을 바꾼 항목 하나입니다. 둘 다 전체 경로입니다.</summary>
public sealed record RenamePair(string Before, string After);

/// <summary>남아 있는 기록 파일 하나입니다.</summary>
public sealed record RenameRecord(string FilePath, DateTime When, int Count, string Folder)
{
    /// <summary>목록에 한 줄로 보일 글입니다.</summary>
    public string Summary =>
        $"{When.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)} · {Count}개";
}

/// <summary>
/// 일괄 이름 바꾸기를 파일로 적어 둡니다.
///
/// 되돌리기는 프로그램을 끄면 사라지지만 이 기록은 남습니다.
/// 한참 뒤에도 무엇을 어떻게 바꿨는지 보고 되돌릴 수 있어야 하기 때문입니다.
/// </summary>
public static class RenameLog
{
    private const string HeaderMark = "#";

    /// <summary>기록이 쌓이는 폴더입니다.</summary>
    public static string FolderPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "2x2Explorer",
        "rename-logs");

    /// <summary>기록 한 장을 남깁니다. 남긴 파일의 경로를 돌려주고, 실패하면 null 입니다.</summary>
    public static string? Write(IReadOnlyList<RenamePair> pairs)
    {
        if (pairs.Count == 0)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(FolderPath);

            var now = DateTime.Now;
            var path = Path.Combine(
                FolderPath,
                $"rename-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.txt");

            var builder = new StringBuilder();
            builder.AppendLine($"{HeaderMark} Explorer 일괄 이름 바꾸기");
            builder.AppendLine($"{HeaderMark} 일시\t{now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
            builder.AppendLine($"{HeaderMark} 개수\t{pairs.Count}");
            builder.AppendLine($"{HeaderMark} 바꾸기 전 전체경로\t바꾼 뒤 전체경로");

            foreach (var pair in pairs)
            {
                builder.AppendLine($"{pair.Before}\t{pair.After}");
            }

            File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
            return path;
        }
        catch (Exception ex) when (IsWriteFailure(ex))
        {
            DiagnosticLog.WriteAlways($"이름 바꾸기 기록을 남기지 못했습니다: {ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    /// <summary>남아 있는 기록을 새것부터 돌려줍니다.</summary>
    public static IReadOnlyList<RenameRecord> List()
    {
        try
        {
            if (!Directory.Exists(FolderPath))
            {
                return Array.Empty<RenameRecord>();
            }

            var records = new List<RenameRecord>();

            foreach (var file in Directory.GetFiles(FolderPath, "rename-*.txt"))
            {
                if (Describe(file) is { } record)
                {
                    records.Add(record);
                }
            }

            records.Sort((left, right) => right.When.CompareTo(left.When));
            return records;
        }
        catch (Exception ex) when (IsWriteFailure(ex))
        {
            return Array.Empty<RenameRecord>();
        }
    }

    /// <summary>기록 한 장을 읽습니다.</summary>
    public static IReadOnlyList<RenamePair> Read(string path)
    {
        try
        {
            var pairs = new List<RenamePair>();

            foreach (var line in File.ReadLines(path))
            {
                if (line.Length == 0 || line.StartsWith(HeaderMark, StringComparison.Ordinal))
                {
                    continue;
                }

                var tab = line.IndexOf('\t');

                if (tab <= 0 || tab == line.Length - 1)
                {
                    continue;
                }

                pairs.Add(new RenamePair(line[..tab], line[(tab + 1)..]));
            }

            return pairs;
        }
        catch (Exception ex) when (IsWriteFailure(ex))
        {
            DiagnosticLog.WriteAlways($"이름 바꾸기 기록을 읽지 못했습니다 {path}: {ex.Message}");
            return Array.Empty<RenamePair>();
        }
    }

    /// <summary>머리말만 훑어 목록에 쓸 것을 만듭니다. 몸통까지 읽지는 않습니다.</summary>
    private static RenameRecord? Describe(string path)
    {
        var when = File.GetLastWriteTime(path);
        var count = 0;
        var folder = string.Empty;

        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith($"{HeaderMark} 일시\t", StringComparison.Ordinal)
                && DateTime.TryParse(
                    line[(line.IndexOf('\t') + 1)..],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsed))
            {
                when = parsed;
                continue;
            }

            if (line.StartsWith(HeaderMark, StringComparison.Ordinal))
            {
                continue;
            }

            var tab = line.IndexOf('\t');

            if (tab <= 0)
            {
                continue;
            }

            if (folder.Length == 0)
            {
                folder = Path.GetDirectoryName(line[..tab]) ?? string.Empty;
            }

            count++;
        }

        return count == 0 ? null : new RenameRecord(path, when, count, folder);
    }

    private static bool IsWriteFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
}
