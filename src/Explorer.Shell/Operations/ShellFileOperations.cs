using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Explorer.Core.Services;
using Explorer.Shell.Interop;

namespace Explorer.Shell.Operations;

/// <summary>
/// 파일 복사·이동·삭제·이름 바꾸기를 윈도우 셸에 맡깁니다.
/// 진행률 창, 같은 이름 충돌 대화상자, 휴지통, Ctrl+Z 되돌리기가 전부 딸려 옵니다.
/// 직접 만들면 그 넷을 다 구현해야 합니다.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ShellFileOperations
{
    /// <param name="renameOnCollision">
    /// true이면 같은 이름이 있을 때 묻지 않고 "- 복사본"처럼 이름을 붙입니다.
    /// 같은 폴더 안에서 복사할 때 씁니다.
    /// </param>
    public static bool Copy(
        IReadOnlyList<string> sources,
        string destinationFolder,
        IntPtr owner,
        bool renameOnCollision = false) =>
        Run(sources, destinationFolder, owner, OperationKind.Copy, permanent: false, newName: null, renameOnCollision: renameOnCollision);

    public static bool Move(IReadOnlyList<string> sources, string destinationFolder, IntPtr owner) =>
        Run(sources, destinationFolder, owner, OperationKind.Move, permanent: false, newName: null);

    /// <param name="permanent">true이면 휴지통을 거치지 않고 바로 지웁니다.</param>
    public static bool Delete(IReadOnlyList<string> targets, bool permanent, IntPtr owner) =>
        Run(targets, null, owner, OperationKind.Delete, permanent, newName: null);

    /// <summary>
    /// 지우면서, 휴지통 어느 자리에 들어갔는지 받아 적습니다.
    /// 그 자리를 알아야 나중에 되돌리기로 꺼낼 수 있습니다.
    /// </summary>
    /// <param name="recycled">휴지통에 들어간 항목들의 이름입니다. 바로 지웠으면 비어 있습니다.</param>
    public static bool Delete(
        IReadOnlyList<string> targets,
        bool permanent,
        IntPtr owner,
        out IReadOnlyList<string> recycled)
    {
        if (permanent)
        {
            recycled = Array.Empty<string>();
            return Run(targets, null, owner, OperationKind.Delete, permanent: true, newName: null);
        }

        var tracker = new DeleteTracker();
        var done = Run(targets, null, owner, OperationKind.Delete, permanent: false, newName: null, tracker: tracker);

        recycled = tracker.Recycled;
        DiagnosticLog.WriteAlways($"삭제 {(done ? "됨" : "못 함")}, 휴지통에서 붙잡은 것 {recycled.Count}개 / 지운 것 {targets.Count}개");
        return done;
    }

    public static bool Rename(string target, string newName, IntPtr owner) =>
        Run(new[] { target }, null, owner, OperationKind.Rename, permanent: false, newName);

    /// <summary>한 항목의 새 이름입니다.</summary>
    public readonly record struct RenameRequest(string Path, string NewName);

    /// <summary>
    /// 여러 항목의 이름을 한 번에 바꿉니다.
    /// 하나씩 부르지 않는 까닭은, 셸 작업 하나로 묶어야 진행률 창도 되돌리기 기록도 하나로 남기 때문입니다.
    /// </summary>
    public static bool RenameMany(IReadOnlyList<RenameRequest> requests, IntPtr owner)
    {
        if (requests.Count == 0)
        {
            return true;
        }

        IFileOperation? operation = null;
        var items = new List<IShellItem>(requests.Count);

        try
        {
            var type = Type.GetTypeFromCLSID(ShellNative.FileOperationClassId);

            if (type is null || Activator.CreateInstance(type) is not IFileOperation created)
            {
                return false;
            }

            operation = created;
            operation.SetOwnerWindow(owner);
            operation.SetOperationFlags(
                ShellNative.FOF_NOCONFIRMMKDIR | ShellNative.FOF_ALLOWUNDO | ShellNative.FOFX_ADDUNDORECORD);

            foreach (var request in requests)
            {
                var item = CreateItem(request.Path);

                if (item is null)
                {
                    continue;
                }

                items.Add(item);

                if (operation.RenameItem(item, request.NewName, IntPtr.Zero) < 0)
                {
                    return false;
                }
            }

            if (operation.PerformOperations() < 0)
            {
                return false;
            }

            operation.GetAnyOperationsAborted(out var aborted);
            return aborted == 0;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            foreach (var item in items)
            {
                Marshal.ReleaseComObject(item);
            }

            if (operation is not null)
            {
                Marshal.ReleaseComObject(operation);
            }
        }
    }

    private enum OperationKind
    {
        Copy,
        Move,
        Delete,
        Rename
    }

    private static bool Run(
        IReadOnlyList<string> sources,
        string? destinationFolder,
        IntPtr owner,
        OperationKind kind,
        bool permanent,
        string? newName,
        bool renameOnCollision = false,
        DeleteTracker? tracker = null)
    {
        if (sources.Count == 0)
        {
            return true;
        }

        IFileOperation? operation = null;
        IShellItem? destination = null;
        var items = new List<IShellItem>(sources.Count);
        var sink = IntPtr.Zero;
        uint cookie = 0;

        try
        {
            var type = Type.GetTypeFromCLSID(ShellNative.FileOperationClassId);

            if (type is null || Activator.CreateInstance(type) is not IFileOperation created)
            {
                return false;
            }

            operation = created;
            operation.SetOwnerWindow(owner);

            // 한 항목씩 끝날 때마다 들으려면 이렇게 붙여 둡니다.
            if (tracker is not null)
            {
                sink = Marshal.GetComInterfaceForObject(tracker, typeof(IFileOperationProgressSink));
                var advised = operation.Advise(sink, out cookie);

                DiagnosticLog.WriteAlways($"삭제 감시 붙이기 0x{advised:X8}, 표 {cookie}");

                if (advised < 0)
                {
                    cookie = 0;
                }
            }

            var flags = ShellNative.FOF_NOCONFIRMMKDIR;

            // 같은 폴더에 복사할 때는 셸이 사본 이름을 붙입니다. 탐색기의 "- 복사본"과 같습니다.
            if (renameOnCollision)
            {
                flags |= ShellNative.FOF_RENAMEONCOLLISION;
            }

            // 지우기는 기본이 휴지통입니다. Shift+Delete일 때만 되돌리기를 빼서 바로 지웁니다.
            if (!(kind == OperationKind.Delete && permanent))
            {
                flags |= ShellNative.FOF_ALLOWUNDO | ShellNative.FOFX_ADDUNDORECORD;
            }

            operation.SetOperationFlags(flags);

            if (destinationFolder is not null)
            {
                destination = CreateItem(destinationFolder);

                if (destination is null)
                {
                    return false;
                }
            }

            foreach (var source in sources)
            {
                var item = CreateItem(source);

                if (item is null)
                {
                    continue;
                }

                items.Add(item);

                var queued = kind switch
                {
                    OperationKind.Copy => operation.CopyItem(item, destination!, null, IntPtr.Zero),
                    OperationKind.Move => operation.MoveItem(item, destination!, null, IntPtr.Zero),
                    // 항목마다도 같은 귀를 달아 둡니다. Advise 가 먹지 않는 자리가 있어 두 길을 다 씁니다.
                    OperationKind.Delete => operation.DeleteItem(item, cookie == 0 ? sink : IntPtr.Zero),
                    OperationKind.Rename => operation.RenameItem(item, newName ?? string.Empty, IntPtr.Zero),
                    _ => -1
                };

                if (queued < 0)
                {
                    return false;
                }
            }

            if (operation.PerformOperations() < 0)
            {
                return false;
            }

            operation.GetAnyOperationsAborted(out var aborted);
            return aborted == 0;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            if (operation is not null && cookie != 0)
            {
                operation.Unadvise(cookie);
            }

            if (sink != IntPtr.Zero)
            {
                Marshal.Release(sink);
            }

            foreach (var item in items)
            {
                Marshal.ReleaseComObject(item);
            }

            if (destination is not null)
            {
                Marshal.ReleaseComObject(destination);
            }

            if (operation is not null)
            {
                Marshal.ReleaseComObject(operation);
            }
        }
    }

    private static IShellItem? CreateItem(string path)
    {
        try
        {
            var interfaceId = ShellNative.ShellItemInterfaceId;
            ShellNative.SHCreateItemFromParsingName(path, IntPtr.Zero, ref interfaceId, out var item);
            return item;
        }
        catch (Exception ex) when (ex is COMException
                                       or System.IO.FileNotFoundException
                                       or ArgumentException)
        {
            return null;
        }
    }
}
