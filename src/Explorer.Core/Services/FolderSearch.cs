using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Explorer.Core.Models;

namespace Explorer.Core.Services;

/// <summary>
/// 폴더를 직접 훑어 이름이 맞는 것을 찾습니다.
///
/// Everything 이 꺼져 있을 때 쓰는 길입니다. 색인이 없으니 훑는 만큼 걸립니다.
/// 그래서 온 컴퓨터가 아니라 지금 보고 있는 폴더 아래만 봅니다.
/// 찾는 대로 조금씩 올려 주므로 다 끝나기를 기다리지 않아도 됩니다.
/// </summary>
public static class FolderSearch
{
    /// <summary>한 번에 올릴 개수입니다.</summary>
    private const int BatchSize = 500;

    /// <summary>이만큼 지나면 덜 찼어도 올립니다(밀리초).</summary>
    private const int ReportEveryMs = 250;

    /// <param name="root">여기 아래를 훑습니다.</param>
    /// <param name="term">찾을 이름입니다. * 와 ? 를 쓸 수 있습니다.</param>
    /// <param name="includeHidden">숨김 항목까지 볼지입니다.</param>
    /// <param name="maxResults">이만큼 찾으면 그만둡니다.</param>
    /// <param name="progress">찾은 것을 조금씩 받습니다.</param>
    /// <returns>찾은 개수입니다.</returns>
    public static Task<int> SearchAsync(
        string root,
        string term,
        bool includeHidden,
        int maxResults,
        IProgress<IReadOnlyList<FileSystemEntry>>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(term))
        {
            return Task.FromResult(0);
        }

        return Task.Run(
            () => Walk(root, term, includeHidden, Math.Max(maxResults, 1), progress, cancellationToken),
            cancellationToken);
    }

    private static int Walk(
        string root,
        string term,
        bool includeHidden,
        int maxResults,
        IProgress<IReadOnlyList<FileSystemEntry>>? progress,
        CancellationToken cancellationToken)
    {
        var folders = new Stack<string>();
        folders.Push(root);

        var batch = new List<FileSystemEntry>(BatchSize);
        var clock = Stopwatch.StartNew();
        var elapsed = 0L;
        var total = 0;

        while (folders.Count > 0 && total < maxResults)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var info in ReadFolder(folders.Pop()))
            {
                var entry = FileSystemEntry.FromInfo(info);

                if (!includeHidden && IsTucked(entry.Attributes))
                {
                    continue;
                }

                // 바로가기 같은 자리를 따라 들어가면 같은 곳을 맴돌 수 있습니다.
                if (entry.IsDirectory && (entry.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    folders.Push(entry.FullPath);
                }

                if (!Matches(entry.Name, term))
                {
                    continue;
                }

                batch.Add(entry);
                total++;

                if (total >= maxResults)
                {
                    break;
                }
            }

            if (batch.Count >= BatchSize || (batch.Count > 0 && clock.ElapsedMilliseconds - elapsed >= ReportEveryMs))
            {
                elapsed = clock.ElapsedMilliseconds;
                progress?.Report(batch);
                batch = new List<FileSystemEntry>(BatchSize);
            }
        }

        if (batch.Count > 0)
        {
            progress?.Report(batch);
        }

        return total;
    }

    /// <summary>
    /// 폴더 하나를 읽습니다. 들여다볼 수 없는 폴더는 비어 있는 것으로 둡니다.
    /// 한 번에 다 받아 두는 것은, 훑는 도중에 권한 때문에 끊기면 그 폴더가 통째로 날아가기 때문입니다.
    /// </summary>
    private static FileSystemInfo[] ReadFolder(string path)
    {
        try
        {
            return new DirectoryInfo(path).GetFileSystemInfos();
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or ArgumentException
                                       or NotSupportedException
                                       or System.Security.SecurityException)
        {
            return Array.Empty<FileSystemInfo>();
        }
    }

    /// <summary>
    /// 탐색기처럼 숨김 속성만 봅니다. 숨김+시스템 항목(System Volume Information 등)도 여기서 함께 빠집니다.
    /// 시스템 속성만 있는 항목(예: MYBOX N:\ 의 폴더)은 찾고, 그 아래로도 내려갑니다.
    /// 목록(DirectoryEnumerator)·팝업 탐색기와 같은 규칙입니다.
    /// </summary>
    private static bool IsTucked(FileAttributes attributes) =>
        (attributes & FileAttributes.Hidden) != 0;

    /// <summary>
    /// 이름이 찾는 말에 맞는지 봅니다.
    /// * 나 ? 가 없으면 그 말이 이름 어딘가에 들어 있기만 하면 됩니다. 탐색기와 같은 셈입니다.
    /// </summary>
    public static bool Matches(string name, string term)
    {
        if (string.IsNullOrEmpty(term))
        {
            return true;
        }

        if (term.IndexOf('*') < 0 && term.IndexOf('?') < 0)
        {
            return name.Contains(term, StringComparison.OrdinalIgnoreCase);
        }

        return Glob(name, ToPartial(term));
    }

    /// <summary>
    /// 물음표만 든 검색어를 부분 일치로 바꿉니다.
    ///
    /// 물음표를 쓰면 이름 전체가 그 꼴에 맞아야 한다는 규칙이 붙습니다.
    /// 그래서 "정?사업"은 "정부사업계획.hwp" 를 찾지 못합니다. 앞뒤를 별표로 감싸 이 턱을 없앱니다.
    ///
    /// 별표를 직접 친 검색어는 손대지 않습니다. 별표를 쓴 사람은 어디서 시작하고 끝나는지를
    /// 스스로 정한 것이라, 여기서 또 감싸면 "*.txt" 가 "메모.txtx" 까지 잡아 확장자 거르개
    /// 노릇을 못 하게 됩니다.
    ///
    /// 따옴표나 ext: 같은 Everything 의 말이 섞인 검색어도 손대지 않습니다.
    /// </summary>
    public static string ToPartial(string? term)
    {
        var trimmed = (term ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        // 와일드카드가 없으면 어차피 들어 있기만 하면 맞는 검색입니다.
        if (trimmed.IndexOf('*') < 0 && trimmed.IndexOf('?') < 0)
        {
            return trimmed;
        }

        // 별표를 직접 쓴 검색어는 그대로 둡니다.
        if (trimmed.IndexOf('*') >= 0)
        {
            return trimmed;
        }

        // 빈칸·따옴표·콜론이 있으면 여러 낱말이나 조건을 적은 것입니다. 감싸면 뜻이 달라집니다.
        if (trimmed.IndexOf(' ') >= 0 || trimmed.IndexOf('"') >= 0 || trimmed.IndexOf(':') >= 0)
        {
            return trimmed;
        }

        return "*" + trimmed + "*";
    }

    /// <summary>
    /// * 와 ? 만 아는 간단한 맞춤입니다.
    /// 되돌아갈 자리를 하나만 기억해 두는 방식이라 뒤로 물러서며 다시 볼 필요가 없습니다.
    /// </summary>
    private static bool Glob(string name, string pattern)
    {
        var n = 0;
        var p = 0;
        var star = -1;
        var mark = 0;

        while (n < name.Length)
        {
            if (p < pattern.Length
                && (pattern[p] == '?'
                    || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(name[n])))
            {
                n++;
                p++;
                continue;
            }

            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p;
                mark = n;
                p++;
                continue;
            }

            if (star < 0)
            {
                return false;
            }

            // 별표가 한 글자를 더 먹은 셈 치고 다시 맞춰 봅니다.
            p = star + 1;
            mark++;
            n = mark;
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
