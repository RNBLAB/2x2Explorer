using System.Collections.Generic;
using Explorer.Core.Models;

namespace Explorer.Core.Settings;

/// <summary>
/// 파일 검색 설정입니다.
/// </summary>
public sealed class SearchSettings
{
    /// <summary>기본으로 사용할 검색 엔진입니다.</summary>
    public SearchProvider Provider { get; set; } = SearchProvider.Everything;

    /// <summary>Everything을 쓸 수 없을 때 폴더 열거 검색으로 대체할지 여부입니다.</summary>
    public bool FallbackToEnumerate { get; set; } = true;

    /// <summary>
    /// Everything64.dll의 경로입니다. 비워 두면 실행 파일 옆의 lib 폴더에서 찾습니다.
    /// </summary>
    public string? EverythingSdkPath { get; set; }

    /// <summary>검색 결과로 가져올 최대 건수입니다.</summary>
    public int MaxResults { get; set; } = 5000;

    /// <summary>
    /// 로컬(네트워크도 클라우드도 아닌 곳: 고정 디스크·USB 메모리 등)에 있는 결과도 보여 줄지입니다.
    /// </summary>
    public bool IncludeLocalDrives { get; set; } = true;

    /// <summary>
    /// 네트워크 드라이브에 있는 결과도 보여 줄지입니다.
    /// 네트워크 파일은 크기·날짜 한 번 읽는 데 랜선을 한 번씩 타므로 검색이 크게 느려집니다.
    /// </summary>
    public bool IncludeNetworkDrives { get; set; } = true;

    /// <summary>
    /// 클라우드(MYBOX 같은 가상 드라이브, OneDrive 같은 동기화 폴더)에 있는 결과도 보여 줄지입니다.
    /// 네트워크이면서 클라우드인 곳은 이 값만 따릅니다.
    /// </summary>
    public bool IncludeCloudDrives { get; set; } = true;

    /// <summary>
    /// 이 볼륨 레이블을 단 드라이브를 클라우드로 봅니다.
    /// MYBOX 는 드라이브 종류가 "이동식"으로 잡혀 USB 메모리와 구별되지 않으므로 레이블로 알아봅니다.
    /// 다른 가상 드라이브를 쓰면 그 레이블을 더합니다.
    /// </summary>
    public List<string> CloudDriveLabels { get; set; } = new() { "MYBOX" };

    /// <summary>
    /// 한 번에 크기·날짜를 읽어 올릴 건수입니다.
    /// 이만큼 읽을 때마다 목록에 먼저 올리고 다음 묶음으로 넘어갑니다.
    /// </summary>
    public int DescribeChunk { get; set; } = 5000;
}
