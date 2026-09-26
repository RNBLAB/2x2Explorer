namespace Explorer.Core.Models;

/// <summary>
/// 끌어다 놓았을 때 무엇을 할지입니다.
/// 윈도우 탐색기가 Ctrl·Shift·Alt 로 가르는 것과 같은 세 가지입니다.
/// </summary>
public enum DropAction
{
    /// <summary>복사합니다. Ctrl 을 누른 채 놓은 경우입니다.</summary>
    Copy,

    /// <summary>옮깁니다. Shift 를 누른 채 놓은 경우입니다.</summary>
    Move,

    /// <summary>바로가기를 만듭니다. Alt 또는 Ctrl+Shift 를 누른 채 놓은 경우입니다.</summary>
    Link
}
