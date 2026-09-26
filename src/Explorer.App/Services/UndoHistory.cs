using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using Explorer.Core.Services;
using Explorer.Shell.Operations;

namespace Explorer.App.Services;

/// <summary>우리가 한 파일 작업의 종류입니다.</summary>
public enum FileActionKind
{
    Copy,
    Move,
    Rename,
    Delete,

    /// <summary>여러 개의 이름을 한꺼번에 바꾼 일입니다.</summary>
    RenameMany
}

/// <summary>
/// 방금 한 파일 작업 하나입니다. 되돌릴 때 이 기록만 보고 거꾸로 합니다.
/// </summary>
/// <param name="Kind">무엇을 했는지입니다.</param>
/// <param name="Sources">손댄 것들의 원래 전체 경로입니다.</param>
/// <param name="Destination">복사·이동이면 받은 폴더입니다.</param>
/// <param name="NewName">이름 바꾸기이면 새 이름입니다.</param>
/// <param name="Recycled">삭제이면 휴지통에 들어간 자리들입니다.</param>
/// <param name="Renamed">일괄 이름 바꾸기이면 바뀐 뒤의 전체 경로들입니다. Sources 와 차례가 같습니다.</param>
public sealed record FileAction(
    FileActionKind Kind,
    IReadOnlyList<string> Sources,
    string? Destination = null,
    string? NewName = null,
    IReadOnlyList<string>? Recycled = null,
    IReadOnlyList<string>? Renamed = null);

/// <summary>
/// 되돌리기 기록입니다.
///
/// 윈도우의 되돌리기 목록은 탐색기가 자기 안에 들고 있어서 다른 프로그램이 부를 수 없습니다.
/// 그래서 우리가 한 일을 우리가 적어 두고 거꾸로 합니다.
/// 옮기기는 도로 옮기고, 이름 바꾸기는 도로 바꾸고, 복사는 만든 사본을 지웁니다.
/// 삭제는 휴지통에 들어간 자리를 지울 때 받아 적어 두었다가 그 자리에서 꺼내 옵니다.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UndoHistory
{
    private const int MaxDepth = 30;

    private readonly LinkedList<FileAction> _actions = new();

    public bool CanUndo => _actions.Count > 0;

    public void Record(FileAction action)
    {
        if (action.Sources.Count == 0)
        {
            return;
        }

        _actions.AddFirst(action);

        while (_actions.Count > MaxDepth)
        {
            _actions.RemoveLast();
        }
    }

    public void Clear() => _actions.Clear();

    /// <summary>가장 최근 작업을 거꾸로 합니다. 사람에게 보일 한 줄을 돌려줍니다.</summary>
    public string Undo(IntPtr owner)
    {
        if (_actions.First is not { } node)
        {
            DiagnosticLog.WriteAlways("되돌리기: 적어 둔 것이 없습니다. 윈도우 메뉴로 한 일은 우리 기록에 남지 않습니다.");
            return "되돌릴 것이 없습니다.";
        }

        var action = node.Value;
        _actions.RemoveFirst();

        return action.Kind switch
        {
            FileActionKind.Move => UndoMove(action, owner),
            FileActionKind.Copy => UndoCopy(action, owner),
            FileActionKind.Rename => UndoRename(action, owner),
            FileActionKind.RenameMany => UndoRenameMany(action, owner),
            _ => UndoDelete(action, owner)
        };
    }

    /// <summary>옮긴 것을 원래 폴더로 되돌립니다. 원래 폴더가 여럿이면 폴더별로 나누어 옮깁니다.</summary>
    private static string UndoMove(FileAction action, IntPtr owner)
    {
        if (action.Destination is not { } destination)
        {
            return "되돌릴 수 없습니다.";
        }

        var byFolder = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in action.Sources)
        {
            var name = Path.GetFileName(source);
            var parent = Path.GetDirectoryName(source);
            var moved = Path.Combine(destination, name);

            if (parent is null || !Exists(moved))
            {
                continue;
            }

            if (!byFolder.TryGetValue(parent, out var list))
            {
                list = new List<string>();
                byFolder[parent] = list;
            }

            list.Add(moved);
        }

        if (byFolder.Count == 0)
        {
            return "되돌릴 파일을 찾지 못했습니다.";
        }

        var restored = 0;

        foreach (var pair in byFolder)
        {
            if (ShellFileOperations.Move(pair.Value, pair.Key, owner))
            {
                restored += pair.Value.Count;
            }
        }

        return restored > 0 ? $"{restored}개를 제자리로 돌려놓았습니다." : "되돌리지 못했습니다.";
    }

    /// <summary>복사로 생긴 사본을 지웁니다. 휴지통으로 보내므로 다시 꺼낼 수 있습니다.</summary>
    private static string UndoCopy(FileAction action, IntPtr owner)
    {
        if (action.Destination is not { } destination)
        {
            return "되돌릴 수 없습니다.";
        }

        var copies = new List<string>(action.Sources.Count);

        foreach (var source in action.Sources)
        {
            var copy = Path.Combine(destination, Path.GetFileName(source));

            // 같은 폴더에 복사한 경우 사본 이름이 달라집니다. 원본을 지우면 안 되므로 건너뜁니다.
            if (Exists(copy) && !string.Equals(copy, source, StringComparison.OrdinalIgnoreCase))
            {
                copies.Add(copy);
            }
        }

        if (copies.Count == 0)
        {
            return "지울 사본을 찾지 못했습니다. 같은 폴더에 복사한 사본은 직접 지워 주십시오.";
        }

        return ShellFileOperations.Delete(copies, permanent: false, owner)
            ? $"복사한 {copies.Count}개를 휴지통으로 보냈습니다."
            : "되돌리지 못했습니다.";
    }

    /// <summary>한꺼번에 바꾼 이름들을 모두 되돌립니다.</summary>
    private static string UndoRenameMany(FileAction action, IntPtr owner)
    {
        if (action.Renamed is not { Count: > 0 } renamed || renamed.Count != action.Sources.Count)
        {
            return "되돌릴 수 없습니다.";
        }

        var requests = new List<ShellFileOperations.RenameRequest>(renamed.Count);

        for (var i = 0; i < renamed.Count; i++)
        {
            // 바뀐 이름이 그대로 있고 원래 이름 자리가 비어 있어야 되돌릴 수 있습니다.
            if (!Exists(renamed[i]) || Exists(action.Sources[i]))
            {
                continue;
            }

            requests.Add(new ShellFileOperations.RenameRequest(
                renamed[i],
                Path.GetFileName(action.Sources[i])));
        }

        if (requests.Count == 0)
        {
            return "되돌릴 파일을 찾지 못했습니다.";
        }

        if (!ShellFileOperations.RenameMany(requests, owner))
        {
            return "되돌리지 못했습니다.";
        }

        return requests.Count == renamed.Count
            ? $"{requests.Count}개의 이름을 되돌렸습니다."
            : $"{renamed.Count}개 가운데 {requests.Count}개의 이름을 되돌렸습니다.";
    }

    /// <summary>휴지통에 넣은 것을 제자리로 꺼냅니다. 바로 지운 것은 꺼낼 데가 없습니다.</summary>
    private static string UndoDelete(FileAction action, IntPtr owner)
    {
        if (action.Recycled is not { Count: > 0 } recycled)
        {
            DiagnosticLog.WriteAlways("삭제 되돌리기: 휴지통 자리를 붙잡아 둔 것이 없습니다.");
            return "휴지통 자리를 붙잡지 못해 되돌릴 수 없습니다. 휴지통에서 직접 꺼내 주십시오.";
        }

        DiagnosticLog.WriteAlways($"삭제 되돌리기: {recycled.Count}개를 꺼냅니다.");

        var restored = ShellRecycleBin.Restore(recycled, action.Sources, owner);

        if (restored == 0)
        {
            return "휴지통에서 꺼내지 못했습니다. 휴지통을 비웠는지 살펴 주십시오.";
        }

        return restored == recycled.Count
            ? $"{restored}개를 휴지통에서 꺼내 제자리에 놓았습니다."
            : $"{recycled.Count}개 가운데 {restored}개만 꺼냈습니다.";
    }

    private static string UndoRename(FileAction action, IntPtr owner)
    {
        if (action.NewName is not { } newName || action.Sources.Count == 0)
        {
            return "되돌릴 수 없습니다.";
        }

        var original = action.Sources[0];
        var parent = Path.GetDirectoryName(original);

        if (parent is null)
        {
            return "되돌릴 수 없습니다.";
        }

        var renamed = Path.Combine(parent, newName);

        if (!Exists(renamed))
        {
            return "되돌릴 파일을 찾지 못했습니다.";
        }

        return ShellFileOperations.Rename(renamed, Path.GetFileName(original), owner)
            ? $"이름을 {Path.GetFileName(original)}(으)로 되돌렸습니다."
            : "되돌리지 못했습니다.";
    }

    private static bool Exists(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
