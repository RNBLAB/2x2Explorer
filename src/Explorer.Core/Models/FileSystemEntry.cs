using System;
using System.IO;

namespace Explorer.Core.Models;

/// <summary>
/// 목록에 한 줄로 나타나는 파일이나 폴더입니다.
/// 열거할 때 한 번 읽고 그 뒤로는 디스크를 다시 건드리지 않습니다.
/// </summary>
public sealed class FileSystemEntry
{
    public required string FullPath { get; init; }

    public required string Name { get; init; }

    public bool IsDirectory { get; init; }

    /// <summary>파일 크기(바이트)입니다. 폴더는 0입니다.</summary>
    public long Size { get; init; }

    public DateTime LastWriteTime { get; init; }

    /// <summary>만든 날짜입니다. 읽지 못하면 <see cref="DateTime.MinValue"/>입니다.</summary>
    public DateTime CreationTime { get; init; }

    public FileAttributes Attributes { get; init; }

    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;

    /// <summary>점을 포함한 확장자입니다. 폴더는 빈 문자열입니다.</summary>
    public string Extension => IsDirectory ? string.Empty : Path.GetExtension(Name);

    /// <summary>
    /// 열거 결과를 읽습니다. 속성이나 크기를 읽다 실패해도 목록에서 빠지지 않도록
    /// 실패한 값만 기본값으로 둡니다. 잠긴 파일이나 권한 없는 항목에서 이 경로를 탑니다.
    /// </summary>
    public static FileSystemEntry FromInfo(FileSystemInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var attributes = SafeRead(() => info.Attributes, FileAttributes.Normal);
        var isDirectory = (attributes & FileAttributes.Directory) != 0;

        var size = 0L;
        if (!isDirectory && info is FileInfo file)
        {
            size = SafeRead(() => file.Length, 0L);
        }

        return new FileSystemEntry
        {
            FullPath = info.FullName,
            Name = info.Name,
            IsDirectory = isDirectory,
            Size = size,
            LastWriteTime = SafeRead(() => info.LastWriteTime, DateTime.MinValue),
            CreationTime = SafeRead(() => info.CreationTime, DateTime.MinValue),
            Attributes = attributes
        };
    }

    private static T SafeRead<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return fallback;
        }
    }
}
