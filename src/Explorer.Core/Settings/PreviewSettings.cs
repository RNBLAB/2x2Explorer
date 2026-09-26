namespace Explorer.Core.Settings;

/// <summary>
/// 미리보기 패널의 동작 설정입니다.
/// </summary>
public sealed class PreviewSettings
{
    /// <summary>
    /// 마지막으로 포커스를 받은 패널의 선택을 따라갈지 여부입니다.
    /// false이면 <see cref="FixedSourcePaneIndex"/> 패널만 따라갑니다.
    /// </summary>
    public bool FollowActivePane { get; set; } = true;

    /// <summary>FollowActivePane이 false일 때 미리보기가 바라볼 패널 인덱스입니다.</summary>
    public int FixedSourcePaneIndex { get; set; }

    /// <summary>
    /// 선택이 바뀐 뒤 실제로 파일을 읽기까지 기다리는 시간(밀리초)입니다.
    /// 방향키로 목록을 훑을 때 파일을 매번 여는 것을 막습니다.
    /// </summary>
    public int DebounceMs { get; set; } = 100;

    /// <summary>텍스트 미리보기로 읽어 들일 최대 크기(바이트)입니다.</summary>
    public long MaxTextPreviewBytes { get; set; } = 2L * 1024 * 1024;

    /// <summary>미리보기 대상이 이 크기를 넘으면 자동으로 열지 않고 확인을 받습니다.</summary>
    public long ConfirmAboveBytes { get; set; } = 256L * 1024 * 1024;
}
