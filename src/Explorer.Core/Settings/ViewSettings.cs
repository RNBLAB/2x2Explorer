using System.Collections.Generic;

namespace Explorer.Core.Settings;

/// <summary>목록의 겉모습과 썸네일 관련 설정입니다.</summary>
public sealed class ViewSettings
{
    /// <summary>목록 한 줄에 들어가는 썸네일의 한 변 길이(픽셀)입니다.</summary>
    public int ThumbnailSize { get; set; } = 20;

    /// <summary>큰 아이콘 보기에서 쓰는 썸네일의 한 변 길이(픽셀)입니다.</summary>
    public int LargeThumbnailSize { get; set; } = 96;

    /// <summary>메모리에 들고 있을 썸네일 개수입니다. 넘치면 오래된 것부터 버립니다.</summary>
    public int ThumbnailCacheCapacity { get; set; } = 2000;

    /// <summary>
    /// 썸네일을 동시에 몇 개까지 만들지 정합니다.
    /// 느린 파일 하나가 나머지를 막지 않으려면 여유가 있어야 합니다.
    /// </summary>
    public int ThumbnailConcurrency { get; set; } = 4;

    /// <summary>
    /// 창 위쪽 메뉴 줄을 보일지 여부입니다.
    /// 평소에는 접어 두고 왼쪽 위 단추로 필요할 때만 펼칩니다.
    /// </summary>
    public bool ShowMenuBar { get; set; }

    /// <summary>
    /// 무슨 일이 있었는지 파일에 적어 둘지 여부입니다.
    /// 평소에는 꺼 둡니다. 무언가 이상할 때만 켜서 기록을 남깁니다.
    /// </summary>
    public bool WriteDiagnosticLog { get; set; }

    /// <summary>각 패널 아래에 폴더·파일 개수와 크기를 보여 주는 줄을 둘지 여부입니다.</summary>
    public bool ShowStatusBar { get; set; } = true;

    /// <summary>
    /// 자세히 보기의 열 구성입니다. 순서·너비·보임 여부를 기억합니다.
    /// 네 패널이 같은 구성을 씁니다.
    /// </summary>
    public List<ColumnSettings> DetailColumns { get; set; } = new();

    /// <summary>
    /// 편집 창(PDF·그림·일괄 편집·일괄 인쇄·일괄 이름 바꾸기)의 탐색기를 왼쪽에 둘지입니다.
    /// 기본은 오른쪽입니다. 탐색기의 배치 전환 단추로 바꾸고, 모든 편집 창이 같은 값을 씁니다.
    /// (한때 "오른쪽에 둘지"로 저장했으나 기본값을 오른쪽으로 되돌리면서 이름을 바꿨습니다.
    /// 예전 값은 읽지 않으므로 모두 기본값인 오른쪽에서 다시 시작합니다.)
    /// </summary>
    public bool PopupExplorerOnLeft { get; set; }

    /// <summary>
    /// 일괄 이름 바꾸기(베타)를 켤지입니다. 켜면 제목 표시줄(인쇄와 설정 사이)에 단추가 보이고 Ctrl+Shift+R 이 듣습니다.
    /// 기능이 아직 불완전해 설정 창 [일반]에서 끌 수 있게 둡니다(사용자 지정 2026-09-26).
    /// </summary>
    public bool ShowBatchRename { get; set; } = true;

    /// <summary>폴더 변경을 감지한 뒤 새로고침까지 기다리는 시간(밀리초)입니다.</summary>
    public int WatchDebounceMs { get; set; } = 600;
}
