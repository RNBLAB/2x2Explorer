using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.Versioning;
using Explorer.Core.Services;
using Explorer.Shell.Interop;

namespace Explorer.App.ViewModels;

/// <summary>
/// 패널 옆에 붙는 폴더 나무입니다.
///
/// 뿌리는 즐겨찾기입니다. 그 가운데 "내 PC" 아래에 드라이브가 달리고,
/// 드라이브를 펼치면 그때 아래를 읽습니다.
/// 즐겨찾기에서 "내 PC"를 빼도 맨 끝에 따로 세우므로 드라이브를 볼 길은 늘 남습니다.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FolderTreeViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<string>>? _favorites;

    public FolderTreeViewModel(Func<IReadOnlyList<string>>? favorites = null)
    {
        _favorites = favorites;
        Reload();
    }

    public ObservableCollection<FolderNodeViewModel> Roots { get; } = new();

    /// <summary>
    /// 나무를 다시 세웁니다. 즐겨찾기를 고치거나 이동식 저장 장치를 꽂고 뺀 뒤에 씁니다.
    /// </summary>
    public void Reload()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Roots.Clear();

        foreach (var node in ReadFavorites())
        {
            Roots.Add(node);
        }

        if (watch.ElapsedMilliseconds > 80)
        {
            DiagnosticLog.Write($"탐색 창 세우기 {watch.ElapsedMilliseconds}ms, 뿌리 {Roots.Count}개");
        }
    }

    /// <summary>
    /// 그 폴더까지 가지를 펼치고 골라 둡니다. 목록이 옮겨 간 자리를 나무도 따라가게 할 때 씁니다.
    /// 길에 걸친 마디만 읽으므로 나무 전체를 읽지는 않습니다.
    /// </summary>
    public void Reveal(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        // 휴지통은 나무에 없는 자리입니다. 짚을 줄이 없습니다.
        if (SpecialPaths.IsRecycleBin(path))
        {
            return;
        }

        // 내 PC를 열었으면 맨 위의 "내 PC" 줄을 짚습니다.
        if (SpecialPaths.IsMyComputer(path))
        {
            foreach (var root in Roots)
            {
                if (root.IsComputer)
                {
                    root.IsSelected = true;
                    return;
                }
            }

            return;
        }

        string full;

        try
        {
            full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        // 가장 가까운 뿌리에서 내려갑니다.
        // C 드라이브와 즐겨찾기의 "바탕 화면"이 둘 다 품는 자리라면 경로가 긴 바탕 화면 쪽이 사람이 찾는 줄입니다.
        FolderNodeViewModel? start = null;
        FolderNodeViewModel? computer = null;

        foreach (var root in Roots)
        {
            if (root.IsComputer)
            {
                // 드라이브는 "내 PC" 아래에 있습니다. 한 층 들어가서 견줍니다.
                foreach (var drive in root.Children)
                {
                    if (Covers(drive.FullPath, full) && IsCloser(drive, start))
                    {
                        start = drive;
                        computer = root;
                    }
                }

                continue;
            }

            if (Covers(root.FullPath, full) && IsCloser(root, start))
            {
                start = root;
                computer = null;
            }
        }

        if (start is null)
        {
            return;
        }

        // 드라이브를 짚으려면 그 위의 "내 PC"부터 펴야 보입니다.
        if (computer is not null)
        {
            computer.IsExpanded = true;
        }

        using var step = UiWatch.Step($"나무에서 자리 짚기 {full}");

        if (Descend(start, full) is { } node)
        {
            node.IsSelected = true;
        }
    }

    private static bool IsCloser(FolderNodeViewModel candidate, FolderNodeViewModel? best) =>
        best is null || candidate.FullPath.Length > best.FullPath.Length;

    /// <summary>뿌리에서 한 층씩 내려갑니다. 길이 끊기면 거기서 멈춥니다.</summary>
    private static FolderNodeViewModel? Descend(FolderNodeViewModel root, string full)
    {
        var node = root;

        // 경로 깊이만큼만 돕니다. 어떤 까닭으로든 제자리를 맴돌면 빠져나옵니다.
        for (var depth = 0; depth < 64; depth++)
        {
            if (Same(node.FullPath, full))
            {
                return node;
            }

            node.IsExpanded = true;

            FolderNodeViewModel? next = null;

            foreach (var child in node.Children)
            {
                if (Covers(child.FullPath, full))
                {
                    next = child;
                    break;
                }
            }

            if (next is null)
            {
                return null;
            }

            node = next;
        }

        return null;
    }

    /// <summary>앞의 경로가 뒤의 경로를 품고 있는지입니다. 같은 경로도 품은 것으로 봅니다.</summary>
    private static bool Covers(string folder, string target)
    {
        if (folder.Length == 0 || SpecialPaths.IsMyComputer(folder))
        {
            return false;
        }

        if (Same(folder, target))
        {
            return true;
        }

        var prefix = folder.EndsWith(Path.DirectorySeparatorChar)
            ? folder
            : folder + Path.DirectorySeparatorChar;

        return target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Same(string left, string right) =>
        string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 즐겨찾기를 뿌리로 세웁니다.
    /// "내 PC"는 드라이브를 담은 마디가 되고, 나머지는 폴더 마디가 됩니다.
    /// 사라진 폴더는 건너뜁니다. 즐겨찾기에 내 PC 가 없으면 맨 끝에 덧붙입니다.
    /// </summary>
    private List<FolderNodeViewModel> ReadFavorites()
    {
        var nodes = new List<FolderNodeViewModel>();

        if (_favorites is null)
        {
            return nodes;
        }

        IReadOnlyList<string> paths;

        try
        {
            paths = _favorites();
        }
        catch (Exception)
        {
            return nodes;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (SpecialPaths.IsMyComputer(path))
            {
                if (!seen.Add(SpecialPaths.MyComputer))
                {
                    continue;
                }

                var computer = FolderNodeViewModel.CreateComputer(ReadDrives());

                // 드라이브가 바로 보이도록 펴 둡니다.
                computer.IsExpanded = true;
                nodes.Add(computer);
                continue;
            }

            var full = Expand(path);

            if (full.Length == 0 || !seen.Add(full.TrimEnd(Path.DirectorySeparatorChar)) || !SafeExists(full))
            {
                continue;
            }

            nodes.Add(new FolderNodeViewModel(DisplayName(full), full));
        }

        // 즐겨찾기에서 내 PC 를 뺐어도 드라이브를 볼 길은 남겨야 합니다. 맨 끝에 덧붙입니다.
        if (!seen.Contains(SpecialPaths.MyComputer))
        {
            nodes.Add(FolderNodeViewModel.CreateComputer(ReadDrives()));
        }

        return nodes;
    }

    private static string Expand(string path)
    {
        try
        {
            // 바탕 화면·다운로드·문서 표시는 이 컴퓨터의 실제 경로로 바뀝니다.
            return PathHelper.Expand(path);
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    /// <summary>셸이 쓰는 이름을 그대로 씁니다. 그래야 "바탕 화면"처럼 탐색기와 같은 이름이 나옵니다.</summary>
    private static string DisplayName(string path) =>
        ShellNames.GetDisplayName(path) ?? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));

    private static bool SafeExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static List<FolderNodeViewModel> ReadDrives()
    {
        try
        {
            var drives = DriveInfo.GetDrives();
            var nodes = new List<FolderNodeViewModel>(drives.Length);

            foreach (var drive in drives)
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                // 셸에게 이름을 물어봅니다. 탐색기에 나오는 "로컬 디스크 (C:)" 그대로입니다.
                // 볼륨 레이블만 읽으면 "새 볼륨" 같은 이름이 나와 탐색기와 달라집니다.
                var label = ShellNames.GetDisplayName(drive.RootDirectory.FullName)
                            ?? drive.Name.TrimEnd(Path.DirectorySeparatorChar);

                nodes.Add(new FolderNodeViewModel(label, drive.RootDirectory.FullName));
            }

            return nodes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<FolderNodeViewModel>();
        }
    }
}
