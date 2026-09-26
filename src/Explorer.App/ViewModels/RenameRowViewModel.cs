using System.IO;

namespace Explorer.App.ViewModels;

/// <summary>
/// 일괄 이름 바꾸기 표의 한 줄입니다.
///
/// 한 줄은 "대상 파일 + 바꿀 이름" 한 쌍입니다. 순서를 바꾸면 두 칸이 함께 움직입니다.
/// 대상 파일이 없는 줄도 있습니다. 엑셀에서 붙여 넣은 이름이 파일보다 많을 때 남는 줄이고,
/// 파일을 더 담으면 위에서부터 이런 줄을 먼저 채웁니다.
/// </summary>
public sealed class RenameRowViewModel : ObservableObject
{
    private string? _fullPath;
    private string _after = string.Empty;
    private string _problem = string.Empty;
    private int _number;

    public RenameRowViewModel()
    {
    }

    public RenameRowViewModel(string fullPath)
    {
        AssignFile(fullPath);
        _after = Before;
    }

    /// <summary>바꾸기 전의 전체 경로입니다. 대상 파일이 없는 줄이면 null 입니다.</summary>
    public string? FullPath => _fullPath;

    public bool HasFile => _fullPath is not null;

    /// <summary>이 항목이 든 폴더입니다. 이름이 겹치는지는 폴더별로 따집니다.</summary>
    public string Folder => _fullPath is null ? string.Empty : Path.GetDirectoryName(_fullPath) ?? string.Empty;

    /// <summary>바꾸기 전 이름입니다. 확장자를 포함합니다. 대상 파일이 없으면 빈 칸입니다.</summary>
    public string Before => _fullPath is null ? string.Empty : Path.GetFileName(_fullPath);

    /// <summary>대상 파일이 없는 줄에 파일을 넣습니다. 바꿀 이름이 비어 있으면 원래 이름으로 채웁니다.</summary>
    public void AssignFile(string fullPath)
    {
        _fullPath = fullPath;
        OnPropertyChanged(nameof(FullPath));
        OnPropertyChanged(nameof(HasFile));
        OnPropertyChanged(nameof(Folder));
        OnPropertyChanged(nameof(Before));

        if (_after.Trim().Length == 0)
        {
            After = Before;
        }
    }

    public string After
    {
        get => _after;
        set => SetProperty(ref _after, value ?? string.Empty);
    }

    /// <summary>이 줄의 문제입니다. 없으면 빈 칸입니다. 표에서는 '이름 변경 후' 칸을 칠하고 풍선 도움말로 보입니다.</summary>
    public string Problem
    {
        get => _problem;
        set
        {
            if (SetProperty(ref _problem, value))
            {
                OnPropertyChanged(nameof(HasProblem));
            }
        }
    }

    public bool HasProblem => _problem.Length > 0;

    /// <summary>표에서 몇 번째 줄인지입니다(1부터). 줄 머리에 보입니다.</summary>
    public int Number
    {
        get => _number;
        set => SetProperty(ref _number, value);
    }

    /// <summary>파일도 이름도 없는 줄입니다. 표 맨 아래에 늘 하나 남겨 두어 새로 적을 자리로 씁니다.</summary>
    public bool IsBlank => _fullPath is null && _after.Trim().Length == 0;

    /// <summary>이름이 실제로 바뀌는 줄인지입니다.</summary>
    public bool Changed =>
        _fullPath is not null
        && After.Trim().Length > 0
        && !string.Equals(Before, After.Trim(), System.StringComparison.Ordinal);
}
