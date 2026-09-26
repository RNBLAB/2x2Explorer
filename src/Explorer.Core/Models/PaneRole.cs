namespace Explorer.Core.Models;

/// <summary>
/// 분할 패널 하나가 맡는 역할입니다.
/// </summary>
public enum PaneRole
{
    /// <summary>폴더 내용을 보여주는 일반 파일 패널입니다.</summary>
    Files,

    /// <summary>선택된 파일을 미리 보여주는 패널입니다. 창 전체에서 하나만 존재합니다.</summary>
    Preview,

    /// <summary>
    /// 찾기만 하는 패널입니다. 탭 없이 검색어 칸과 결과 목록만 둡니다.
    /// 찾은 것을 열거나 오른쪽 단추 메뉴를 쓰는 것은 탐색기 칸과 같습니다.
    /// </summary>
    Search
}
