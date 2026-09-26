# 구조

## 세 프로젝트가 아래에서 위로 쌓입니다

`Explorer.Core`는 UI를 모릅니다. 설정 모델, 경로 규칙, 나중에 들어올 파일 열거·검색 추상화가 여기 있습니다. net10.0을 대상으로 해서 윈도우 없이도 돌려볼 수 있습니다.

`Explorer.Shell`은 윈도우에만 있는 기능을 감쌉니다. P/Invoke와 COM 선언이 전부 이 안에 머물러야 합니다. 앱 코드에 `DllImport`가 흩어지기 시작하면 이 경계가 무너집니다.

`Explorer.App`은 WPF 창과 뷰입니다. 두 프로젝트를 참조합니다.

## 창은 고정 2x2 Grid 하나입니다

`MainWindow.xaml`이 2행 2열 Grid를 두고 각 칸에 `PaneHost`를 하나씩 놓습니다. 칸의 역할은 `settings.json`의 `panes[].role`이 정합니다.

`PaneHost`는 모든 패널이 공유하는 껍데기입니다. 상단 바 한 줄과 본문 자리를 제공하고, 안에 무엇이 들어가는지는 모릅니다.

## 상단 바는 2열 Grid로 메뉴와 탭을 한 줄에 놓습니다

0열은 폭이 `Auto`이고 최소 폭 96을 확보합니다. 메뉴가 여기 좌측 정렬로 들어갑니다.

1열은 `*`로 남는 공간을 모두 차지하고, 탭 스트립이 우측 정렬로 들어갑니다. `ClipToBounds=True`라서 탭이 늘어나도 메뉴를 밀어내지 못합니다.

기본 `TabControl`은 쓰지 않습니다. 탭 스트립과 콘텐츠가 한 덩어리라 탭만 떼어 메뉴 옆에 붙일 수 없기 때문입니다. 3단계에서 탭 스트립은 `ItemsControl`로, 본문은 별도 `ContentPresenter`로 만듭니다.

## 도구 모음 아이콘은 윈도우 기본 글꼴을 씁니다

Segoe MDL2 Assets(Windows 10 1709 이상, 11에 기본 포함)의 글리프를 `Content`에 직접 넣습니다. 이미지 파일을 배포에 넣지 않아도 되고, 윈도우 탐색기와 같은 모양이 나옵니다.

`Explorer.App/Views/Panes/FileListView.xaml`의 단추가 쓰는 코드는 Back E72B, Forward E72A, Up E74A, NewFolder E8F4, ViewAll E8A9, Refresh E72C, Search E721입니다.

Windows 11 전용인 Segoe Fluent Icons로 갈아타려면 `ToolIconButton` 스타일의 `FontFamily` 한 줄만 바꾸면 됩니다. 글리프 코드는 대부분 그대로입니다.

## 패널 하나에 탭이 여럿, 목록 화면은 하나입니다

`PaneViewModel`이 `Tabs`(각각 `FileListViewModel`)와 `SelectedTab`을 들고 있습니다. 정렬과 보기 방식은 패널 전체의 설정이라 `PaneViewModel`이 바꾸고 모든 탭에 알립니다. 폴더·이동 이력·목록은 탭마다 따로입니다.

`FileListView`는 패널마다 하나만 만들고 `SelectedTab`에 바인딩합니다. 탭마다 화면을 만들면 탭 수만큼 목록 컨트롤이 메모리에 남습니다. 대신 탭을 오가면 스크롤 위치는 처음으로 돌아갑니다.

상단 바의 메뉴는 `PaneMenu`, 탭 스트립은 `PaneTabStrip`이고 둘 다 `PaneViewModel`을 DataContext로 받습니다.

## 설정은 한 곳에서 읽고 한 곳에서 씁니다

`JsonSettingsService`가 `%APPDATA%\Explorer\settings.json`을 읽고 씁니다. 저장은 `.tmp`에 쓴 뒤 바꿔치기해서, 쓰는 도중 전원이 나가도 기존 파일이 남습니다.

`AppSettings.Normalize()`가 읽은 값을 다듬습니다. 패널은 항상 4개, 미리보기 패널은 항상 하나, 숫자는 범위 안으로 맞춥니다. 사람이 손으로 고친 설정 파일 때문에 프로그램이 안 뜨는 상황을 막는 장치입니다.

`AppHost`가 앱이 사는 동안 설정 인스턴스 하나를 들고 있고, 창이 닫힐 때 그대로 저장합니다.

## 창 위치는 화면 밖으로 나가지 않게 보정합니다

`WindowPlacementService.Capture`는 최대화 상태에서 `RestoreBounds`를 저장합니다. 최대화된 크기를 저장하면 다음 실행에서 창이 화면을 덮습니다.

`Restore`는 저장된 좌표가 현재 화면 영역과 겹치는지 확인합니다. 겹치는 면적이 너무 작으면(모니터를 뺀 경우) 가운데로 되돌립니다.

## 파일 목록은 세 가지를 지키도록 만들었습니다

폴더 열거는 `Task.Run`으로 백그라운드에서 돌고, 다른 폴더로 옮기면 `CancellationToken`으로 이전 요청을 버립니다. 느린 네트워크 폴더를 열다 다른 곳으로 갔을 때 옛 결과가 화면을 덮어쓰는 것을 막습니다.

목록은 `VirtualizingPanel.IsVirtualizing`과 `VirtualizationMode=Recycling`으로 보이는 줄만 만듭니다. 썸네일도 같은 원리로, 줄의 `Image`가 화면에 나타날 때(`Loaded`) 비로소 요청합니다. 파일이 1만 개여도 셸 호출은 수십 건에 그칩니다.

셸 썸네일 핸들러 중에는 STA에서만 도는 것이 있어, `ShellThumbnailService`가 오래 사는 STA 작업자 스레드 몇 개로 큐를 처리합니다. 요청마다 스레드를 만들면 스크롤 한 번에 스레드가 수백 개 생깁니다.

## 썸네일 캐시는 패널 네 개가 함께 씁니다

`AppHost`가 `ThumbnailCache` 하나를 만들어 모든 패널에 넘깁니다. 패널마다 캐시를 두면 같은 파일의 썸네일을 네 번 만듭니다.

캐시는 LRU입니다. `ViewSettings.ThumbnailCacheCapacity`를 넘으면 오래 안 쓴 것부터 버립니다.

## 미리보기는 활성 패널의 선택을 따라갑니다

신호는 세 단계로 올라옵니다. 탭(`FileListViewModel`)이 `SelectionChanged`를 내고, 패널(`PaneViewModel`)이 보이는 탭에서 온 것만 골라 다시 내보내고, `MainWindow`가 활성 패널의 신호만 미리보기에 넘깁니다.

미리보기 패널을 클릭해도 활성 패널은 바뀌지 않습니다. `PaneHost`가 미리보기 역할일 때는 `Activated`를 내지 않기 때문입니다.

선택이 바뀌면 `PreviewViewModel`이 `DispatcherTimer`로 200ms 기다렸다가 읽습니다. 방향키로 목록을 훑을 때 파일을 매번 여는 것을 막습니다. 읽는 도중 선택이 또 바뀌면 이전 작업은 `CancellationToken`으로 버립니다.

`PreviewSettings.FollowActivePane`이 false이면 활성 패널을 무시하고 `FixedSourcePaneIndex` 패널에 고정합니다.

## 텍스트 인코딩은 세 번에 걸쳐 가려냅니다

BOM이 있으면 그대로 믿고, 없으면 엄격한 UTF-8로 읽어 봅니다. 실패하면 CP949입니다.

파일 앞부분만 잘라 읽으므로 마지막 글자가 중간에서 끊길 수 있습니다. 그대로 두면 UTF-8 판별이 실패해 멀쩡한 UTF-8 파일을 CP949로 읽습니다. 그래서 끝에 걸친 불완전한 바이트 열을 먼저 떼어 냅니다.

## 앞으로 붙을 자리

| 기능 | 들어갈 곳 | 단계 |
|---|---|---|
| ~~폴더 열거·가상화 목록~~ | `Explorer.App/Views/Panes/FileListView` | 완료 |
| ~~썸네일~~ | `Explorer.Shell/Thumbnails` + `Explorer.App/Services/ThumbnailCache` | 완료 |
| 메뉴·탭 스트립 | `PaneHost`의 MenuHost / TabStripHost | 3 |
| 미리보기 뷰어 | `Explorer.App/ViewModels/PreviewViewModel` | 이미지·텍스트 완료 |
| 파일 조작·우클릭 | `Explorer.Shell/Interop` | 6 |
| Everything 검색 | `Explorer.Shell/Search` | 6 |
