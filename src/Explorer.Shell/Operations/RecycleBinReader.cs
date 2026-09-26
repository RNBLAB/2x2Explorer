using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using Explorer.Core.Services;

namespace Explorer.Shell.Operations;

/// <summary>
/// 휴지통에 든 것을 직접 읽습니다.
///
/// 휴지통은 지운 것마다 파일 둘을 둡니다.
/// $R 로 시작하는 파일이 내용이고, 짝이 되는 $I 파일에 원래 이름과 지운 때가 적혀 있습니다.
/// 셸에게 묻지 않고 이 두 파일을 읽어 목록을 세웁니다.
/// </summary>
[SupportedOSPlatform("windows")]
public static class RecycleBinReader
{
    /// <summary>휴지통에 든 것 하나입니다.</summary>
    public sealed record Item(
        string RecycledPath,
        string MetaPath,
        string OriginalPath,
        string Name,
        long Size,
        DateTime DeletedAt,
        bool IsDirectory);

    /// <summary>
    /// 모든 드라이브의 휴지통을 읽습니다.
    /// 다른 사용자의 휴지통은 들여다볼 수 없으므로 저절로 빠집니다.
    /// </summary>
    public static IReadOnlyList<Item> Read()
    {
        var items = new List<Item>();

        DriveInfo[] drives;

        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return items;
        }

        foreach (var drive in drives)
        {
            if (!SafeIsReady(drive))
            {
                continue;
            }

            var root = Path.Combine(drive.RootDirectory.FullName, "$RECYCLE.BIN");

            foreach (var folder in SafeDirectories(root))
            {
                ReadFolder(folder, items);
            }
        }

        return items;
    }

    /// <summary>
    /// 휴지통 항목을 아주 지웁니다. 내용($R)과 짝 파일($I)을 함께 지웁니다.
    /// 짝 파일을 남기면 내용 없는 유령 항목이 휴지통에 남습니다.
    /// </summary>
    public static int DeleteForever(IReadOnlyList<string> recycledPaths)
    {
        ArgumentNullException.ThrowIfNull(recycledPaths);

        var done = 0;

        foreach (var path in recycledPaths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
                else
                {
                    continue;
                }

                var meta = MetaPathOf(path);

                if (meta is not null && File.Exists(meta))
                {
                    File.Delete(meta);
                }

                done++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.WriteAlways($"휴지통에서 지우지 못했습니다: {path} {ex.Message}");
            }
        }

        return done;
    }

    /// <summary>$R 자리에 짝지어진 $I 파일의 자리입니다.</summary>
    private static string? MetaPathOf(string recycledPath)
    {
        var name = Path.GetFileName(recycledPath);
        var folder = Path.GetDirectoryName(recycledPath);

        if (folder is null || name.Length < 3 || !name.StartsWith("$R", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.Combine(folder, "$I" + name[2..]);
    }

    private static void ReadFolder(string folder, List<Item> items)
    {
        foreach (var meta in SafeFiles(folder, "$I*"))
        {
            var name = Path.GetFileName(meta);

            if (name.Length < 3)
            {
                continue;
            }

            // $I 와 $R 은 뒤의 이름이 같습니다. 앞 두 글자만 바꿔 짝을 찾습니다.
            var recycled = Path.Combine(folder, "$R" + name[2..]);
            var isDirectory = Directory.Exists(recycled);

            if (!isDirectory && !File.Exists(recycled))
            {
                // 내용은 없고 딱지만 남은 자리입니다. 보여 줄 것이 없습니다.
                continue;
            }

            if (Parse(meta) is not { } parsed)
            {
                continue;
            }

            items.Add(new Item(
                recycled,
                meta,
                parsed.Path,
                Path.GetFileName(parsed.Path.TrimEnd(Path.DirectorySeparatorChar)),
                parsed.Size,
                parsed.DeletedAt,
                isDirectory));
        }
    }

    /// <summary>
    /// $I 파일을 읽습니다.
    /// 앞 여덟 바이트가 판 번호, 다음 여덟이 크기, 다음 여덟이 지운 때입니다.
    /// 그 뒤가 원래 경로인데, 판 1은 길이가 520바이트로 고정이고 판 2는 앞에 글자 수가 붙습니다.
    /// </summary>
    private static (string Path, long Size, DateTime DeletedAt)? Parse(string metaPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(metaPath);

            if (bytes.Length < 24)
            {
                return null;
            }

            var version = BitConverter.ToInt64(bytes, 0);
            var size = BitConverter.ToInt64(bytes, 8);
            var stamp = BitConverter.ToInt64(bytes, 16);

            var deletedAt = DateTime.MinValue;

            if (stamp > 0)
            {
                try
                {
                    deletedAt = DateTime.FromFileTime(stamp);
                }
                catch (ArgumentOutOfRangeException)
                {
                }
            }

            string path;

            if (version >= 2)
            {
                if (bytes.Length < 28)
                {
                    return null;
                }

                var characters = BitConverter.ToInt32(bytes, 24);

                if (characters <= 0 || 28 + (characters * 2) > bytes.Length)
                {
                    return null;
                }

                path = Encoding.Unicode.GetString(bytes, 28, characters * 2);
            }
            else
            {
                if (bytes.Length < 24 + 520)
                {
                    return null;
                }

                path = Encoding.Unicode.GetString(bytes, 24, 520);
            }

            // 문자열 끝의 널 문자를 떼어 냅니다.
            var end = path.IndexOf('\0');

            if (end >= 0)
            {
                path = path[..end];
            }

            return path.Length == 0 ? null : (path, size, deletedAt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static bool SafeIsReady(DriveInfo drive)
    {
        try
        {
            return drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string[] SafeDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Array.Empty<string>();
        }
    }

    private static string[] SafeFiles(string path, string pattern)
    {
        try
        {
            return Directory.GetFiles(path, pattern);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Array.Empty<string>();
        }
    }
}
