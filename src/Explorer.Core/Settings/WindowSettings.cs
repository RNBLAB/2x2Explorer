namespace Explorer.Core.Settings;

/// <summary>
/// 메인 창의 위치와 크기입니다. 종료할 때 저장하고 시작할 때 복원합니다.
/// </summary>
public sealed class WindowSettings
{
    /// <summary>창의 왼쪽 좌표입니다. null이면 화면 가운데에서 시작합니다.</summary>
    public double? Left { get; set; }

    /// <summary>창의 위쪽 좌표입니다. null이면 화면 가운데에서 시작합니다.</summary>
    public double? Top { get; set; }

    public double Width { get; set; } = 1280;

    public double Height { get; set; } = 800;

    public bool IsMaximized { get; set; }

    /// <summary>
    /// 윈도우가 알려 준 창 자리입니다. 화면 전체를 하나로 본 실제 픽셀입니다.
    ///
    /// 배율이 다른 모니터가 섞여 있어도 어긋나지 않고,
    /// 어느 모니터에서 최대화했는지까지 그대로 되살아납니다.
    /// 위의 Left·Top·Width·Height는 이 값이 없는 옛 설정을 위한 대비책입니다.
    /// </summary>
    public int? PlacementShow { get; set; }

    public int? PlacementLeft { get; set; }

    public int? PlacementTop { get; set; }

    public int? PlacementRight { get; set; }

    public int? PlacementBottom { get; set; }
}
