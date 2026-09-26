using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Explorer.Core.Services;

namespace Explorer.Shell.Search;

/// <summary>Everything이 찾아 준 항목 하나입니다. 경로와 폴더 여부만 담습니다.</summary>
public sealed record EverythingHit(string FullPath, bool IsDirectory);

/// <summary>
/// Everything 프로그램에 창 메시지로 검색을 맡깁니다.
///
/// SDK로 배포되는 Everything64.dll을 쓰지 않습니다.
/// 그 파일을 함께 넣어 두어야 하고 비트 수도 맞춰야 하는데,
/// 실제로 오가는 것은 WM_COPYDATA 메시지 한 쌍뿐이라 직접 주고받는 편이 간단합니다.
///
/// 요청을 보내면 Everything이 우리 창으로 답을 되보냅니다. 그래서 창 손잡이가 필요합니다.
/// </summary>
[SupportedOSPlatform("windows")]
public static class EverythingIpc
{
    /// <summary>Everything이 항상 띄워 두는 숨은 창의 클래스 이름입니다.</summary>
    private const string WindowClassName = "EVERYTHING_TASKBAR_NOTIFICATION";

    private const int WindowMessageCopyData = 0x004A;

    /// <summary>유니코드 검색 요청임을 알리는 값입니다. SDK 헤더의 EVERYTHING_IPC_COPYDATA_QUERYW 입니다.</summary>
    private const int QueryKind = 2;

    /// <summary>경로까지 견주어 찾습니다.</summary>
    private const int MatchPath = 0x00000004;

    /// <summary>답신 머리의 크기(바이트)입니다. DWORD 일곱 개입니다.</summary>
    private const int ListHeaderSize = 28;

    /// <summary>답신 항목 하나의 크기(바이트)입니다. DWORD 세 개입니다.</summary>
    private const int ListItemSize = 12;

    private const int ItemFlagFolder = 0x00000001;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, ref CopyDataStruct data);

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public IntPtr Kind;
        public int Size;
        public IntPtr Data;
    }

    /// <summary>Everything이 떠 있는지 봅니다. 꺼져 있으면 검색을 쓸 수 없습니다.</summary>
    public static bool IsRunning => FindWindow(WindowClassName, null) != IntPtr.Zero;

    /// <summary>
    /// 검색을 요청합니다. 답은 곧바로 오지 않고 <paramref name="replyWindow"/>로 되돌아옵니다.
    /// </summary>
    /// <param name="replyWindow">답을 받을 우리 창의 손잡이입니다.</param>
    /// <param name="replyKind">답신에 붙여 달라고 부탁하는 표식입니다. 우리 것인지 가릴 때 씁니다.</param>
    public static bool Send(IntPtr replyWindow, int replyKind, string search, int maxResults)
    {
        var target = FindWindow(WindowClassName, null);

        if (target == IntPtr.Zero)
        {
            DiagnosticLog.Write("Everything 창을 찾지 못했습니다. 프로그램이 꺼져 있습니다.");
            return false;
        }

        var text = search ?? string.Empty;

        // 머리 20바이트 + 검색어 + 끝을 알리는 0
        var size = 20 + ((text.Length + 1) * 2);
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            // reply_hwnd 는 32비트 자리입니다. 64비트 창 손잡이도 아래 32비트만 넘깁니다.
            Marshal.WriteInt32(buffer, 0, (int)(replyWindow.ToInt64() & 0xFFFFFFFF));
            Marshal.WriteInt32(buffer, 4, replyKind);
            Marshal.WriteInt32(buffer, 8, MatchPath);
            Marshal.WriteInt32(buffer, 12, 0);
            Marshal.WriteInt32(buffer, 16, maxResults);

            for (var index = 0; index < text.Length; index++)
            {
                Marshal.WriteInt16(buffer, 20 + (index * 2), (short)text[index]);
            }

            Marshal.WriteInt16(buffer, 20 + (text.Length * 2), 0);

            var payload = new CopyDataStruct
            {
                Kind = new IntPtr(QueryKind),
                Size = size,
                Data = buffer
            };

            // SendMessage 가 돌아올 때까지 Everything 은 요청만 받아 둡니다. 답은 따로 옵니다.
            var accepted = SendMessage(target, WindowMessageCopyData, replyWindow, ref payload) != IntPtr.Zero;

            if (!accepted)
            {
                DiagnosticLog.Write("Everything이 검색 요청을 받지 않았습니다.");
            }

            return accepted;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// 창에 들어온 메시지가 우리 검색의 답인지 보고, 맞으면 목록으로 풀어 줍니다.
    /// 메시지가 살아 있는 동안에만 읽을 수 있는 기억이므로 그 자리에서 다 옮겨 담습니다.
    /// </summary>
    public static bool TryReadReply(int message, IntPtr parameter, int replyKind, out IReadOnlyList<EverythingHit> hits)
    {
        hits = Array.Empty<EverythingHit>();

        if (message != WindowMessageCopyData || parameter == IntPtr.Zero)
        {
            return false;
        }

        var payload = Marshal.PtrToStructure<CopyDataStruct>(parameter);

        if (payload.Kind.ToInt64() != replyKind || payload.Data == IntPtr.Zero || payload.Size < ListHeaderSize)
        {
            return false;
        }

        var count = Marshal.ReadInt32(payload.Data, 20);

        if (count < 0 || ListHeaderSize + ((long)count * ListItemSize) > payload.Size)
        {
            DiagnosticLog.Write($"Everything 답신이 이상합니다. 항목 {count}개, 크기 {payload.Size}바이트");
            return false;
        }

        var list = new List<EverythingHit>(count);

        for (var index = 0; index < count; index++)
        {
            var itemAt = ListHeaderSize + (index * ListItemSize);
            var flags = Marshal.ReadInt32(payload.Data, itemAt);
            var nameAt = Marshal.ReadInt32(payload.Data, itemAt + 4);
            var pathAt = Marshal.ReadInt32(payload.Data, itemAt + 8);

            if (nameAt <= 0 || nameAt >= payload.Size || pathAt <= 0 || pathAt >= payload.Size)
            {
                continue;
            }

            var name = Marshal.PtrToStringUni(IntPtr.Add(payload.Data, nameAt));
            var folder = Marshal.PtrToStringUni(IntPtr.Add(payload.Data, pathAt));

            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var full = string.IsNullOrEmpty(folder)
                ? name
                : folder.EndsWith('\\') ? folder + name : folder + "\\" + name;

            list.Add(new EverythingHit(full, (flags & ItemFlagFolder) != 0));
        }

        hits = list;
        return true;
    }
}
