using Explorer.Core.Services;

namespace Explorer.App.ViewModels;

/// <summary>
/// 정규화 창 요소 표의 한 줄입니다. 이름 하나를 요소로 나눈 결과(<see cref="NameParts"/>)를 칸마다 고칠 수 있게 보입니다.
/// 칸을 고치면 짐작 표시가 풀리고, 조립 결과(<see cref="Result"/>)는 창이 다시 셉니다.
/// </summary>
public sealed class NormalizeRowViewModel : ObservableObject
{
    private readonly NameParts _parts;
    private string _result = string.Empty;

    public NormalizeRowViewModel(RenameRowViewModel target, NameParts parts)
    {
        Target = target;
        _parts = parts;
        Unresolved = parts.Sequence.Length == 0 && parts.Date.Length == 0 && parts.DocNumber.Length == 0
            && parts.Project.Length == 0 && parts.Version.Length == 0 && parts.Author.Length == 0;
    }

    /// <summary>결과를 써 넣을 일괄 이름 바꾸기 표의 줄입니다.</summary>
    public RenameRowViewModel Target { get; }

    public NameParts Parts => _parts;

    public string Original => _parts.Original;

    /// <summary>본문 말고는 아무 요소도 찾지 못한 이름입니다. 표에서 흐리게 보입니다.</summary>
    public bool Unresolved { get; }

    public string Sequence
    {
        get => _parts.Sequence;
        set => Set(value, v => _parts.Sequence = v);
    }

    /// <summary>날짜 칸입니다. yyyy-MM-dd 로 보이고, 고쳐 적으면 다시 읽습니다. 날짜로 읽히지 않으면 적힌 그대로 씁니다.</summary>
    public string Date
    {
        get => NamePattern.ShowDate(_parts.Date, _parts.DateSuffix);
        set
        {
            var text = (value ?? string.Empty).Trim();

            if (text.Length == 0)
            {
                _parts.Date = string.Empty;
                _parts.DateSuffix = string.Empty;
            }
            else if (NamePattern.TryReadDate(text, out var date, out var suffix))
            {
                _parts.Date = date;
                _parts.DateSuffix = suffix;
            }
            else
            {
                _parts.Date = text;
                _parts.DateSuffix = string.Empty;
            }

            OnPropertyChanged();
        }
    }

    public string DocNumber
    {
        get => _parts.DocNumber;
        set => Set(value, v => _parts.DocNumber = v);
    }

    public string Project
    {
        get => _parts.Project;
        set => Set(value, v =>
        {
            _parts.Project = v;
            _parts.ProjectGuessed = false;
            OnPropertyChanged(nameof(ProjectGuessed));
        });
    }

    public bool ProjectGuessed => _parts.ProjectGuessed && _parts.Project.Length > 0;

    public string Body
    {
        get => _parts.Body;
        set => Set(value, v => _parts.Body = v);
    }

    public string Version
    {
        get => _parts.Version;
        set => Set(value, v => _parts.Version = v.TrimStart('v', 'V').Trim());
    }

    public string Author
    {
        get => _parts.Author;
        set => Set(value, v =>
        {
            _parts.Author = v;
            _parts.AuthorGuessed = false;
            OnPropertyChanged(nameof(AuthorGuessed));
        });
    }

    public bool AuthorGuessed => _parts.AuthorGuessed && _parts.Author.Length > 0;

    public string Note
    {
        get => _parts.Note;
        set => Set(value, v => _parts.Note = v);
    }

    /// <summary>목표 패턴으로 조립한 이름입니다.</summary>
    public string Result
    {
        get => _result;
        set => SetProperty(ref _result, value);
    }

    /// <summary>요소 칸을 이름으로 읽고 씁니다. 붙여넣기·열 채우기가 씁니다.</summary>
    public string this[NamePart part]
    {
        get => part switch
        {
            NamePart.Sequence => Sequence,
            NamePart.Date => Date,
            NamePart.DocNumber => DocNumber,
            NamePart.Project => Project,
            NamePart.Body => Body,
            NamePart.Version => Version,
            NamePart.Author => Author,
            NamePart.Note => Note,
            _ => string.Empty
        };
        set
        {
            switch (part)
            {
                case NamePart.Sequence: Sequence = value; break;
                case NamePart.Date: Date = value; break;
                case NamePart.DocNumber: DocNumber = value; break;
                case NamePart.Project: Project = value; break;
                case NamePart.Body: Body = value; break;
                case NamePart.Version: Version = value; break;
                case NamePart.Author: Author = value; break;
                case NamePart.Note: Note = value; break;
            }
        }
    }

    private void Set(string? value, System.Action<string> apply, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        apply((value ?? string.Empty).Trim());
        OnPropertyChanged(name);
    }
}
