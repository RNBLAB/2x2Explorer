namespace Explorer.Core.Models;

/// <summary>일괄 인쇄에서 그 파일을 어떻게 다룰지입니다.</summary>
public enum PrintItemKind
{
    /// <summary>우리가 직접 그려서 찍습니다. 용지와 방향과 배율을 우리가 정합니다.</summary>
    Image,

    /// <summary>연결된 프로그램에 맡깁니다. 용지와 배율은 그 프로그램 설정을 따릅니다.</summary>
    Document,

    /// <summary>찍을 수 없는 것입니다.</summary>
    Unsupported
}
