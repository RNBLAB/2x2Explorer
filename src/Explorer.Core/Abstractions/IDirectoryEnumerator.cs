using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Explorer.Core.Models;

namespace Explorer.Core.Abstractions;

/// <summary>폴더 내용을 읽어 오는 창구입니다.</summary>
public interface IDirectoryEnumerator
{
    /// <summary>
    /// 폴더 한 단계를 읽습니다. 반드시 백그라운드에서 수행되고,
    /// 다른 폴더로 이동하면 <paramref name="cancellationToken"/>으로 중간에 버립니다.
    /// </summary>
    Task<IReadOnlyList<FileSystemEntry>> EnumerateAsync(
        string path,
        bool includeHidden,
        CancellationToken cancellationToken);
}
