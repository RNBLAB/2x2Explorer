namespace Explorer.Core.Models;

/// <summary>
/// 파일 검색을 어느 엔진으로 수행할지 정합니다.
/// </summary>
public enum SearchProvider
{
    /// <summary>Everything SDK를 통해 색인 검색을 합니다.</summary>
    Everything,

    /// <summary>현재 폴더 아래를 직접 열거해 검색합니다. Everything을 쓸 수 없을 때의 대안입니다.</summary>
    Enumerate
}
