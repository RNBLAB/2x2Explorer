using System;
using System.Globalization;
using System.Windows.Media;
using Explorer.Core.Models;
using Explorer.Core.Services;

namespace Explorer.App.ViewModels;

/// <summary>목록의 한 줄입니다.</summary>
public sealed class FileItemViewModel : ObservableObject
{
    private ImageSource? _thumbnail;
    private bool _isEditing;
    private string _editingName = string.Empty;

    public FileItemViewModel(FileSystemEntry entry)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
    }

    public FileSystemEntry Entry { get; }

    public string Name => Entry.Name;

    public string FullPath => Entry.FullPath;

    public bool IsDirectory => Entry.IsDirectory;

    public string SizeText => Entry.IsDirectory ? string.Empty : ByteSizeFormatter.Format(Entry.Size);

    public string ModifiedText => Entry.LastWriteTime == DateTime.MinValue
        ? string.Empty
        : Entry.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    public string CreatedText => Entry.CreationTime == DateTime.MinValue
        ? string.Empty
        : Entry.CreationTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    /// <summary>점을 뺀 확장자입니다. 폴더는 빈 칸입니다.</summary>
    public string ExtensionText => Entry.Extension.Length > 1
        ? Entry.Extension.TrimStart('.').ToLowerInvariant()
        : string.Empty;

    /// <summary>이 항목이 든 폴더입니다. 검색 결과에서 어디 것인지 가릴 때 씁니다.</summary>
    public string FolderText => System.IO.Path.GetDirectoryName(Entry.FullPath) ?? string.Empty;

    public string TypeText
    {
        get
        {
            if (Entry.IsDirectory)
            {
                return "폴더";
            }

            var extension = Entry.Extension;
            return extension.Length > 1
                ? extension.TrimStart('.').ToUpperInvariant() + " 파일"
                : "파일";
        }
    }

    /// <summary>숨김 파일은 흐리게 보여 주기 위한 표시입니다.</summary>
    public double Opacity => Entry.IsHidden ? 0.55 : 1.0;

    /// <summary>셸에서 받아 온 썸네일입니다. 화면에 줄이 나타날 때 비로소 채워집니다.</summary>
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }

    /// <summary>이름을 그 자리에서 고치는 중인지 여부입니다.</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set => SetProperty(ref _isEditing, value);
    }

    /// <summary>편집 상자에 들어 있는 글자입니다.</summary>
    public string EditingName
    {
        get => _editingName;
        set => SetProperty(ref _editingName, value);
    }

    /// <summary>이름 편집을 시작합니다.</summary>
    public void BeginEdit()
    {
        EditingName = Name;
        IsEditing = true;
    }
}
