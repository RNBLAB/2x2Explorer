# 2x2 Explorer

**한국어** · [English](README.en.md)

네 칸으로 나뉜 윈도우용 파일 탐색기입니다. 칸마다 탭과 미리보기가 따로 있고, 파일을 옮기다 말고 이름을 한꺼번에 바꾸거나 그림·PDF를 고치는 일을 같은 창 안에서 합니다.

개인이 쓰려고 만든 도구를 공개한 것입니다. 손볼 곳이 아직 많습니다.

## 무엇을 하나

**네 칸 배치** — 2x2 고정입니다. 칸마다 탭 줄, 주소 표시줄, 도구 모음, 미리보기가 따로 있습니다. 배치는 설정 창에서 2분할·3분할로도 바꿉니다.

**미리보기** — 그림과 글자는 직접 그립니다(문법 강조 포함). PDF, 한글(.hwp/.hwpx), 오피스 문서는 윈도우에 설치된 미리보기 핸들러에 맡깁니다. Ctrl+휠로 확대·축소하고 휠로 넘깁니다.

**일괄 이름 바꾸기(베타)** — 표에 붙여 넣어 고치고, 찾아 바꾸기·연번·요소 정규화(날짜·프로젝트명·문서번호·작성자 따위를 알아서 나눠 다시 조립)를 합니다. 되돌리기가 있습니다.

**그림 편집** — 자르기, 회전(90° 단위는 무손실), 기울기 보정, 크기 조절, 형식 변환. 여러 장을 한꺼번에 처리하는 창도 따로 있습니다.

**PDF 편집** — 쪽 순서 바꾸기, 빼기, 합치기, 그림으로 뽑기.

**일괄 인쇄** — 고른 파일들을 한 번에 보냅니다.

**셸 통합** — 복사·이동·삭제를 윈도우의 `IFileOperation`에 맡기므로 진행률 창, 이름 충돌 대화상자, 휴지통, 되돌리기가 그대로 동작합니다. 우클릭 메뉴, 바로가기(.lnk), 속성 창도 윈도우 것을 씁니다.

**검색** — [Everything](https://www.voidtools.com/)이 돌고 있으면 그쪽에 묻고, 없으면 폴더를 훑습니다.

**테마** — 윈도우의 밝게·어둡게 설정을 따라가고, 실행 중에 바뀌어도 반영합니다.

## 요구 사항

- Windows 10 1809 이상 (x64)
- .NET 10 SDK — 빌드에 필요합니다. 실행 환경(런타임)만으로는 빌드되지 않습니다.
- 첫 빌드에는 인터넷 — PDF 편집에 쓰는 [PDFsharp](https://github.com/empira/PDFsharp)(MIT)를 NuGet에서 한 번 받아 옵니다. 그 뒤로는 없어도 됩니다.

## 받아서 실행하기

### 1. .NET 10 SDK 설치

```cmd
winget install Microsoft.DotNet.SDK.10
```

또는 <https://dotnet.microsoft.com/download/dotnet/10.0> 에서 **SDK x64** 설치 파일을 받습니다. 설치가 끝나면 명령 창을 새로 열어야 인식됩니다.

```cmd
dotnet --list-sdks
```

`10.`으로 시작하는 줄이 나오면 됩니다.

### 2. 내려받아 실행

```cmd
git clone https://github.com/RNBLAB/2x2Explorer.git
cd 2x2Explorer
start.cmd
```

`start.cmd`는 빌드한 뒤 실행하고, 그 사이에 나온 글을 모두 `run.log`에 남깁니다. 탐색기에서 더블클릭해도 됩니다.

명령으로 바로 실행하려면:

```cmd
dotnet run --project src\Explorer.App\Explorer.App.csproj
```

Visual Studio를 쓴다면 `2x2Explorer.sln`을 열고 `Explorer.App`을 시작 프로젝트로 지정한 뒤 F5입니다.

> `start.cmd`는 빌드로 만들어지는 `Explorer.exe` 대신 `dotnet Explorer.dll`로 실행합니다. 서명 없는 실행 파일을 윈도우 보호 기능이 막는 경우가 있어서입니다.

## 시험

```cmd
test.cmd
```

`Explorer.Core`의 규칙들을 한 번에 검사하고 결과를 화면과 `test.log`에 남깁니다. 바깥에서 받아 오는 꾸러미가 없어 인터넷 없이도 그 자리에서 돕니다.

## 설정 파일

처음 실행하면 `%APPDATA%\2x2Explorer\settings.json`이 만들어집니다. 본보기는 [`docs/settings.sample.json`](docs/settings.sample.json)에 있습니다.

칸마다 시작 위치와 시작 방식(`startPath` / `restoreTabs`)을 여기서 정합니다. 파일이 깨져 있으면 `.broken-<시각>`으로 밀어 두고 기본값으로 뜹니다.

## 프로젝트 구성

| 프로젝트 | 대상 | 맡는 일 |
|---|---|---|
| `Explorer.Core` | net10.0 | 설정 모델, 경로·이름 규칙, 정렬·검색. UI를 모릅니다 |
| `Explorer.Shell` | net10.0-windows | 윈도우 셸 P/Invoke — 파일 조작, 썸네일, 미리보기 핸들러, 인쇄, 휴지통 |
| `Explorer.App` | net10.0-windows (WPF) | 창, 칸, 편집 창 |

`Explorer.Core`가 UI에 기대지 않는 것이 규칙입니다. 덕분에 설정과 이름 규칙을 콘솔에서 그대로 시험할 수 있습니다. `DllImport`는 `Explorer.Shell` 밖으로 나가지 않습니다.

## 문서

- [`docs/decisions.md`](docs/decisions.md) — 확정된 설계 결정과 그 이유
- [`docs/architecture.md`](docs/architecture.md) — 구조와 각 계층의 역할
- [`docs/roadmap.md`](docs/roadmap.md) — 단계별 구현 계획과 현재 위치
- [`docs/releasing.md`](docs/releasing.md) — 버전 번호 규칙과 패치노트 쓰는 법
- [`CHANGELOG.md`](CHANGELOG.md) — 판마다 무엇이 바뀌었는지

## 알려진 제약

- 큰 아이콘 보기는 가상화가 되지 않아 파일이 수천 개인 폴더에서 느려집니다.
- 한글 미리보기는 한컴 오피스의 미리보기 핸들러에 기댑니다. 설치돼 있지 않으면 문서에 담긴 첫 쪽 그림으로 물러납니다.
- 일괄 이름 바꾸기는 베타입니다. 설정 창에서 끌 수 있습니다.
- 오피스 미리보기는 핸들러가 WPF 바깥에 그리므로 그 위에 다른 UI를 겹칠 수 없습니다.

## 라이선스

Apache License 2.0. 전문은 [`LICENSE`](LICENSE)에, 저작권 표시와 제3자 꾸러미 목록은 [`NOTICE`](NOTICE)에 있습니다.

Copyright 2026 RNBLAB
