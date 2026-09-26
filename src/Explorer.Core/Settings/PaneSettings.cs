using System.Collections.Generic;
using Explorer.Core.Models;

namespace Explorer.Core.Settings;

/// <summary>
/// 분할 패널 하나의 설정입니다. 설정 파일의 panes 배열 항목에 대응합니다.
/// </summary>
public sealed class PaneSettings
{
    /// <summary>패널 위치입니다. 0=좌상, 1=우상, 2=좌하, 3=우하.</summary>
    public int Index { get; set; }

    /// <summary>패널 역할입니다.</summary>
    public PaneRole Role { get; set; } = PaneRole.Files;

    /// <summary>
    /// 시작 위치입니다. 환경 변수를 쓸 수 있습니다(예: %USERPROFILE%\Desktop).
    /// 경로가 없어졌으면 존재하는 상위 폴더로 거슬러 올라가 엽니다.
    /// </summary>
    public string StartPath { get; set; } = "%USERPROFILE%";

    /// <summary>시작할 때 시작 위치만 열지, 지난 탭을 모두 복원할지 정합니다.</summary>
    public PaneStartupMode StartupMode { get; set; } = PaneStartupMode.StartPath;

    /// <summary>지난 세션에 열려 있던 탭 경로입니다. StartupMode가 RestoreTabs일 때만 쓰입니다.</summary>
    public List<string> LastTabs { get; set; } = new();

    /// <summary>
    /// 지난 세션 탭들이 몇 번 즐겨찾기로 열린 것이었는지입니다. LastTabs 와 차례가 같습니다.
    /// 색은 즐겨찾기 차례에서 나오므로, 이것을 적어 두지 않으면 탭을 복원할 때 색이 사라집니다.
    /// 작업 탭은 -1 입니다.
    /// </summary>
    public List<int> LastTabColors { get; set; } = new();

    /// <summary>지난 세션에 선택돼 있던 탭의 인덱스입니다.</summary>
    public int LastActiveTab { get; set; }

    /// <summary>숨김·시스템 파일을 목록에 보일지 여부입니다.</summary>
    public bool ShowHiddenFiles { get; set; }

    /// <summary>이 패널의 정렬 기준입니다.</summary>
    public SortColumn SortColumn { get; set; } = SortColumn.Name;

    /// <summary>이 패널의 정렬 방향입니다.</summary>
    public SortDirection SortDirection { get; set; } = SortDirection.Ascending;

    /// <summary>
    /// 정렬할 때 폴더를 파일보다 먼저 모을지입니다. 바로가기(.lnk)도 폴더와 함께 모읍니다.
    /// 끄면 폴더와 파일을 가리지 않고 한 줄로 정렬합니다.
    /// </summary>
    public bool FoldersFirst { get; set; } = true;

    /// <summary>이 패널의 보기 방식입니다.</summary>
    public ViewMode ViewMode { get; set; } = ViewMode.Details;

    /// <summary>아이콘 보기에서 쓰는 아이콘 한 변의 길이(픽셀)입니다. Ctrl+휠로 바뀝니다.</summary>
    public int IconSize { get; set; } = 96;

    /// <summary>이 패널 왼쪽에 폴더 나무를 붙일지 여부입니다.</summary>
    public bool ShowTree { get; set; }

    /// <summary>이 패널에 미리보기를 붙일지 여부입니다.</summary>
    public bool ShowPreview { get; set; } = true;
}
