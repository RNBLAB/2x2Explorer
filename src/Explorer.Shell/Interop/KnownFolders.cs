using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Explorer.Shell.Interop;

/// <summary>
/// 윈도우가 정해 둔 폴더의 실제 경로를 셸에게 묻습니다.
///
/// 다운로드 폴더는 .NET 이 따로 알려 주지 않습니다.
/// 사용자가 다운로드 폴더를 다른 드라이브로 옮겨 두었을 수도 있어, 사용자 폴더 아래라고 짐작하면 틀립니다.
/// </summary>
[SupportedOSPlatform("windows")]
public static class KnownFolders
{
    /// <summary>FOLDERID_Downloads 입니다.</summary>
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>다운로드 폴더의 실제 경로입니다. 얻지 못하면 null 입니다.</summary>
    public static string? Downloads() => Locate(DownloadsId);

    private static string? Locate(Guid folderId)
    {
        var id = folderId;
        var buffer = IntPtr.Zero;

        try
        {
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out buffer) != 0 || buffer == IntPtr.Zero)
            {
                return null;
            }

            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(buffer);
            }
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);
}
