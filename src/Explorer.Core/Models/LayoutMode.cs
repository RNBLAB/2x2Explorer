namespace Explorer.Core.Models;

/// <summary>
/// 창을 몇으로, 어떻게 나눌지입니다.
/// 어느 것이든 2×2 격자 위에 놓으면 그려집니다. 칸을 옆이나 아래로 이어 붙이면 3분할과 2분할이 됩니다.
/// </summary>
public enum LayoutMode
{
    /// <summary>네 칸입니다.</summary>
    Quad,

    /// <summary>위 한 칸, 아래 두 칸입니다.</summary>
    TopOneBottomTwo,

    /// <summary>위 두 칸, 아래 한 칸입니다.</summary>
    TopTwoBottomOne,

    /// <summary>왼쪽 한 칸, 오른쪽 두 칸입니다.</summary>
    LeftOneRightTwo,

    /// <summary>왼쪽 두 칸, 오른쪽 한 칸입니다.</summary>
    LeftTwoRightOne,

    /// <summary>위아래 두 칸입니다.</summary>
    TwoRows,

    /// <summary>좌우 두 칸입니다.</summary>
    TwoColumns
}
