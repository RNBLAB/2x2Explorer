using System;

namespace Explorer.Core.Models;

/// <summary>
/// 바꿀 이름에 무슨 문제가 있는지입니다. 여럿이 한꺼번에 걸릴 수 있어 겹쳐 쓸 수 있게 두었습니다.
/// </summary>
[Flags]
public enum RenameProblem
{
    None = 0,

    /// <summary>이름이 비어 있습니다.</summary>
    Empty = 1,

    /// <summary>윈도우가 파일 이름에 쓸 수 없는 글자가 들어 있습니다.</summary>
    BadCharacter = 2,

    /// <summary>같은 폴더 안에서 다른 줄과 이름이 겹칩니다.</summary>
    Duplicate = 4,

    /// <summary>그 폴더에 이미 그 이름이 있습니다.</summary>
    Exists = 8
}
