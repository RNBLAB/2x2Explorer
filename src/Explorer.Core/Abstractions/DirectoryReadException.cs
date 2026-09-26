using System;
using System.IO;

namespace Explorer.Core.Abstractions;

/// <summary>
/// 폴더를 어느 길로도 읽지 못했을 때 던집니다.
/// 예전에는 빈 목록으로 돌려줘서 "비어 있는 폴더"와 "읽지 못한 폴더"가 화면에서 구별되지 않았습니다.
/// </summary>
public sealed class DirectoryReadException : IOException
{
    public DirectoryReadException(string path, string reason, Exception? inner)
        : base($"{path}: {reason}", inner)
    {
        Path = path;
        Reason = reason;
    }

    /// <summary>읽으려던 폴더입니다.</summary>
    public string Path { get; }

    /// <summary>상태 줄에 띄울 짧은 까닭입니다.</summary>
    public string Reason { get; }
}
