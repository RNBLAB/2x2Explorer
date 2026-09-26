using System;

namespace Explorer.Core.Services;

/// <summary>
/// 고정 탭에 붙는 색입니다.
///
/// 즐겨찾기 차례대로 하나씩 물려 줍니다. 열 개를 넘어가면 처음으로 돌아갑니다.
/// 색상환을 고르게 도는 열 가지라, 나란히 놓아도 이웃과 갈립니다.
/// </summary>
public static class TabPalette
{
    private static readonly string[] Colors =
    {
        "#D96D5F", // 빨강
        "#D9A46D", // 주황
        "#D9D96D", // 노랑
        "#64C59E", // 초록
        "#6DD9D3", // 청록
        "#6DB3D9", // 하늘
        "#6D8CD9", // 파랑
        "#8B6DD9", // 남보라
        "#B06DD9", // 자주
        "#D96DA4"  // 분홍
    };

    public static int Count => Colors.Length;

    /// <summary>그 차례의 색입니다. 차례가 음수이면 색을 주지 않습니다.</summary>
    public static string? At(int index) =>
        index < 0 ? null : Colors[index % Colors.Length];
}
