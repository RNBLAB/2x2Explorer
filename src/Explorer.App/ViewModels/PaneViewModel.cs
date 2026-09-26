using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Explorer.App.Services;
using Explorer.Core.Models;
using Explorer.Core.Services;
using Explorer.Core.Settings;

namespace Explorer.App.ViewModels;

/// <summary>
/// 분할 패널 하나입니다. 탭 목록과, 탭들이 함께 쓰는 설정(정렬·보기 방식)을 맡습니다.
/// 탭 하나하나는 <see cref="FileListViewModel"/>이고, 자기 폴더와 이동 이력을 따로 들고 있습니다.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PaneViewModel : ObservableObject, IDisposable
{
    private const int MaxTabs = 30;

    private readonly AppHost _host;
    private FileListViewModel? _selectedTab;
    private bool _showTree;
    private bool _treeOnRight;
    private bool _disposed;

    /// <summary>방금 닫은 탭의 폴더입니다. 나중에 닫은 것부터 다시 엽니다.</summary>
    private readonly Stack<string> _closed = new();

    public PaneViewModel(AppHost host, PaneSettings settings)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));

        // 나무 맨 위의 즐겨찾기는 설정에서 그때그때 읽습니다. 여기서 값을 베껴 두면 곧 어긋납니다.
        Tree = new FolderTreeViewModel(() => _host.Settings.Favorites);

        // 미리보기는 패널마다 하나씩입니다. 자기 패널에서 고른 파일만 따라갑니다.
        Preview = new PreviewViewModel(host.Settings.Preview, host.Thumbnails);

        NewTabCommand = new RelayCommand(_ => AddTab(SelectedTab?.CurrentPath ?? Settings.StartPath));
        CloseTabCommand = new RelayCommand(
            parameter => CloseTab(parameter as FileListViewModel ?? SelectedTab),
            _ => Tabs.Count > 1);
        CloseOtherTabsCommand = new RelayCommand(_ => CloseOtherTabs(), _ => Tabs.Count > 1);
        SetStartPathCommand = new RelayCommand(_ => SetStartPathToCurrent(), _ => SelectedTab is not null);
        StartupModeCommand = new RelayCommand(parameter => ApplyStartupMode(parameter as string));

        DuplicateTabCommand = new RelayCommand(
            _ => AddTab(SelectedTab?.CurrentPath),
            _ => SelectedTab is not null);

        ReopenTabCommand = new RelayCommand(_ => ReopenClosedTab(), _ => _closed.Count > 0);
    }

    /// <summary>
    /// 이 패널에서 고른 파일이 바뀌면 발생합니다.
    /// 탭마다 따로 오는 신호를 여기서 하나로 모아 미리보기 패널에 넘깁니다.
    /// </summary>
    public event EventHandler<FileItemViewModel?>? SelectionChanged;

    public PaneSettings Settings { get; }

    /// <summary>이 패널 오른쪽에 붙는 미리보기입니다.</summary>
    public PreviewViewModel Preview { get; }

    /// <summary>이 패널 옆에 붙는 폴더 나무입니다. 세로로 긴 칸에서만 보입니다.</summary>
    public FolderTreeViewModel Tree { get; }

    /// <summary>
    /// 탐색 창을 목록의 오른쪽에 붙일지입니다. 창의 오른쪽 끝에 놓인 칸에서 참이 됩니다.
    /// 분할 방식에 따라 창이 정해 줍니다.
    /// </summary>
    public bool TreeOnRight
    {
        get => _treeOnRight;
        set => SetProperty(ref _treeOnRight, value);
    }

    /// <summary>폴더 나무를 보일지 여부입니다. 분할 방식에 따라 창이 정해 줍니다.</summary>
    public bool ShowTree
    {
        get => _showTree;
        set
        {
            if (!SetProperty(ref _showTree, value) || !value)
            {
                return;
            }

            // 접어 둔 동안 즐겨찾기가 바뀌었을 수 있습니다. 펴는 김에 다시 세웁니다.
            Tree.Reload();

            // 이제 막 나무를 폈으면 지금 보고 있는 폴더를 바로 짚어 줍니다.
            FollowInTree(SelectedTab?.CurrentPath);
        }
    }

    public ObservableCollection<FileListViewModel> Tabs { get; } = new();

    public RelayCommand NewTabCommand { get; }

    public RelayCommand CloseTabCommand { get; }

    public RelayCommand CloseOtherTabsCommand { get; }

    public RelayCommand SetStartPathCommand { get; }

    public RelayCommand StartupModeCommand { get; }

    /// <summary>지금 탭과 같은 폴더로 탭을 하나 더 엽니다.</summary>
    public RelayCommand DuplicateTabCommand { get; }

    /// <summary>방금 닫은 탭을 다시 엽니다.</summary>
    public RelayCommand ReopenTabCommand { get; }

    public FileListViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            var previous = _selectedTab;

            if (!SetProperty(ref _selectedTab, value))
            {
                return;
            }

            // 나무가 따라가려면 지금 탭이 어디로 옮겨 가는지 들어야 합니다.
            if (previous is not null)
            {
                previous.PropertyChanged -= OnSelectedTabPropertyChanged;
            }

            if (value is not null)
            {
                value.PropertyChanged += OnSelectedTabPropertyChanged;
            }

            foreach (var tab in Tabs)
            {
                tab.IsSelected = ReferenceEquals(tab, value);
            }

            // 시작할 때 미뤄 둔 탭이면 이제야 폴더를 읽습니다.
            if (value is not null)
            {
                value.EnsureLoadedAsync().Fire("탭 읽기", value);
            }

            SetStartPathCommand.RaiseCanExecuteChanged();
            RaiseSelection(value?.SelectedItem);
            FollowInTree(value?.CurrentPath);
        }
    }

    public ViewMode ViewMode => Settings.ViewMode;

    /// <summary>이 패널에 미리보기를 보일지 여부입니다. 전체 메뉴에서 창별로 켜고 끕니다.</summary>
    public bool ShowPreview
    {
        get => Settings.ShowPreview;
        set
        {
            if (Settings.ShowPreview == value)
            {
                return;
            }

            Settings.ShowPreview = value;
            OnPropertyChanged();
            SyncPreview();
        }
    }

    /// <summary>등록된 즐겨찾기입니다. 탭 오른쪽 클릭 메뉴가 이 목록을 보여 줍니다.</summary>
    public IReadOnlyList<string> Favorites => _host.Settings.Favorites;

    /// <summary>창 전체 설정입니다. 즐겨찾기 편집 창이 직접 고칩니다.</summary>
    public AppSettings AppSettings => _host.Settings;

    /// <summary>되돌리기 기록입니다. 창 전체가 하나를 함께 씁니다.</summary>
    public UndoHistory Undo => _host.Undo;

    /// <summary>지금 보고 있는 폴더입니다. 없으면 빈 문자열입니다.</summary>
    public string CurrentFolderPath => SelectedTab?.CurrentPath ?? string.Empty;

    /// <summary>설정을 파일에 적습니다.</summary>
    public void SaveSettings() => _host.SettingsService.Save(_host.Settings);

    /// <summary>방금 닫은 탭을 다시 엽니다.</summary>
    private void ReopenClosedTab()
    {
        if (_closed.Count == 0)
        {
            return;
        }

        AddTab(_closed.Pop());
        ReopenTabCommand.RaiseCanExecuteChanged();
    }

    /// <summary>미리보기 설정이 밖에서 바뀌었음을 알립니다. 창 설정 창이 고친 뒤에 부릅니다.</summary>
    public void NotifyPreviewChanged()
    {
        OnPropertyChanged(nameof(ShowPreview));
        SyncPreview();
    }

    /// <summary>상태 줄 설정이 바뀌었음을 모든 탭에 알립니다.</summary>
    /// <summary>목록 아이콘 크기가 바뀌었을 때입니다. 받아 둔 그림을 버리고 다시 받습니다.</summary>
    public void NotifyThumbnailSizeChanged()
    {
        foreach (var tab in Tabs)
        {
            tab.NotifyViewModeChanged();
        }
    }

    public void NotifyStatusBarChanged()
    {
        foreach (var tab in Tabs)
        {
            tab.NotifyStatusBarChanged();
        }
    }

    /// <summary>고른 탭을 주어진 주소로 옮깁니다.</summary>
    public void NavigateSelectedTo(string path)
    {
        if (SelectedTab is not { } tab)
        {
            return;
        }

        // 이미 그 폴더를 보고 있으면 할 일이 없습니다.
        // 나무가 목록을 따라가며 마디를 고를 때 되돌아오는 길을 여기서 끊습니다.
        if (SamePath(tab.CurrentPath, path))
        {
            return;
        }

        tab.NavigateAsync(path).Fire("폴더 열기", tab);
    }

    /// <summary>
    /// 이 탭의 폴더를 다른 패널에 새 탭으로 엽니다. 이 패널의 탭은 그대로 둡니다.
    /// 탭 줄에서 오른쪽 단추로 끌어 "여기로 탭 복사"를 골랐을 때 씁니다.
    /// </summary>
    public void CopyTabTo(PaneViewModel target, FileListViewModel tab)
    {
        if (target is null || ReferenceEquals(target, this) || !Tabs.Contains(tab))
        {
            return;
        }

        var path = tab.CurrentPath;

        if (path.Length == 0)
        {
            return;
        }

        target.AddTab(path);
    }

    /// <summary>
    /// 이 탭과 같은 폴더의 탭을 이 패널의 그 차례(0~탭 수)에 새로 끼우고 고릅니다.
    /// 탭 줄에서 Ctrl+끌기, 또는 오른쪽 끌기의 "여기로 탭 복사"에 씁니다.
    /// </summary>
    public void CopyTabAt(FileListViewModel tab, int insertIndex)
    {
        if (!Tabs.Contains(tab) || tab.CurrentPath.Length == 0)
        {
            return;
        }

        var copy = AddTab(tab.CurrentPath);
        MoveTab(copy, Math.Clamp(insertIndex, 0, Tabs.Count - 1));
    }

    /// <summary>
    /// 이 탭을 그 차례(0~탭 수, 옮기기 전 기준)로 옮깁니다. 자기 뒤로 옮기면 한 칸 당겨 셉니다.
    /// 오른쪽 끌기의 "여기로 탭 이동"에 씁니다.
    /// </summary>
    public void PlaceTab(FileListViewModel tab, int insertIndex)
    {
        var current = Tabs.IndexOf(tab);

        if (current < 0)
        {
            return;
        }

        var target = insertIndex > current ? insertIndex - 1 : insertIndex;
        MoveTab(tab, Math.Clamp(target, 0, Tabs.Count - 1));
        SelectedTab = tab;
    }

    /// <summary>이 탭을 다른 패널로 옮깁니다. 탭 줄에서 끌어다 옮길 때 씁니다.</summary>
    public void MoveTabTo(PaneViewModel target, FileListViewModel tab)
    {
        if (target is null || ReferenceEquals(target, this) || !Tabs.Contains(tab))
        {
            return;
        }

        var path = tab.CurrentPath;

        if (path.Length == 0)
        {
            return;
        }

        target.AddTab(path);

        // 마지막 한 장을 넘기면 이 패널이 빈 채로 남습니다. 시작 위치를 한 장 열어 둡니다.
        if (Tabs.Count <= 1)
        {
            AddTab(Settings.StartPath);
        }

        CloseTab(tab);
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private void OnSelectedTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileListViewModel.CurrentPath) && ReferenceEquals(sender, _selectedTab))
        {
            FollowInTree(_selectedTab?.CurrentPath);
        }
    }

    /// <summary>목록이 옮겨 간 자리를 나무도 따라가게 합니다. 나무를 접어 두었으면 하지 않습니다.</summary>
    private void FollowInTree(string? path)
    {
        if (!ShowTree || string.IsNullOrEmpty(path))
        {
            return;
        }

        Tree.Reveal(path);
    }

    /// <summary>메뉴에 그대로 넣을 문구입니다. XAML의 StringFormat은 콜론 때문에 까다로워 여기서 만듭니다.</summary>
    public string StartPathDisplay => "시작 위치: " + (string.IsNullOrWhiteSpace(Settings.StartPath)
        ? "(지정 안 됨)"
        : SpecialPaths.ToDisplay(Settings.StartPath));

    public bool IsStartPathMode => Settings.StartupMode == PaneStartupMode.StartPath;

    public bool IsRestoreTabsMode => Settings.StartupMode == PaneStartupMode.RestoreTabs;

    /// <summary>
    /// 시작 방식에 따라 첫 탭들을 엽니다.
    /// 지난 탭을 복원하는 경우에도 열 수 있는 탭이 하나도 없으면 시작 위치로 떨어집니다.
    /// </summary>
    public void Initialize()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            InitializeCore();
        }
        finally
        {
            DiagnosticLog.Write($"{Settings.Index}번 칸 세우기 {watch.ElapsedMilliseconds}ms, 탭 {Tabs.Count}장");
        }
    }

    private void InitializeCore()
    {
        if (Settings.StartupMode == PaneStartupMode.RestoreTabs && Settings.LastTabs.Count > 0)
        {
            var colors = RestoreColors(Settings.LastTabs);

            for (var index = 0; index < Settings.LastTabs.Count && index < MaxTabs; index++)
            {
                AddTab(Settings.LastTabs[index], select: false, defer: true, favoriteIndex: colors[index]);
            }

            if (Tabs.Count > 0)
            {
                var index = Math.Clamp(Settings.LastActiveTab, 0, Tabs.Count - 1);
                SelectedTab = Tabs[index];
                return;
            }
        }

        OpenDefaultTabs();
    }

    /// <summary>
    /// 되살릴 탭들이 몇 번 즐겨찾기였는지 알아냅니다.
    ///
    /// 설정 파일에 적어 둔 차례가 있으면 그대로 씁니다.
    /// 차례를 적기 전에 저장된 파일이면 경로로 맞춰 봅니다.
    /// 즐겨찾기 탭은 시작 위치 탭 뒤에 붙으므로 뒤에서부터 찾습니다.
    /// 그래야 시작 위치와 같은 폴더의 즐겨찾기가 앞쪽 작업 탭을 가로채지 않습니다.
    /// </summary>
    private int[] RestoreColors(IReadOnlyList<string> paths)
    {
        var result = new int[paths.Count];
        Array.Fill(result, -1);

        var stored = Settings.LastTabColors;

        if (stored is not null && stored.Count >= paths.Count)
        {
            for (var index = 0; index < paths.Count; index++)
            {
                result[index] = stored[index];
            }

            return result;
        }

        var favorites = _host.Settings.Favorites;

        for (var favorite = 0; favorite < favorites.Count && favorite < AppSettings.MaxFavorites; favorite++)
        {
            var target = PathHelper.ResolveExistingDirectory(favorites[favorite]);

            for (var index = paths.Count - 1; index >= 0; index--)
            {
                if (result[index] >= 0 || !SamePath(PathHelper.ResolveExistingDirectory(paths[index]), target))
                {
                    continue;
                }

                result[index] = favorite;
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// 탭을 모두 닫고 처음 상태로 되돌립니다.
    /// 시작 위치 탭 하나만 남습니다.
    /// </summary>
    public void ResetTabs()
    {
        foreach (var tab in Tabs)
        {
            tab.SelectionChanged -= OnTabSelectionChanged;
            tab.Dispose();
        }

        Tabs.Clear();
        SelectedTab = null;

        OpenDefaultTabs();
        RaiseTabCommandState();
    }

    /// <summary>시작 위치 탭 하나를 엽니다.</summary>
    private void OpenDefaultTabs()
    {
        // 시작 위치 탭 하나만 엽니다.
        // 즐겨찾기는 제목 표시줄의 단추로 언제든 열 수 있으므로 탭으로 미리 깔지 않습니다.
        AddTab(Settings.StartPath);

        if (Tabs.Count > 0)
        {
            SelectedTab = Tabs[0];
        }
    }

    /// <param name="defer">
    /// 고르지 않은 탭이면 폴더를 읽지 않고 이름만 걸어 둡니다.
    /// 창이 뜨는 동안 네 패널이 탭 수만큼 폴더를 읽으면 그만큼 늦어집니다.
    /// </param>
    /// <param name="favoriteIndex">
    /// 즐겨찾기 차례입니다. 즐겨찾기로 여는 고정 탭만 이 값을 받아 색 띠를 답니다.
    /// 보통 탭은 -1 이라 색이 붙지 않습니다.
    /// </param>
    public FileListViewModel AddTab(string? path, bool select = true, bool defer = false, int favoriteIndex = -1)
    {
        var tab = new FileListViewModel(
            this,
            _host.Settings.View,
            _host.DirectoryEnumerator,
            _host.Thumbnails,
            _host.Search,
            _host.Settings.Search);
        tab.SelectionChanged += OnTabSelectionChanged;
        tab.ApplyTabColor(favoriteIndex);
        Tabs.Add(tab);

        var resolved = PathHelper.ResolveExistingDirectory(path);
        var chosen = select || SelectedTab is null;

        // 미뤄 두라고 한 탭은 고르지 않은 한 읽지 않습니다.
        // 어느 탭을 고를지는 부르는 쪽이 마지막에 정합니다.
        if (defer && !select)
        {
            tab.Defer(resolved);
        }
        else
        {
            if (chosen)
            {
                SelectedTab = tab;
            }

            tab.NavigateAsync(resolved).Fire("폴더 열기", tab);
        }

        RaiseTabCommandState();
        return tab;
    }

    /// <summary>
    /// 탭 줄에 붙지 않는 목록을 하나 만듭니다. 검색 창이 이것을 안에 담아 씁니다.
    /// 보기 방식과 아이콘 크기는 이 칸의 것을 그대로 따릅니다.
    /// </summary>
    public FileListViewModel CreateDetachedList() =>
        new(
            this,
            _host.Settings.View,
            _host.DirectoryEnumerator,
            _host.Thumbnails,
            _host.Search,
            _host.Settings.Search);

    public void CloseTab(FileListViewModel? tab)
    {
        // 마지막 한 장은 닫지 않습니다. 빈 패널은 할 수 있는 일이 없습니다.
        if (tab is null || Tabs.Count <= 1)
        {
            return;
        }

        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        if (tab.CurrentPath.Length > 0)
        {
            _closed.Push(tab.CurrentPath);

            while (_closed.Count > 20)
            {
                // 오래된 것은 버립니다. 스택이라 바닥을 덜어 내려면 한 번 뒤집어야 합니다.
                var keep = _closed.ToArray();
                _closed.Clear();

                for (var i = keep.Length - 2; i >= 0; i--)
                {
                    _closed.Push(keep[i]);
                }
            }
        }

        Tabs.RemoveAt(index);
        tab.SelectionChanged -= OnTabSelectionChanged;
        tab.Dispose();

        if (ReferenceEquals(SelectedTab, tab))
        {
            SelectedTab = Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
        }

        RaiseTabCommandState();
    }

    private void CloseOtherTabs()
    {
        var keep = SelectedTab;
        if (keep is null)
        {
            return;
        }

        foreach (var tab in Tabs.Where(t => !ReferenceEquals(t, keep)).ToList())
        {
            Tabs.Remove(tab);
            tab.SelectionChanged -= OnTabSelectionChanged;
            tab.Dispose();
        }

        RaiseTabCommandState();
    }

    /// <summary>
    /// 열 머리글을 눌렀을 때입니다. 정렬은 패널 전체의 설정이라 모든 탭에 함께 적용합니다.
    /// </summary>
    public void ApplySort(string? columnName)
    {
        if (!Enum.TryParse<SortColumn>(columnName, ignoreCase: true, out var column))
        {
            return;
        }

        if (Settings.SortColumn == column)
        {
            Settings.SortDirection = Settings.SortDirection == SortDirection.Ascending
                ? SortDirection.Descending
                : SortDirection.Ascending;
        }
        else
        {
            Settings.SortColumn = column;
            Settings.SortDirection = SortDirection.Ascending;
        }

        foreach (var tab in Tabs)
        {
            tab.NotifySortChanged();
        }
    }

    /// <summary>
    /// 폴더를 먼저 모아 정렬할지 정합니다. 이 패널의 탭이 모두 함께 바뀌고 설정에 남습니다.
    /// </summary>
    public void SetFoldersFirst(bool foldersFirst)
    {
        if (Settings.FoldersFirst == foldersFirst)
        {
            return;
        }

        Settings.FoldersFirst = foldersFirst;
        SaveSettings();

        foreach (var tab in Tabs)
        {
            tab.NotifySortChanged();
        }
    }

    /// <summary>
    /// 숨김·시스템 파일을 보일지 뒤집습니다. 이 패널의 탭이 모두 함께 바뀝니다.
    /// </summary>
    public void ToggleHiddenFiles()
    {
        Settings.ShowHiddenFiles = !Settings.ShowHiddenFiles;
        OnPropertyChanged(nameof(ShowHiddenFiles));

        foreach (var tab in Tabs)
        {
            tab.NotifyHiddenFilesChanged();
        }
    }

    /// <summary>
    /// 칸 안에서 탐색 창이 차지하는 비율입니다. 창 전체가 같은 값을 씁니다.
    /// 가룻대를 놓으면 이 값이 바뀌고 설정에 남습니다.
    /// </summary>
    public double TreeSplit
    {
        get => _host.Settings.TreeSplit;
        set
        {
            var wanted = Math.Clamp(value, 0.15, 0.7);

            if (Math.Abs(wanted - _host.Settings.TreeSplit) < 0.005)
            {
                return;
            }

            _host.Settings.TreeSplit = wanted;
            _host.SettingsService.Save(_host.Settings);
            OnPropertyChanged(nameof(TreeSplit));
        }
    }

    /// <summary>즐겨찾기를 고쳤다고 창 전체에 알립니다. 네 칸의 탐색 창이 같이 바뀝니다.</summary>
    public void NotifyFavoritesChanged() => _host.NotifyFavoritesChanged();

    /// <summary>즐겨찾기를 고쳤습니다. 나무 맨 위 줄을 다시 세웁니다.</summary>
    public void RefreshFavorites()
    {
        if (!ShowTree)
        {
            return;
        }

        Tree.Reload();
        FollowInTree(SelectedTab?.CurrentPath);
    }

    /// <summary>설정에서 탐색 창 너비를 되돌렸다고 화면에 알립니다.</summary>
    public void NotifyTreeSplitChanged() => OnPropertyChanged(nameof(TreeSplit));

    /// <summary>숨김·시스템 파일을 목록에 보이는 중인지입니다.</summary>
    public bool ShowHiddenFiles => Settings.ShowHiddenFiles;

    /// <summary>
    /// 폴더 나무를 처음부터 다시 읽습니다. 드라이브 목록도 다시 봅니다.
    /// 펼쳐 두었던 가지는 접히고, 지금 보고 있는 폴더까지는 다시 펴 줍니다.
    /// </summary>
    public void RefreshTree()
    {
        if (!ShowTree)
        {
            return;
        }

        Tree.Reload();
        FollowInTree(SelectedTab?.CurrentPath);
    }

    public void ApplyViewMode(string? modeName)
    {
        if (!Enum.TryParse<ViewMode>(modeName, ignoreCase: true, out var mode) || Settings.ViewMode == mode)
        {
            return;
        }

        Settings.ViewMode = mode;
        OnPropertyChanged(nameof(ViewMode));

        foreach (var tab in Tabs)
        {
            tab.NotifyViewModeChanged();
        }
    }

    /// <summary>아이콘 보기에서 Ctrl+휠을 굴렸을 때 아이콘 크기를 한 단계 옮깁니다.</summary>
    public void AdjustIconSize(int direction)
    {
        if (Settings.ViewMode != ViewMode.LargeIcons || direction == 0)
        {
            return;
        }

        var current = Settings.IconSize;
        var next = current;

        if (direction > 0)
        {
            foreach (var step in IconSizeSteps)
            {
                if (step > current)
                {
                    next = step;
                    break;
                }
            }
        }
        else
        {
            for (var index = IconSizeSteps.Length - 1; index >= 0; index--)
            {
                if (IconSizeSteps[index] < current)
                {
                    next = IconSizeSteps[index];
                    break;
                }
            }
        }

        if (next == current)
        {
            return;
        }

        Settings.IconSize = next;

        foreach (var tab in Tabs)
        {
            tab.NotifyViewModeChanged();
        }

        SaveSettings();
    }

    /// <summary>아이콘 크기의 단계입니다. 탐색기처럼 미끄러지듯이가 아니라 단계로 움직입니다.</summary>
    private static readonly int[] IconSizeSteps = { 32, 48, 64, 96, 128, 160, 200, 256 };

    /// <summary>검색을 쓸 수 있게 됐다고 탭들에 알립니다.</summary>
    public void NotifySearchReady()
    {
        foreach (var tab in Tabs)
        {
            tab.NotifySearchReady();
        }
    }

    private void SetStartPathToCurrent()
    {
        if (SelectedTab is null || SelectedTab.CurrentPath.Length == 0)
        {
            return;
        }

        Settings.StartPath = SelectedTab.CurrentPath;
        OnPropertyChanged(nameof(StartPathDisplay));
    }

    private void ApplyStartupMode(string? modeName)
    {
        if (!Enum.TryParse<PaneStartupMode>(modeName, ignoreCase: true, out var mode))
        {
            return;
        }

        Settings.StartupMode = mode;
        OnPropertyChanged(nameof(IsStartPathMode));
        OnPropertyChanged(nameof(IsRestoreTabsMode));
    }

    /// <summary>창을 닫을 때 지금 열려 있는 탭을 설정에 적어 둡니다.</summary>
    public void PersistState()
    {
        var paths = new List<string>(Tabs.Count);
        var colors = new List<int>(Tabs.Count);

        foreach (var tab in Tabs)
        {
            if (tab.CurrentPath.Length > 0)
            {
                paths.Add(tab.CurrentPath);

                // 색은 경로가 아니라 즐겨찾기 차례에서 나옵니다. 차례를 같이 적어 둡니다.
                colors.Add(tab.FavoriteIndex);
            }
        }

        Settings.LastTabs = paths;
        Settings.LastTabColors = colors;
        Settings.LastActiveTab = SelectedTab is null ? 0 : Math.Max(Tabs.IndexOf(SelectedTab), 0);
    }

    private void OnTabSelectionChanged(object? sender, FileItemViewModel? item)
    {
        // 보이지 않는 탭에서 온 신호는 흘려보냅니다.
        if (ReferenceEquals(sender, SelectedTab))
        {
            RaiseSelection(item);
        }
    }

    private void RaiseSelection(FileItemViewModel? item)
    {
        // 미리보기를 접어 둔 칸에서는 미리보기를 부르지 않습니다.
        // 보이지 않아도 PDF·오피스 문서는 만든 회사의 미리보기 프로그램을 띄우는데,
        // 그 프로그램과 주고받는 동안 화면 갈래가 몇 초씩 멈춥니다.
        if (ShowPreview)
        {
            Preview.SetTarget(item);
        }

        SelectionChanged?.Invoke(this, item);
    }

    /// <summary>
    /// 미리보기를 켜고 끈 것을 미리보기에 옮깁니다.
    /// 켜면 지금 고른 것을 보여 주고, 끄면 붙들고 있던 문서를 내려놓습니다.
    /// </summary>
    private void SyncPreview() =>
        Preview.SetTarget(ShowPreview ? SelectedTab?.SelectedItem : null);

    /// <summary>탭을 끌어 옮겼을 때 순서를 바꿉니다.</summary>
    public void MoveTab(FileListViewModel tab, int targetIndex)
    {
        var current = Tabs.IndexOf(tab);

        if (current < 0 || targetIndex < 0 || targetIndex >= Tabs.Count || current == targetIndex)
        {
            return;
        }

        Tabs.Move(current, targetIndex);
    }

    private void RaiseTabCommandState()
    {
        CloseTabCommand.RaiseCanExecuteChanged();
        CloseOtherTabsCommand.RaiseCanExecuteChanged();
        DuplicateTabCommand.RaiseCanExecuteChanged();
        ReopenTabCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_selectedTab is not null)
        {
            _selectedTab.PropertyChanged -= OnSelectedTabPropertyChanged;
        }

        foreach (var tab in Tabs)
        {
            tab.SelectionChanged -= OnTabSelectionChanged;
            tab.Dispose();
        }

        Tabs.Clear();
        Preview.Dispose();
    }
}
