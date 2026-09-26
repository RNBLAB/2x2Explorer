using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.Versioning;
using Explorer.Core.Services;

namespace Explorer.App.ViewModels;

/// <summary>
/// 폴더 나무의 마디 하나입니다.
///
/// 아래 가지는 펼칠 때 비로소 읽습니다.
/// 처음부터 다 읽으면 드라이브 하나 펼치는 데도 한참 걸립니다.
/// 그래서 펼칠 수 있어 보이게만 해 두는 빈 마디를 하나 넣어 둡니다.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FolderNodeViewModel : ObservableObject
{
    private static readonly FolderNodeViewModel Placeholder = new("", "", false);

    private bool _expanded;
    private bool _selected;
    private bool _filled;

    public FolderNodeViewModel(string name, string fullPath, bool mayHaveChildren = true)
    {
        Name = name;
        FullPath = fullPath;

        if (mayHaveChildren)
        {
            Children.Add(Placeholder);
        }
    }

    /// <summary>
    /// 드라이브를 담는 "내 PC" 마디입니다.
    /// 디스크에서 읽어 올 자리가 아니므로 받은 드라이브 목록을 그대로 답니다.
    /// </summary>
    public static FolderNodeViewModel CreateComputer(IEnumerable<FolderNodeViewModel> drives)
    {
        var node = new FolderNodeViewModel(
            SpecialPaths.MyComputerDisplayName,
            SpecialPaths.MyComputer,
            mayHaveChildren: false)
        {
            IsComputer = true
        };

        // 이미 채운 것으로 두어야 펼칠 때 디스크를 뒤지지 않습니다.
        node._filled = true;

        foreach (var drive in drives)
        {
            node.Children.Add(drive);
        }

        return node;
    }

    /// <summary>이 마디가 드라이브를 담는 "내 PC"인지입니다.</summary>
    public bool IsComputer { get; private init; }

    public string Name { get; }

    public string FullPath { get; }

    public ObservableCollection<FolderNodeViewModel> Children { get; } = new();

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (!SetProperty(ref _expanded, value) || !value)
            {
                return;
            }

            Fill();
        }
    }

    public bool IsSelected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    /// <summary>아래 폴더를 읽어 채웁니다. 한 번만 읽습니다.</summary>
    public void Fill()
    {
        if (_filled)
        {
            return;
        }

        _filled = true;
        Children.Clear();

        // 디스크를 읽는 일입니다. 화면 갈래에서 돌면 그동안 창이 멈춥니다.
        using var step = UiWatch.Step($"나무 가지 읽기 {FullPath}");

        foreach (var child in ReadChildren(FullPath))
        {
            Children.Add(child);
        }
    }

    /// <summary>다시 읽습니다. 폴더를 만들거나 지운 뒤에 씁니다.</summary>
    public void Refresh()
    {
        // "내 PC"는 디스크에서 읽어 오는 자리가 아닙니다. 드라이브 목록은 나무를 다시 세울 때 바뀝니다.
        if (IsComputer)
        {
            return;
        }

        _filled = false;

        if (_expanded)
        {
            Fill();
        }
        else
        {
            Children.Clear();
            Children.Add(Placeholder);
        }
    }

    private static IReadOnlyList<FolderNodeViewModel> ReadChildren(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return Array.Empty<FolderNodeViewModel>();
        }

        try
        {
            var names = new List<string>();

            foreach (var folder in Directory.EnumerateDirectories(path))
            {
                var attributes = SafeAttributes(folder);

                // 시스템이 숨겨 둔 자리는 보여 주지 않습니다. 탐색기와 같은 규칙입니다.
                if ((attributes & FileAttributes.System) != 0 && (attributes & FileAttributes.Hidden) != 0)
                {
                    continue;
                }

                names.Add(folder);
            }

            names.Sort((left, right) =>
                NaturalStringComparer.Instance.Compare(Path.GetFileName(left), Path.GetFileName(right)));

            var nodes = new List<FolderNodeViewModel>(names.Count);

            foreach (var folder in names)
            {
                nodes.Add(new FolderNodeViewModel(Path.GetFileName(folder), folder));
            }

            return nodes;
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or ArgumentException
                                       or NotSupportedException)
        {
            // 들여다볼 수 없는 폴더입니다. 비어 있는 것으로 둡니다.
            return Array.Empty<FolderNodeViewModel>();
        }
    }

    private static FileAttributes SafeAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return FileAttributes.Normal;
        }
    }
}
