namespace Explorer.Core.Models;

/// <summary>미리보기에서 글자를 어떻게 칠할지입니다.</summary>
public enum CodeSpanKind
{
    /// <summary>주석입니다.</summary>
    Comment,

    /// <summary>따옴표로 묶은 글월입니다.</summary>
    Quoted,

    /// <summary>수입니다.</summary>
    Number,

    /// <summary>그 말의 문법이 미리 잡아 둔 낱말입니다.</summary>
    Keyword
}

/// <summary>
/// 칠할 구간 하나입니다. 글자 차례와 길이로 가리킵니다.
/// 구간끼리 겹치지 않고 앞에서 뒤로 차례대로 놓입니다.
/// </summary>
public readonly record struct CodeSpan(int Start, int Length, CodeSpanKind Kind);
