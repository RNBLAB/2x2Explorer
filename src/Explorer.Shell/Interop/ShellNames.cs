using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Explorer.Core.Services;

namespace Explorer.Shell.Interop;

/// <summary>
/// 셸이 쓰는 이름을 그대로 얻어 옵니다.
///
/// 드라이브 이름을 직접 짜 맞추면 탐색기와 달라집니다.
/// 탐색기는 "로컬 디스크 (C:)"처럼 보여 주는데, 볼륨 레이블만 읽으면 "새 볼륨"이 나옵니다.
/// 셸에게 물으면 탐색기에 나오는 그 이름이 그대로 옵니다.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ShellNames
{
    /// <summary>보여 주는 이름을 달라는 표시입니다.</summary>
    private const uint WantDisplayName = 0x000000200;

    /// <summary>파일이 실제로 있는지 따지지 말라는 표시입니다.</summary>
    private const uint DoNotTouchDisk = 0x000000010;

    private const int FileAttributeDirectory = 0x00000010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        int attributes,
        ref FileInfo info,
        int size,
        uint flags);

    /// <summary>
    /// 드라이브 이름을 담아 둡니다.
    /// 드라이브는 이름을 물을 때 디스크를 만져야 하는데, 네트워크 드라이브는 그 한 번이 오래 걸립니다.
    /// 한 번 물어본 것은 프로그램이 도는 동안 그대로 씁니다. 드라이브 이름은 좀처럼 바뀌지 않습니다.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> DriveNames =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>탐색기에 나오는 그 이름입니다. 얻지 못하면 null입니다.</summary>
    public static string? GetDisplayName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (IsDriveRoot(path))
        {
            return DriveNames.GetOrAdd(path.Trim(), Ask);
        }

        return Ask(path);
    }

    private static string? Ask(string path)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var info = default(FileInfo);

            // 드라이브는 디스크를 만져 봐야 이름을 알 수 있습니다.
            // 만지지 말라고 하면 셸이 볼륨 레이블도, 레지스트리에 적어 둔 이름도 읽지 못해
            // "새 볼륨 (X:)" 같은 맹탕 이름이 돌아옵니다.
            var flags = IsDriveRoot(path) ? WantDisplayName : WantDisplayName | DoNotTouchDisk;

            var result = SHGetFileInfo(
                path,
                FileAttributeDirectory,
                ref info,
                Marshal.SizeOf<FileInfo>(),
                flags);

            // 오래 걸리는 것은 거의 네트워크 드라이브입니다. 얼마나 걸렸는지 남겨 둡니다.
            if (watch.ElapsedMilliseconds > 100)
            {
                DiagnosticLog.Write($"셸에게 이름 묻기 {path} {watch.ElapsedMilliseconds}ms");
            }

            return result == IntPtr.Zero || string.IsNullOrWhiteSpace(info.DisplayName)
                ? null
                : info.DisplayName;
        }
        catch (Exception ex) when (ex is COMException or EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    /// <summary>"C:\" 처럼 드라이브 뿌리인지입니다.</summary>
    private static bool IsDriveRoot(string path)
    {
        var trimmed = path.Trim();

        return trimmed.Length is 2 or 3
               && char.IsLetter(trimmed[0])
               && trimmed[1] == ':';
    }
}
