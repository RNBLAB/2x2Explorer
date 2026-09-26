namespace Explorer.Core.Models;

/// <summary>
/// 패널이 시작할 때 무엇을 여는지 정합니다.
/// </summary>
public enum PaneStartupMode
{
    /// <summary>지정한 시작 위치 하나만 엽니다. 기본값입니다.</summary>
    StartPath,

    /// <summary>지난 세션에 열려 있던 탭을 모두 복원합니다.</summary>
    RestoreTabs
}
