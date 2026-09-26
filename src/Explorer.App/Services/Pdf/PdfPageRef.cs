using System;
using System.IO;

namespace Explorer.App.Services.Pdf;

/// <summary>
/// 편집 중인 문서의 한 쪽이 어디서 왔는지입니다.
/// PDF 파일의 몇째 쪽이거나(<see cref="PageIndex"/> 0 이상), 그림 파일 한 장입니다(-1).
/// 문서는 이 목록일 뿐이고, 저장할 때 원본에서 쪽을 가져와 새 PDF 를 만듭니다.
/// 그래서 병합·삭제를 해도 원본 파일은 건드리지 않습니다.
/// </summary>
public sealed record PdfPageRef(string Path, int PageIndex)
{
    public bool IsImage => PageIndex < 0;

    public static PdfPageRef Image(string path) => new(path, -1);

    public string Describe() => IsImage
        ? System.IO.Path.GetFileName(Path)
        : $"{System.IO.Path.GetFileName(Path)} · {PageIndex + 1}쪽";

    public static bool IsPdf(string path) =>
        string.Equals(System.IO.Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);
}
