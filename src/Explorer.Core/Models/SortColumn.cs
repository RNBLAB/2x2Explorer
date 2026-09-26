namespace Explorer.Core.Models;

/// <summary>목록을 정렬하는 기준입니다.</summary>
public enum SortColumn
{
    Name,
    Size,
    LastWriteTime,
    Type,
    CreationTime
}

/// <summary>정렬 방향입니다.</summary>
public enum SortDirection
{
    Ascending,
    Descending
}
