using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Explorer.Core.Services;
using Explorer.Shell.Interop;

namespace Explorer.Shell.Operations;

/// <summary>
/// 셸 파일 작업이 한 항목씩 지날 때마다 알려 주는 자리입니다.
///
/// 선언 순서가 곧 함수 표의 순서입니다. 중간에 끼워 넣거나 빼면 엉뚱한 함수가 불립니다.
/// 쓰지 않는 것도 자리를 채워 두어야 하는 이유입니다.
/// </summary>
[ComImport]
[Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IFileOperationProgressSink
{
    [PreserveSig] int StartOperations();

    [PreserveSig] int FinishOperations(int result);

    [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string? newName);

    [PreserveSig] int PostRenameItem(
        uint flags,
        IShellItem item,
        [MarshalAs(UnmanagedType.LPWStr)] string? newName,
        int result,
        IShellItem? newlyCreated);

    [PreserveSig] int PreMoveItem(
        uint flags,
        IShellItem item,
        IShellItem destinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string? newName);

    [PreserveSig] int PostMoveItem(
        uint flags,
        IShellItem item,
        IShellItem destinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string? newName,
        int result,
        IShellItem? newlyCreated);

    [PreserveSig] int PreCopyItem(
        uint flags,
        IShellItem item,
        IShellItem destinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string? newName);

    [PreserveSig] int PostCopyItem(
        uint flags,
        IShellItem item,
        IShellItem destinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string? newName,
        int result,
        IShellItem? newlyCreated);

    [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);

    [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? newlyCreated);

    [PreserveSig] int PreNewItem(
        uint flags,
        IShellItem destinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string? newName);

    [PreserveSig] int PostNewItem(
        uint flags,
        IShellItem destinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string? newName,
        [MarshalAs(UnmanagedType.LPWStr)] string? templateName,
        uint fileAttributes,
        int result,
        IShellItem? newItem);

    [PreserveSig] int UpdateProgress(uint workTotal, uint workSoFar);

    [PreserveSig] int ResetTimer();

    [PreserveSig] int PauseTimer();

    [PreserveSig] int ResumeTimer();
}

/// <summary>
/// 지운 항목이 휴지통 어느 자리에 들어갔는지 받아 적습니다.
///
/// 휴지통에 들어간 항목은 원래 경로로는 더 이상 가리킬 수 없습니다.
/// 셸이 그때그때 새 이름을 붙여 넣기 때문입니다.
/// 그 새 자리를 알아 두어야 나중에 제자리로 돌려놓을 수 있습니다.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DeleteTracker : IFileOperationProgressSink
{
    /// <summary>SIGDN_DESKTOPABSOLUTEPARSING — 다시 찾아갈 수 있는 이름을 달라는 뜻입니다.</summary>
    private const uint DesktopAbsoluteParsing = 0x80028000;

    private readonly List<string> _recycled = new();

    /// <summary>휴지통에 들어간 항목들의 이름입니다. 지운 차례 그대로입니다.</summary>
    public IReadOnlyList<string> Recycled => _recycled;

    public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? newlyCreated)
    {
        // 바로 지운 항목은 새로 생긴 자리가 없습니다. 휴지통으로 간 것만 적힙니다.
        var name = newlyCreated is null ? null : TryGetParsingName(newlyCreated);

        DiagnosticLog.WriteAlways(
            $"지움 알림 0x{result:X8}, 새 자리 {(newlyCreated is null ? "없음" : name ?? "이름 못 읽음")}");

        if (result >= 0 && name is not null)
        {
            _recycled.Add(name);
        }

        return 0;
    }

    public int StartOperations() => 0;

    public int FinishOperations(int result) => 0;

    public int PreRenameItem(uint flags, IShellItem item, string? newName) => 0;

    public int PostRenameItem(uint flags, IShellItem item, string? newName, int result, IShellItem? newlyCreated) => 0;

    public int PreMoveItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName) => 0;

    public int PostMoveItem(
        uint flags,
        IShellItem item,
        IShellItem destinationFolder,
        string? newName,
        int result,
        IShellItem? newlyCreated) => 0;

    public int PreCopyItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName) => 0;

    public int PostCopyItem(
        uint flags,
        IShellItem item,
        IShellItem destinationFolder,
        string? newName,
        int result,
        IShellItem? newlyCreated) => 0;

    public int PreDeleteItem(uint flags, IShellItem item) => 0;

    public int PreNewItem(uint flags, IShellItem destinationFolder, string? newName) => 0;

    public int PostNewItem(
        uint flags,
        IShellItem destinationFolder,
        string? newName,
        string? templateName,
        uint fileAttributes,
        int result,
        IShellItem? newItem) => 0;

    public int UpdateProgress(uint workTotal, uint workSoFar) => 0;

    public int ResetTimer() => 0;

    public int PauseTimer() => 0;

    public int ResumeTimer() => 0;

    private static string? TryGetParsingName(IShellItem item)
    {
        var pointer = IntPtr.Zero;

        try
        {
            if (item.GetDisplayName(DesktopAbsoluteParsing, out pointer) < 0 || pointer == IntPtr.Zero)
            {
                return null;
            }

            return Marshal.PtrToStringUni(pointer);
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            if (pointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }
    }
}
