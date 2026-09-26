namespace Explorer.Core.Models;

/// <summary>파일 목록을 그리는 방식입니다.</summary>
public enum ViewMode
{
    /// <summary>이름·크기·날짜·유형을 열로 보여 줍니다.</summary>
    Details,

    /// <summary>작은 아이콘과 이름만 한 줄씩 보여 줍니다.</summary>
    List,

    /// <summary>큰 미리보기 그림을 격자로 늘어놓습니다.</summary>
    LargeIcons
}
