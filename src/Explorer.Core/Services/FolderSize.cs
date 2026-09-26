using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Explorer.Core.Services;

/// <summary>
/// 폴더가 실제로 얼마나 차지하는지 셉니다.
///
/// 폴더에는 크기라는 것이 없습니다. 안에 든 파일을 다 더해야 알 수 있고, 그래서 시간이 걸립니다.
/// 고를 때마다 자동으로 세지 않고 사람이 시킬 때만 세는 까닭입니다.
/// 세는 동안 지금까지의 합을 조금씩 알려 주고, 언제든 멈출 수 있습니다.
/// </summary>
public static class FolderSize
{
    /// <summary>이만큼 지나면 지금까지의 합을 알립니다(밀리초).</summary>
    private const int ReportEveryMs = 200;

    /// <param name="paths">폴더든 파일이든 됩니다. 파일은 제 크기만 더합니다.</param>
    /// <param name="progress">지금까지 더한 바이트 수입니다.</param>
    public static Task<long> MeasureAsync(
        IReadOnlyList<string> paths,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        if (paths.Count == 0)
        {
            return Task.FromResult(0L);
        }

        return Task.Run(() => Measure(paths, progress, cancellationToken), cancellationToken);
    }

    private static long Measure(
        IReadOnlyList<string> paths,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        var folders = new Stack<string>();
        var total = 0L;
        var clock = Stopwatch.StartNew();
        var elapsed = 0L;

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                folders.Push(path);
                continue;
            }

            total += FileLength(path);
        }

        while (folders.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var info in ReadFolder(folders.Pop()))
            {
                var attributes = SafeAttributes(info);

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    // 다른 자리를 가리키는 폴더는 따라가지 않습니다. 같은 것을 두 번 세거나 맴돌 수 있습니다.
                    if ((attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        folders.Push(info.FullName);
                    }

                    continue;
                }

                if (info is FileInfo file)
                {
                    total += SafeLength(file);
                }
            }

            if (clock.ElapsedMilliseconds - elapsed >= ReportEveryMs)
            {
                elapsed = clock.ElapsedMilliseconds;
                progress?.Report(total);
            }
        }

        progress?.Report(total);
        return total;
    }

    private static FileSystemInfo[] ReadFolder(string path)
    {
        try
        {
            return new DirectoryInfo(path).GetFileSystemInfos();
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            return Array.Empty<FileSystemInfo>();
        }
    }

    private static long FileLength(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? file.Length : 0;
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            return 0;
        }
    }

    private static long SafeLength(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            return 0;
        }
    }

    private static FileAttributes SafeAttributes(FileSystemInfo info)
    {
        try
        {
            return info.Attributes;
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            return FileAttributes.Normal;
        }
    }

    private static bool IsUnreadable(Exception ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException;
}
