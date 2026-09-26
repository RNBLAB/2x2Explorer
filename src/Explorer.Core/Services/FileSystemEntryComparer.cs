using System;
using System.Collections.Generic;
using Explorer.Core.Models;

namespace Explorer.Core.Services;

/// <summary>
/// 목록 정렬 규칙입니다.
///
/// 폴더 먼저로 두면 방향과 무관하게 폴더가 늘 파일보다 위에 옵니다.
/// 윈도우 탐색기와 같은 동작이고, 이게 없으면 내림차순에서 폴더가 바닥으로 내려갑니다.
/// 바로가기(.lnk)는 폴더 쪽에 함께 모읍니다. 대개 다른 폴더로 건너가는 문이라 폴더와 나란히 있는 편이 찾기 쉽습니다.
/// 폴더 먼저를 끄면 폴더·파일·바로가기를 가리지 않고 한 줄로 정렬합니다.
/// </summary>
public sealed class FileSystemEntryComparer : IComparer<FileSystemEntry>
{
    private readonly SortColumn _column;
    private readonly SortDirection _direction;
    private readonly bool _directoriesFirst;

    public FileSystemEntryComparer(SortColumn column, SortDirection direction, bool directoriesFirst = true)
    {
        _column = column;
        _direction = direction;
        _directoriesFirst = directoriesFirst;
    }

    public int Compare(FileSystemEntry? x, FileSystemEntry? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        if (_directoriesFirst)
        {
            var xFront = IsFolderLike(x);
            var yFront = IsFolderLike(y);

            if (xFront != yFront)
            {
                return xFront ? -1 : 1;
            }
        }

        var result = _column switch
        {
            SortColumn.Size => x.Size.CompareTo(y.Size),
            SortColumn.LastWriteTime => x.LastWriteTime.CompareTo(y.LastWriteTime),
            SortColumn.CreationTime => x.CreationTime.CompareTo(y.CreationTime),
            SortColumn.Type => string.Compare(x.Extension, y.Extension, StringComparison.OrdinalIgnoreCase),
            _ => 0
        };

        // 같은 값이면 이름으로 가릅니다. 정렬이 새로고침마다 뒤바뀌는 것을 막습니다.
        if (result == 0)
        {
            result = NaturalStringComparer.Instance.Compare(x.Name, y.Name);
        }

        return _direction == SortDirection.Descending ? -result : result;
    }

    /// <summary>폴더 쪽에 모을 것인지입니다. 폴더와 바로가기입니다.</summary>
    private static bool IsFolderLike(FileSystemEntry entry) =>
        entry.IsDirectory
        || string.Equals(entry.Extension, ".lnk", StringComparison.OrdinalIgnoreCase);
}
