using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Explorer.App.Services;
using Explorer.Core.Abstractions;
using Explorer.Core.Models;
using Explorer.Core.Services;
using Explorer.Core.Settings;
using Explorer.Shell.Operations;

namespace Explorer.App.ViewModels;

/// <summary>
/// 탭 하나입니다. 현재 폴더, 목록, 이동 이력을 들고 있습니다.
/// 정렬과 보기 방식은 패널 전체가 공유하므로 <see cref="PaneViewModel"/>이 맡고,
/// 여기서는 그 결과를 받아 다시 그리기만 합니다.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FileListViewModel : ObservableObject, IDisposable, IProblemSink
{
    private readonly PaneViewModel _owner;
    private readonly PaneSettings _pane;
    private readonly ViewSettings _view;
    private readonly IDirectoryEnumerator _enumerator;
    private readonly ThumbnailCache _thumbnails;
    private readonly EverythingSearchService _search;
    private readonly SearchSettings _searchSettings;
    private readonly NavigationHistory _history = new();
    private readonly DebouncedDirectoryWatcher _watcher;
    private readonly Dispatcher _dispatcher;

    private CancellationTokenSource? _navigation;
    private IReadOnlyList<FileSystemEntry> _entries = Array.Empty<FileSystemEntry>();
    private IReadOnlyList<FileItemViewModel> _items = Array.Empty<FileItemViewModel>();
    private FileItemViewModel? _selectedItem;
    private string _currentPath = string.Empty;

    /// <summary>아직 읽지 않고 미뤄 둔 폴더입니다. 읽고 나면 null 이 됩니다.</summary>
    private string? _deferredPath;

    /// <summary>
    /// 휴지통을 보고 있을 때, 휴지통 안의 자리와 원래 자리를 짝지은 표입니다.
    /// 되돌리려면 원래 자리를 알아야 하는데 목록 항목에는 담을 자리가 없습니다.
    /// </summary>
    private Dictionary<string, string> _recycled = new(StringComparer.OrdinalIgnoreCase);

    private string? _tabColorHex;
    private bool _hasTabColor;
    private System.Windows.Media.Brush _tabFillBrush = System.Windows.Media.Brushes.Transparent;
    private System.Windows.Media.Brush _tabEdgeBrush = System.Windows.Media.Brushes.Transparent;
    private System.Windows.Thickness _tabEdgeThickness;

    /// <summary>지금 고른 것들의 경로입니다. 폴더 크기를 셀 때 씁니다.</summary>
    private IReadOnlyList<string> _selectedPaths = Array.Empty<string>();

    /// <summary>폴더 크기를 세는 중인 일감입니다. 멈추기로 끊습니다.</summary>
    private CancellationTokenSource? _measure;

    private bool _isMeasuring;

    private string _measuredText = string.Empty;
    private string _addressText = string.Empty;
    private string _statusText = string.Empty;
    private string _selectionText = string.Empty;
    private bool _isLoading;
    private bool _isSelected;
    private bool _isSearching;
    private string _searchTerm = string.Empty;
    private bool _disposed;

    // 우리가 직접 만든 변경까지 감시기가 다시 새로고침하면 편집 중인 이름이 날아갑니다.
    private DateTime _suppressWatchUntil = DateTime.MinValue;

    public FileListViewModel(
        PaneViewModel owner,
        ViewSettings view,
        IDirectoryEnumerator enumerator,
        ThumbnailCache thumbnails,
        EverythingSearchService search,
        SearchSettings searchSettings)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _pane = owner.Settings;
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
        _thumbnails = thumbnails ?? throw new ArgumentNullException(nameof(thumbnails));
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _searchSettings = searchSettings ?? throw new ArgumentNullException(nameof(searchSettings));

        _dispatcher = Dispatcher.CurrentDispatcher;

        _watcher = new DebouncedDirectoryWatcher(view.WatchDebounceMs);
        _watcher.Changed += OnDirectoryChanged;

        BackCommand = new RelayCommand(_ => GoBackAsync().Fire("뒤로 가기", this), _ => _history.CanGoBack);
        ForwardCommand = new RelayCommand(_ => GoForwardAsync().Fire("앞으로 가기", this), _ => _history.CanGoForward);
        UpCommand = new RelayCommand(_ => GoUpAsync().Fire("위로 가기", this), _ => HasParent());
        RefreshCommand = new RelayCommand(_ => RefreshEverythingAsync().Fire("새로 고침", this));
        OpenSelectedCommand = new RelayCommand(_ => OpenAsync(SelectedItem).Fire("열기", this));
        NewFolderCommand = new RelayCommand(_ => CreateFolderAsync().Fire("새 폴더 만들기", this), _ => CanCreateFolder());

        // 정렬과 보기 방식은 패널 전체의 설정이라 소유자에게 넘깁니다.
        SortCommand = new RelayCommand(parameter => _owner.ApplySort(parameter as string));
        ViewModeCommand = new RelayCommand(parameter => _owner.ApplyViewMode(parameter as string));
        CloseTabCommand = new RelayCommand(_ => _owner.CloseTab(this));

        MeasureCommand = new RelayCommand(_ => MeasureAsync().Fire("폴더 크기 세기", this));

        SearchCommand = new RelayCommand(
            _ => BeginSearch(),
            _ => _search.IsAvailable || (_searchSettings.FallbackToEnumerate && CanWalkCurrentFolder()));

        NavigateToCrumbCommand = new RelayCommand(parameter =>
        {
            if (parameter is string path && path.Length > 0)
            {
                NavigateAsync(path).Fire("폴더 열기", this);
            }
        });
    }

    /// <summary>선택이 바뀔 때 발생합니다. 5단계에서 미리보기 패널이 이 신호를 받습니다.</summary>
    public event EventHandler<FileItemViewModel?>? SelectionChanged;

    /// <summary>
    /// 특정 줄을 화면 안으로 끌어와 달라는 신호입니다.
    /// 가상화 때문에 화면 밖의 줄은 아직 만들어지지 않아, 스크롤해야 편집 상자가 생깁니다.
    /// </summary>
    public event EventHandler<FileItemViewModel>? EnsureVisibleRequested;

    public RelayCommand BackCommand { get; }

    public RelayCommand ForwardCommand { get; }

    public RelayCommand UpCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand OpenSelectedCommand { get; }

    public RelayCommand NewFolderCommand { get; }

    public RelayCommand SortCommand { get; }

    public RelayCommand ViewModeCommand { get; }

    public RelayCommand CloseTabCommand { get; }

    /// <summary>
    /// 이름으로 찾기입니다.
    /// Everything 이 켜져 있으면 온 컴퓨터에서, 아니면 지금 폴더 아래를 직접 훑습니다.
    /// </summary>
    public RelayCommand SearchCommand { get; }

    /// <summary>주소 표시줄의 경로 조각을 눌렀을 때 그 폴더로 옮겨 갑니다.</summary>
    public RelayCommand NavigateToCrumbCommand { get; }

    /// <summary>폴더 크기 세기를 시작하거나 멈춥니다.</summary>
    public RelayCommand MeasureCommand { get; }

    /// <summary>폴더 크기를 세는 중인지입니다.</summary>
    public bool IsMeasuring
    {
        get => _isMeasuring;
        private set
        {
            if (SetProperty(ref _isMeasuring, value))
            {
                OnPropertyChanged(nameof(MeasureText));
            }
        }
    }

    /// <summary>크기 세기 단추에 보일 글입니다.</summary>
    public string MeasureText => IsMeasuring ? "멈추기" : "폴더 크기";

    /// <summary>세어 본 결과입니다. 아직 세지 않았으면 빈 글입니다.</summary>
    public string MeasuredText
    {
        get => _measuredText;
        private set => SetProperty(ref _measuredText, value);
    }

    /// <summary>
    /// 고른 것들의 크기를 셉니다. 고른 것이 없으면 지금 폴더를 셉니다.
    /// 세는 중에 다시 부르면 멈춥니다.
    /// </summary>
    public async Task MeasureAsync()
    {
        if (IsMeasuring)
        {
            _measure?.Cancel();
            return;
        }

        var targets = _selectedPaths.Count > 0
            ? _selectedPaths
            : CurrentPath.Length > 0 && !SpecialPaths.IsVirtual(CurrentPath)
                ? new[] { CurrentPath }
                : Array.Empty<string>();

        if (targets.Count == 0)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _measure = cancellation;

        IsMeasuring = true;
        MeasuredText = "세는 중…";

        var progress = new Progress<long>(bytes =>
        {
            if (!cancellation.IsCancellationRequested)
            {
                MeasuredText = $"{ByteSizeFormatter.Format(bytes)} 세는 중…";
            }
        });

        try
        {
            var total = await FolderSize
                .MeasureAsync(targets, progress, cancellation.Token)
                .ConfigureAwait(true);

            MeasuredText = $"{targets.Count}개 · {ByteSizeFormatter.Format(total)}";
        }
        catch (OperationCanceledException)
        {
            MeasuredText = "세기를 멈췄습니다.";
        }
        finally
        {
            if (ReferenceEquals(_measure, cancellation))
            {
                _measure = null;
                IsMeasuring = false;
            }
        }
    }

    /// <summary>
    /// 주소 표시줄에 늘어놓을 경로 조각입니다.
    /// 맨 앞은 언제나 "내 PC"이고, 그 뒤로 드라이브와 폴더 이름이 이어집니다.
    /// </summary>
    public IReadOnlyList<PathCrumb> Crumbs
    {
        get
        {
            var crumbs = new List<PathCrumb>();

            if (CurrentPath.Length == 0)
            {
                return crumbs;
            }

            crumbs.Add(new PathCrumb(SpecialPaths.MyComputerDisplayName, SpecialPaths.MyComputer));

            if (SpecialPaths.IsMyComputer(CurrentPath))
            {
                return crumbs;
            }

            if (SpecialPaths.IsRecycleBin(CurrentPath))
            {
                crumbs.Add(new PathCrumb(SpecialPaths.RecycleBinDisplayName, SpecialPaths.RecycleBin));
                return crumbs;
            }

            string full;
            string root;

            try
            {
                full = Path.GetFullPath(CurrentPath).TrimEnd(Path.DirectorySeparatorChar);
                root = Path.GetPathRoot(CurrentPath) ?? string.Empty;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return crumbs;
            }

            if (root.Length == 0)
            {
                return crumbs;
            }

            // 드라이브는 "C:" 대신 "C:"로 보여 줍니다. 누르면 뿌리로 갑니다.
            crumbs.Add(new PathCrumb(root.TrimEnd(Path.DirectorySeparatorChar), root));

            var rest = full.Length > root.Length ? full[root.Length..] : string.Empty;
            var walked = root;

            foreach (var part in rest.Split(
                         Path.DirectorySeparatorChar,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                walked = Path.Combine(walked, part);
                crumbs.Add(new PathCrumb(part, walked));
            }

            return crumbs;
        }
    }

    /// <summary>주소 표시줄로 옮겨 가 검색어를 받아 달라는 신호입니다.</summary>
    public event EventHandler? SearchRequested;

    /// <summary>지금 검색 결과를 보고 있는지 여부입니다.</summary>
    public bool IsSearching
    {
        get => _isSearching;
        private set => SetProperty(ref _isSearching, value);
    }

    public IReadOnlyList<FileItemViewModel> Items
    {
        get => _items;
        private set => SetProperty(ref _items, value);
    }

    public FileItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                SelectionChanged?.Invoke(this, value);
            }
        }
    }

    public string CurrentPath
    {
        get => _currentPath;
        private set
        {
            if (SetProperty(ref _currentPath, value))
            {
                OnPropertyChanged(nameof(Header));
                OnPropertyChanged(nameof(Crumbs));
            }
        }
    }

    /// <summary>탭에 표시할 이름입니다. 폴더 이름만 쓰고, 드라이브 루트면 경로 그대로 씁니다.</summary>
    public string Header
    {
        get
        {
            if (_searchTerm.Length > 0)
            {
                return "검색: " + _searchTerm;
            }

            if (CurrentPath.Length == 0)
            {
                return "새 탭";
            }

            if (SpecialPaths.IsMyComputer(CurrentPath))
            {
                return SpecialPaths.MyComputerDisplayName;
            }

            if (SpecialPaths.IsRecycleBin(CurrentPath))
            {
                return SpecialPaths.RecycleBinDisplayName;
            }

            var trimmed = CurrentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            return name.Length > 0 ? name : CurrentPath;
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                // 고른 탭은 색을 짙게 칠합니다.
                RefreshTabBrushes();
            }
        }
    }

    /// <summary>주소 표시줄의 글자입니다. 사용자가 고치는 동안에는 CurrentPath와 다를 수 있습니다.</summary>
    public string AddressText
    {
        get => _addressText;
        set => SetProperty(ref _addressText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>지금 고른 항목의 개수와 크기입니다. 고른 것이 없으면 빈 글입니다.</summary>
    public string SelectionText
    {
        get => _selectionText;
        private set => SetProperty(ref _selectionText, value);
    }

    /// <summary>화면에서 지금 고른 항목의 경로입니다. 제목 띠의 일괄 인쇄 단추가 읽어 갑니다.</summary>
    public IReadOnlyList<string> SelectedPaths => _selectedPaths;

    /// <summary>화면에서 고른 것이 바뀔 때마다 알려 줍니다.</summary>
    public void ReportSelection(IReadOnlyList<FileItemViewModel> items)
    {
        using var step = UiWatch.Step($"고른 것 알리기 {items.Count}개");

        // 다른 것을 골랐으면 세던 크기는 그 자리에서 뜻을 잃습니다.
        _measure?.Cancel();
        MeasuredText = string.Empty;

        if (items.Count == 0)
        {
            _selectedPaths = Array.Empty<string>();
            SelectionText = string.Empty;
            return;
        }

        var paths = new List<string>(items.Count);
        var bytes = 0L;
        var folders = 0;

        foreach (var item in items)
        {
            paths.Add(item.FullPath);

            if (item.IsDirectory)
            {
                folders++;
            }
            else
            {
                bytes += item.Entry.Size;
            }
        }

        _selectedPaths = paths;

        // 폴더는 여기서 크기를 세지 않습니다. 안을 다 뒤져야 하는 일이라, 사람이 시킬 때만 셉니다.
        SelectionText = folders == items.Count
            ? $"{items.Count}개 선택"
            : $"{items.Count}개 선택 · {ByteSizeFormatter.Format(bytes)}";
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public SortColumn SortColumn => _pane.SortColumn;

    public SortDirection SortDirection => _pane.SortDirection;

    public ViewMode ViewMode => _pane.ViewMode;

    /// <summary>
    /// 폴더(와 바로가기)를 파일보다 먼저 모아 정렬할지입니다. 보기 방식 메뉴가 켜고 끕니다.
    /// 메뉴는 둘 가운데 하나를 고르는 모양이라, 켜는 쪽만 받고 끄는 쪽은 받지 않습니다.
    /// 이미 골라 둔 줄을 다시 눌러도 체크가 풀리지 않게 하려는 것입니다.
    /// </summary>
    public bool FoldersFirst
    {
        get => _pane.FoldersFirst;
        set
        {
            if (value)
            {
                _owner.SetFoldersFirst(true);
            }

            OnPropertyChanged(nameof(FoldersFirst));
            OnPropertyChanged(nameof(MixFoldersAndFiles));
        }
    }

    /// <summary>폴더와 파일을 가리지 않고 한 줄로 정렬할지입니다. <see cref="FoldersFirst"/> 의 반대편입니다.</summary>
    public bool MixFoldersAndFiles
    {
        get => !_pane.FoldersFirst;
        set
        {
            if (value)
            {
                _owner.SetFoldersFirst(false);
            }

            OnPropertyChanged(nameof(FoldersFirst));
            OnPropertyChanged(nameof(MixFoldersAndFiles));
        }
    }

    /// <summary>
    /// 이 탭을 떠날 때의 스크롤 자리입니다.
    /// 패널 하나에 목록 화면은 하나뿐이라, 탭을 오가면 자리가 처음으로 돌아갑니다.
    /// 그래서 탭마다 따로 적어 두었다가 돌아올 때 되돌립니다.
    /// </summary>
    public double ScrollOffset { get; set; }

    /// <summary>목록 보기는 옆으로 늘어나므로 가로 자리도 함께 적어 둡니다.</summary>
    public double HorizontalScrollOffset { get; set; }

    /// <summary>아래 상태 줄을 보일지 여부입니다. 창 전체에 함께 적용됩니다.</summary>
    public bool ShowStatusBar => _view.ShowStatusBar;

    /// <summary>작업이 실패했을 때 상태 줄에 한 줄 남깁니다.</summary>
    public void ReportProblem(string message) => StatusText = message;

    /// <summary>되돌리기 기록입니다. 일괄 이름 바꾸기 창이 자기 일을 여기에 적습니다.</summary>
    public UndoHistory Undo => _owner.Undo;

    /// <summary>목록 겉모습 설정입니다. 자세히 보기의 열 구성을 화면 쪽이 여기서 읽고 씁니다.</summary>
    public ViewSettings ViewOptions => _view;

    /// <summary>상태 줄 설정이 바뀌었을 때 소유자가 부릅니다.</summary>
    public void NotifyStatusBarChanged() => OnPropertyChanged(nameof(ShowStatusBar));

    /// <summary>지금 보기 방식에서 쓸 썸네일 크기입니다. 아이콘 보기에서만 커집니다.</summary>
    public int ThumbnailPixelSize => _pane.ViewMode == ViewMode.LargeIcons
        ? _pane.IconSize
        : _view.ThumbnailSize;

    /// <summary>아이콘 보기에서 한 칸의 너비입니다. 아이콘보다 조금 넓어야 이름이 들어갑니다.</summary>
    public double TileWidth => _pane.IconSize + 24;

    /// <summary>아이콘 보기에서 한 칸의 높이입니다. 아이콘 아래에 이름 두 줄이 들어갑니다.</summary>
    public double TileHeight => _pane.IconSize + 58;

    /// <summary>목록 보기에서 한 열의 너비입니다.</summary>
    public double ListColumnWidth => 220;

    /// <summary>목록 보기에서 한 줄의 높이입니다.</summary>
    public double ListRowHeight => _view.ThumbnailSize + 8;

    /// <summary>패널의 정렬이 바뀌었을 때 소유자가 부릅니다.</summary>
    public void NotifySortChanged()
    {
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortDirection));
        OnPropertyChanged(nameof(FoldersFirst));
        OnPropertyChanged(nameof(MixFoldersAndFiles));
        RebuildItems(preserveSelection: true);
    }

    /// <summary>숨김 파일 설정이 바뀌었을 때 소유자가 부릅니다.</summary>
    public void NotifyHiddenFilesChanged()
    {
        OnPropertyChanged(nameof(ShowHiddenFiles));

        // 아직 읽지 않고 미뤄 둔 탭은 그대로 둡니다. 고를 때 새 설정으로 읽습니다.
        if (_deferredPath is not null)
        {
            return;
        }

        // 목록을 다시 읽어야 합니다. 걸러내는 일이 폴더를 읽는 쪽에서 일어나기 때문입니다.
        NavigateAsync(CurrentPath, recordHistory: false).Fire("새로 고침", this);
    }

    /// <summary>
    /// 숨김·시스템 파일을 목록에 보일지입니다.
    /// 메뉴가 체크 표시를 이 값에 걸어 두고, 눌린 결과도 이리로 들어옵니다.
    /// </summary>
    public bool ShowHiddenFiles
    {
        get => _pane.ShowHiddenFiles;
        set
        {
            if (value != _pane.ShowHiddenFiles)
            {
                _owner.ToggleHiddenFiles();
            }
        }
    }

    /// <summary>패널의 보기 방식이 바뀌었을 때 소유자가 부릅니다.</summary>
    public void NotifyViewModeChanged()
    {
        // 보기 방식이 바뀌면 썸네일 크기도 달라지므로 이미 받아 둔 그림을 버립니다.
        foreach (var item in Items)
        {
            item.Thumbnail = null;
        }

        OnPropertyChanged(nameof(ViewMode));
        OnPropertyChanged(nameof(ThumbnailPixelSize));
        OnPropertyChanged(nameof(TileWidth));
        OnPropertyChanged(nameof(TileHeight));
    }

    /// <summary>
    /// 폴더를 엽니다. 이전 이동이 아직 끝나지 않았으면 버립니다.
    /// 느린 네트워크 폴더를 열다 다른 곳으로 옮겼을 때 옛 결과가 덮어쓰는 것을 막습니다.
    /// </summary>
    /// <summary>
    /// 폴더를 읽지 않고 이름만 걸어 둡니다.
    /// 시작할 때 보이지 않는 탭까지 한꺼번에 읽으면 창이 늦게 뜹니다.
    /// 그 탭을 고르는 순간 <see cref="EnsureLoadedAsync"/>가 비로소 읽습니다.
    /// </summary>
    public void Defer(string path)
    {
        _deferredPath = path;
        CurrentPath = path;
        AddressText = SpecialPaths.ToDisplay(path);
    }

    /// <summary>미뤄 둔 탭이면 이제 읽습니다. 이미 읽은 탭이면 아무 일도 하지 않습니다.</summary>
    public Task EnsureLoadedAsync()
    {
        if (_deferredPath is not { } path)
        {
            return Task.CompletedTask;
        }

        _deferredPath = null;
        return NavigateAsync(path, recordHistory: true);
    }

    public async Task NavigateAsync(string path, bool recordHistory = true)
    {
        var fallback = _currentPath.Length > 0 ? _currentPath : null;
        var resolved = PathHelper.ResolveExistingDirectory(path, fallback);

        // 폴더를 열면 검색은 끝난 것으로 봅니다.
        IsSearching = false;

        // 다른 폴더를 여는 것이므로 스크롤은 처음으로 돌립니다.
        ScrollOffset = 0;
        HorizontalScrollOffset = 0;

        if (_searchTerm.Length > 0)
        {
            _searchTerm = string.Empty;
            OnPropertyChanged(nameof(Header));
        }

        _navigation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _navigation = cancellation;

        CurrentPath = resolved;
        AddressText = SpecialPaths.ToDisplay(resolved);

        if (recordHistory)
        {
            _history.Record(resolved);
        }

        RaiseNavigationState();
        IsLoading = true;

        var watch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            IReadOnlyList<FileSystemEntry> entries;

            if (SpecialPaths.IsRecycleBin(resolved))
            {
                // 휴지통은 폴더가 아니라 $I·$R 짝 파일 더미입니다. 우리가 직접 읽습니다.
                var bin = await Task.Run(ReadRecycleBin, cancellation.Token).ConfigureAwait(true);

                if (cancellation.IsCancellationRequested)
                {
                    return;
                }

                _recycled = bin.Originals;
                entries = bin.Entries;
            }
            else
            {
                _recycled.Clear();

                entries = await _enumerator
                    .EnumerateAsync(resolved, _pane.ShowHiddenFiles, cancellation.Token)
                    .ConfigureAwait(true);
            }

            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            // 다시 읽었는데 내용이 그대로면 목록을 갈아 끼우지 않습니다.
            // 갈아 끼우는 한 번이 화면 갈래를 수백 밀리초씩 붙들고, 고른 것과 스크롤 자리도 날아갑니다.
            // 다른 프로그램이 임시 파일을 만들고 지울 때마다 감시기가 깨우므로 이 걸음이 잦습니다.
            if (!recordHistory && SameEntries(_entries, entries))
            {
                _entries = entries;
                _watcher.Watch(resolved);
                return;
            }

            _entries = entries;

            var rebuild = System.Diagnostics.Stopwatch.StartNew();
            RebuildItems(preserveSelection: false);

            if (watch.ElapsedMilliseconds > 150)
            {
                DiagnosticLog.Write(
                    $"폴더 열기 {SpecialPaths.ToDisplay(resolved)} {entries.Count}개,"
                        + $" 읽기 {watch.ElapsedMilliseconds - rebuild.ElapsedMilliseconds}ms,"
                        + $" 목록 세우기 {rebuild.ElapsedMilliseconds}ms");
            }

            OnPropertyChanged(nameof(IsRecycleBin));
            _watcher.Watch(resolved);
        }
        catch (OperationCanceledException)
        {
            // 다른 폴더로 옮겨 간 경우입니다.
        }
        catch (DirectoryReadException ex) when (!cancellation.IsCancellationRequested)
        {
            // 빈 폴더와 구별되도록 까닭을 상태 줄에 띄웁니다. 자세한 것은 debug.log 에 남아 있습니다.
            _entries = Array.Empty<FileSystemEntry>();
            RebuildItems(preserveSelection: false);
            StatusText = "폴더를 읽지 못했습니다: " + ex.Reason;
        }
        finally
        {
            if (ReferenceEquals(_navigation, cancellation))
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>주소 표시줄에서 엔터를 눌렀을 때 호출합니다. 검색 중이면 검색어로 받습니다.</summary>
    public Task NavigateToAddressAsync() =>
        IsSearching ? RunSearchAsync(AddressText) : NavigateAsync(AddressText);

    /// <summary>주소 표시줄을 검색어 칸으로 씁니다. 화면은 이 신호를 받아 그 칸으로 옮겨 갑니다.</summary>
    public void BeginSearch()
    {
        if (UsesEverything())
        {
            IsSearching = true;
            AddressText = string.Empty;
            StatusText = "찾을 이름을 적고 Enter를 누르십시오. 온 컴퓨터에서 찾습니다.";
            SearchRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (!_searchSettings.FallbackToEnumerate)
        {
            StatusText = "Everything이 실행 중이 아닙니다. 먼저 Everything을 켜 주십시오.";
            return;
        }

        if (!CanWalkCurrentFolder())
        {
            StatusText = "Everything이 꺼져 있습니다. 폴더를 하나 연 뒤에 찾아 주십시오.";
            return;
        }

        IsSearching = true;
        AddressText = string.Empty;
        StatusText = $"찾을 이름을 적고 Enter를 누르십시오. {SpecialPaths.ToDisplay(CurrentPath)} 아래에서 찾습니다.";
        SearchRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Everything 에 맡길 수 있는 상태인지입니다.</summary>
    private bool UsesEverything() =>
        _searchSettings.Provider == SearchProvider.Everything && _search.IsAvailable;

    /// <summary>직접 훑을 수 있는 자리인지입니다. 내 PC 처럼 폴더가 아닌 자리에서는 훑을 것이 없습니다.</summary>
    private bool CanWalkCurrentFolder() =>
        CurrentPath.Length > 0 && !SpecialPaths.IsVirtual(CurrentPath);

    /// <summary>
    /// Everything 없이 지금 폴더 아래를 직접 훑습니다.
    /// 다 끝나기를 기다리지 않고 찾는 대로 올립니다. 색인이 없어 오래 걸릴 수 있기 때문입니다.
    /// </summary>
    private async Task WalkForAsync(string term, CancellationTokenSource cancellation)
    {
        var root = CurrentPath;

        if (!CanWalkCurrentFolder())
        {
            StatusText = "폴더를 하나 연 뒤에 찾아 주십시오.";
            return;
        }

        var display = SpecialPaths.ToDisplay(root);
        var collected = new List<FileSystemEntry>();

        _entries = collected;
        RebuildItems(preserveSelection: false);
        StatusText = $"{display} 아래를 훑는 중…";

        var progress = new Progress<IReadOnlyList<FileSystemEntry>>(batch =>
        {
            if (cancellation.IsCancellationRequested || !ReferenceEquals(_entries, collected))
            {
                return;
            }

            collected.AddRange(batch);
            RebuildItems(preserveSelection: true);
            StatusText = $"{display} 아래를 훑는 중… {collected.Count}개";
        });

        var total = await FolderSearch
            .SearchAsync(root, term, _pane.ShowHiddenFiles, _searchSettings.MaxResults, progress, cancellation.Token)
            .ConfigureAwait(true);

        if (cancellation.IsCancellationRequested || !ReferenceEquals(_entries, collected))
        {
            return;
        }

        RebuildItems(preserveSelection: true);

        StatusText = total switch
        {
            0 => $"{display} 아래에 '{term}'에 맞는 것이 없습니다.",
            _ when total >= _searchSettings.MaxResults =>
                $"검색 결과 {total}개 — 너무 많아 여기서 멈췄습니다.",
            _ => $"검색 결과 {total}개 — {display} 아래"
        };
    }

    /// <summary>검색을 접고 보던 폴더로 돌아갑니다.</summary>
    public void ExitSearch()
    {
        if (!IsSearching)
        {
            return;
        }

        NavigateAsync(CurrentPath, recordHistory: false).Fire("폴더 열기", this);
    }

    /// <summary>
    /// Everything에 검색을 맡기고 결과를 목록에 올립니다.
    /// 폴더를 읽는 것이 아니므로 이동 이력에는 남기지 않습니다.
    /// </summary>
    private async Task RunSearchAsync(string term)
    {
        term = (term ?? string.Empty).Trim();

        if (term.Length == 0)
        {
            ExitSearch();
            return;
        }

        _navigation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _navigation = cancellation;

        IsSearching = true;
        _searchTerm = term;
        OnPropertyChanged(nameof(Header));
        IsLoading = true;

        // 토큰을 미리 받아 둡니다.
        // 답을 기다리는 사이에 이 탭이 닫히면 원본이 버려지고, 그 뒤에 Token 을 물으면 예외가 납니다.
        var token = cancellation.Token;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            if (!UsesEverything())
            {
                await WalkForAsync(term, cancellation).ConfigureAwait(true);
                return;
            }

            // 세 단추를 모두 껐으면 찾을 곳이 없습니다. "맞는 것이 없습니다"와 헷갈리지 않게 따로 알립니다.
            if (!_searchSettings.IncludeLocalDrives
                && !_searchSettings.IncludeNetworkDrives
                && !_searchSettings.IncludeCloudDrives)
            {
                _entries = Array.Empty<FileSystemEntry>();
                RebuildItems(preserveSelection: false);
                StatusText = "찾을 곳이 없습니다. 로컬·네트워크·클라우드 단추를 하나 이상 켜 주십시오.";
                return;
            }

            // 물음표·별표가 든 검색어는 부분 일치로 바꿔 보냅니다.
            // Everything 은 그것들이 있으면 이름 전체가 맞아야 한다고 보기 때문입니다.
            var query = FolderSearch.ToPartial(term);

            // 읽는 대로 먼저 보여 줍니다. 다 끝나기를 기다리면 넓은 낱말에서는 한참 빈 화면입니다.
            var progress = new Progress<IReadOnlyList<FileSystemEntry>>(part =>
            {
                if (_disposed || token.IsCancellationRequested)
                {
                    return;
                }

                _entries = part;
                RebuildItems(preserveSelection: false);
                StatusText = $"검색 결과 {part.Count}개 — 더 읽는 중…";
            });

            var found = await _search
                .SearchAsync(
                    query,
                    _searchSettings.MaxResults,
                    _searchSettings.IncludeLocalDrives,
                    _searchSettings.IncludeNetworkDrives,
                    _searchSettings.IncludeCloudDrives,
                    _searchSettings.CloudDriveLabels,
                    _searchSettings.DescribeChunk,
                    progress,
                    token)
                .ConfigureAwait(true);

            DiagnosticLog.Write($"검색 '{query}' {found.Count}건 받는 데 {watch.ElapsedMilliseconds}ms");

            if (_disposed || token.IsCancellationRequested)
            {
                return;
            }

            _entries = found;
            RebuildItems(preserveSelection: false);

            DiagnosticLog.Write($"검색 목록 세우기까지 모두 {watch.ElapsedMilliseconds}ms");

            // RebuildItems가 폴더 개수를 적어 두므로 그 뒤에 검색 결과로 바꿔 씁니다.
            StatusText = found.Count == 0
                ? $"'{term}'에 맞는 것이 없습니다."
                : $"검색 결과 {found.Count}개";
        }
        finally
        {
            if (ReferenceEquals(_navigation, cancellation))
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>검색을 쓸 수 있게 됐다고 소유자가 알려 줍니다.</summary>
    public void NotifySearchReady() => SearchCommand.RaiseCanExecuteChanged();

    /// <summary>
    /// 그 파일이 들어 있는 폴더를 열고 그 줄을 골라 둡니다.
    /// 검색 결과에서 "경로 열기"를 눌렀을 때 씁니다.
    /// </summary>
    public async Task RevealAsync(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
        {
            return;
        }

        var folder = Path.GetDirectoryName(fullPath);

        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        await NavigateAsync(folder).ConfigureAwait(true);
        SelectByPath(fullPath);

        if (SelectedItem is { } item)
        {
            EnsureVisibleRequested?.Invoke(this, item);
        }
    }

    /// <summary>같은 폴더를 다시 읽습니다. 선택은 유지합니다.</summary>
    public async Task RefreshAsync()
    {
        var keepPath = SelectedItem?.FullPath;

        await NavigateAsync(CurrentPath, recordHistory: false).ConfigureAwait(true);

        if (keepPath is not null)
        {
            SelectByPath(keepPath);
        }
    }

    /// <summary>
    /// 줄이 화면에 나타날 때 호출합니다. 목록 전체가 아니라 보이는 줄만 썸네일을 만듭니다.
    /// 파일이 1만 개여도 셸 호출은 화면에 보이는 수십 건에 그칩니다.
    /// </summary>
    public async Task RequestThumbnailAsync(FileItemViewModel item)
    {
        if (item.Thumbnail is not null || _disposed)
        {
            return;
        }

        var token = _navigation?.Token ?? CancellationToken.None;

        try
        {
            // 목록에는 파일 종류 아이콘만 씁니다. 내용 미리보기는 오른쪽 미리보기 칸이 맡습니다.
            var image = await _thumbnails
                .GetAsync(item.FullPath, ThumbnailPixelSize, iconOnly: true, token)
                .ConfigureAwait(true);

            if (image is not null && !token.IsCancellationRequested)
            {
                item.Thumbnail = image;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // 부르는 쪽이 결과를 기다리지 않으므로, 여기서 삼키면 아무 데도 드러나지 않습니다.
            DiagnosticLog.Write($"썸네일 요청 실패 {item.FullPath}: {ex.GetType().Name} {ex.Message}");
        }
    }

    public async Task OpenAsync(FileItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.IsDirectory)
        {
            await NavigateAsync(item.FullPath).ConfigureAwait(true);
            return;
        }

        // 폴더를 가리키는 바로가기는 이 탭에서 그 폴더로 갑니다.
        // 셸에 맡기면 윈도우 탐색기가 따로 뜹니다.
        if (string.Equals(item.Entry.Extension, ".lnk", StringComparison.OrdinalIgnoreCase)
            && ShellShortcut.ReadTarget(item.FullPath) is { } target)
        {
            // 대상이 네트워크에 있으면 있는지 보는 데도 시간이 걸립니다. 다른 갈래에서 봅니다.
            var isFolder = await Task.Run(() => Directory.Exists(target)).ConfigureAwait(true);

            if (isFolder)
            {
                DiagnosticLog.Write($"바로가기 따라가기 {item.Name} → {target}");
                await NavigateAsync(target).ConfigureAwait(true);
                return;
            }
        }

        // 셸에 맡겨 여는 일은 연결 프로그램이 다 뜰 때까지 돌아오지 않습니다.
        // 화면 갈래에서 부르면 그동안 창이 통째로 멈춥니다. 다른 갈래에 맡기고 기다립니다.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var opened = await Task.Run(() => ShellLauncher.Open(item.FullPath)).ConfigureAwait(true);

        DiagnosticLog.Write($"파일 열기 {item.Name} {watch.ElapsedMilliseconds}ms {(opened ? "" : "— 실패")}");

        if (!opened)
        {
            StatusText = "연결된 프로그램으로 열지 못했습니다.";
        }
    }

    /// <summary>고른 파일을 클립보드에 담습니다. 잘라내기와 복사는 표시만 다릅니다.</summary>
    public void CopyToClipboard(IReadOnlyList<FileItemViewModel> items, bool cut)
    {
        if (items.Count == 0)
        {
            return;
        }

        var paths = new List<string>(items.Count);

        foreach (var item in items)
        {
            paths.Add(item.FullPath);
        }

        StatusText = FileClipboard.Set(paths, cut)
            ? $"{paths.Count}개를 클립보드에 {(cut ? "잘라냈습니다" : "복사했습니다")}."
            : "클립보드를 쓸 수 없습니다. 다른 프로그램이 잡고 있을 수 있습니다.";
    }

    /// <summary>
    /// 클립보드의 파일을 현재 폴더에 붙여넣습니다.
    /// 진행률 창과 같은 이름 충돌 처리는 윈도우가 합니다.
    /// </summary>
    public async Task PasteAsync(IntPtr owner)
    {
        if (!CanModifyCurrentFolder())
        {
            return;
        }

        if (!FileClipboard.TryGet(out var paths, out var cut))
        {
            return;
        }

        var done = cut
            ? ShellFileOperations.Move(paths, CurrentPath, owner)
            : ShellFileOperations.Copy(paths, CurrentPath, owner, ComesFromSameFolder(paths, CurrentPath));

        if (done)
        {
            _owner.Undo.Record(new FileAction(
                cut ? FileActionKind.Move : FileActionKind.Copy,
                paths,
                CurrentPath));
        }
        else
        {
            StatusText = "붙여넣기를 마치지 못했습니다.";
        }

        await NavigateAsync(CurrentPath, recordHistory: false).ConfigureAwait(true);
    }

    /// <summary>
    /// 마우스로 끌어다 놓은 파일을 받습니다.
    /// 어디에 놓을지와 복사인지 이동인지는 화면 쪽에서 이미 정해서 넘겨줍니다.
    /// </summary>
    public async Task DropAsync(IReadOnlyList<string> sources, string destinationFolder, DropAction action, IntPtr owner)
    {
        if (sources.Count == 0
            || string.IsNullOrEmpty(destinationFolder)
            || SpecialPaths.IsMyComputer(destinationFolder))
        {
            return;
        }

        // 바로가기는 셸 파일 작업이 아니라 .lnk 파일을 새로 만드는 일이라 길이 다릅니다.
        if (action == DropAction.Link)
        {
            var made = ShellShortcut.CreateAll(sources, destinationFolder);

            StatusText = made > 0
                ? $"바로가기 {made}개를 만들었습니다."
                : "바로가기를 만들지 못했습니다.";

            await NavigateAsync(CurrentPath, recordHistory: false).ConfigureAwait(true);
            return;
        }

        var copy = action == DropAction.Copy;

        var done = copy
            ? ShellFileOperations.Copy(sources, destinationFolder, owner, ComesFromSameFolder(sources, destinationFolder))
            : ShellFileOperations.Move(sources, destinationFolder, owner);

        if (done)
        {
            _owner.Undo.Record(new FileAction(
                copy ? FileActionKind.Copy : FileActionKind.Move,
                sources,
                destinationFolder));
        }

        StatusText = done
            ? $"{sources.Count}개를 {(copy ? "복사했습니다" : "옮겼습니다")}."
            : copy ? "복사를 마치지 못했습니다." : "이동을 마치지 못했습니다.";

        await NavigateAsync(CurrentPath, recordHistory: false).ConfigureAwait(true);
    }

    /// <param name="permanent">true이면 휴지통을 거치지 않습니다.</param>
    public async Task DeleteAsync(IReadOnlyList<FileItemViewModel> items, bool permanent, IntPtr owner)
    {
        if (items.Count == 0 || !CanModifyCurrentFolder())
        {
            return;
        }

        var paths = new List<string>(items.Count);

        foreach (var item in items)
        {
            paths.Add(item.FullPath);
        }

        if (ShellFileOperations.Delete(paths, permanent, owner, out var recycled))
        {
            // 휴지통으로 보낸 것만 적어 둡니다. 바로 지운 것은 되돌릴 거리가 없습니다.
            if (!permanent && recycled.Count > 0)
            {
                _owner.Undo.Record(new FileAction(FileActionKind.Delete, paths, Recycled: recycled));
            }
        }
        else
        {
            StatusText = "삭제를 마치지 못했습니다.";
        }

        await NavigateAsync(CurrentPath, recordHistory: false).ConfigureAwait(true);
    }

    /// <summary>윈도우 탐색기의 속성 창을 띄웁니다.</summary>
    public void ShowProperties(FileItemViewModel? item, IntPtr owner)
    {
        if (item is null)
        {
            return;
        }

        if (!ShellLauncher.ShowProperties(item.FullPath, owner))
        {
            StatusText = "속성 창을 열지 못했습니다.";
        }
    }

    /// <summary>
    /// 복사할 것 가운데 이미 그 폴더에 있는 것이 있는지 봅니다.
    /// 있으면 셸에게 사본 이름을 붙이라고 일러 줍니다. 탐색기가 "- 복사본"을 만드는 방식입니다.
    /// </summary>
    private static bool ComesFromSameFolder(IReadOnlyList<string> paths, string folder)
    {
        var target = TrimSeparator(folder);

        foreach (var path in paths)
        {
            try
            {
                var parent = Path.GetDirectoryName(Path.GetFullPath(path));

                if (parent is not null
                    && string.Equals(TrimSeparator(parent), target, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is ArgumentException
                                           or NotSupportedException
                                           or PathTooLongException)
            {
                // 읽을 수 없는 경로는 그냥 넘어갑니다.
            }
        }

        return false;
    }

    private static string TrimSeparator(string path) => path.TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>방금 한 파일 작업을 거꾸로 합니다.</summary>
    /// <summary>
    /// F5 나 새로 고침 단추가 부르는 길입니다. 목록과 함께 폴더 나무도 다시 읽습니다.
    /// 폴더를 지켜보다 저절로 일어나는 새로 고침은 이 길을 타지 않습니다.
    /// 그때마다 나무를 다시 읽으면 펼쳐 둔 가지가 자꾸 접힙니다.
    /// </summary>
    public async Task RefreshEverythingAsync()
    {
        _owner.RefreshTree();
        await RefreshAsync().ConfigureAwait(true);
    }

    public async Task UndoAsync(IntPtr owner)
    {
        StatusText = _owner.Undo.Undo(owner);
        await NavigateAsync(CurrentPath, recordHistory: false).ConfigureAwait(true);
    }

    /// <summary>즐겨찾기로 열린 고정 탭인지입니다. 색을 받은 탭만 참입니다.</summary>
    public bool HasTabColor
    {
        get => _hasTabColor;
        private set => SetProperty(ref _hasTabColor, value);
    }

    /// <summary>탭을 채우는 색입니다. 고른 탭은 짙게, 고르지 않은 탭은 조금 묽게 칠합니다.</summary>
    public System.Windows.Media.Brush TabFillBrush
    {
        get => _tabFillBrush;
        private set => SetProperty(ref _tabFillBrush, value);
    }

    /// <summary>탭 테두리 색입니다. 채운 색보다 한 단계 짙습니다.</summary>
    public System.Windows.Media.Brush TabEdgeBrush
    {
        get => _tabEdgeBrush;
        private set => SetProperty(ref _tabEdgeBrush, value);
    }

    /// <summary>테두리 두께입니다. 색 없는 작업 탭은 0입니다.</summary>
    public System.Windows.Thickness TabEdgeThickness
    {
        get => _tabEdgeThickness;
        private set => SetProperty(ref _tabEdgeThickness, value);
    }

    /// <summary>
    /// 즐겨찾기 차례에 맞는 색을 붙입니다. 차례가 음수이면 색 없는 작업 탭이 됩니다.
    /// </summary>
    public void ApplyTabColor(int favoriteIndex)
    {
        FavoriteIndex = favoriteIndex < 0 ? -1 : favoriteIndex;
        _tabColorHex = TabPalette.At(favoriteIndex);
        RefreshTabBrushes();
    }

    /// <summary>
    /// 몇 번 즐겨찾기로 열린 탭인지입니다. 작업 탭은 -1 입니다.
    /// 창을 닫을 때 이 값을 적어 두었다가, 지난 탭을 복원할 때 같은 색을 다시 붙입니다.
    /// </summary>
    public int FavoriteIndex { get; private set; } = -1;

    /// <summary>
    /// 색을 다시 만듭니다. 고른 탭인지에 따라 짙기가 달라지므로 고름이 바뀔 때도 부릅니다.
    /// 만든 붓은 얼려 둡니다. 화면이 여러 번 그려져도 같은 것을 씁니다.
    /// </summary>
    private void RefreshTabBrushes()
    {
        System.Windows.Media.Color color;

        if (_tabColorHex is null)
        {
            HasTabColor = false;
            TabFillBrush = System.Windows.Media.Brushes.Transparent;
            TabEdgeBrush = System.Windows.Media.Brushes.Transparent;
            TabEdgeThickness = default;
            return;
        }

        try
        {
            color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_tabColorHex);
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException)
        {
            HasTabColor = false;
            TabFillBrush = System.Windows.Media.Brushes.Transparent;
            TabEdgeBrush = System.Windows.Media.Brushes.Transparent;
            TabEdgeThickness = default;
            return;
        }

        HasTabColor = true;

        // 고르지 않은 탭은 조금 묽게 칠해 고른 탭이 도드라지게 합니다.
        // 고른 탭은 90%(0xE6), 고르지 않은 탭은 30%(0x4D) 만큼 칠합니다.
        var fill = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb(IsSelected ? (byte)0xE6 : (byte)0x4D, color.R, color.G, color.B));
        fill.Freeze();
        TabFillBrush = fill;

        var edge = new System.Windows.Media.SolidColorBrush(Darken(color, IsSelected ? 0.62 : 0.78));
        edge.Freeze();
        TabEdgeBrush = edge;

        TabEdgeThickness = new System.Windows.Thickness(1);
    }

    /// <summary>
    /// 화면 구조가 바뀔 때마다 귀 기울이는 접근성 프로그램이 붙어 있는지입니다.
    /// 붙어 있으면 WPF 가 줄 하나하나마다 알림을 보내느라 목록 갈아 끼우기가 크게 느려집니다.
    /// </summary>
    private static bool ListeningForStructure()
    {
        try
        {
            return System.Windows.Automation.Peers.AutomationPeer.ListenerExists(
                System.Windows.Automation.Peers.AutomationEvents.StructureChanged);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>같은 색을 그만큼 어둡게 합니다. 테두리에 씁니다.</summary>
    private static System.Windows.Media.Color Darken(System.Windows.Media.Color color, double factor) =>
        System.Windows.Media.Color.FromRgb(
            (byte)(color.R * factor),
            (byte)(color.G * factor),
            (byte)(color.B * factor));

    /// <summary>
    /// 로컬(네트워크도 클라우드도 아닌 곳)의 결과도 담을지입니다. 검색 줄의 디스크 단추가 이 값을 켜고 끕니다.
    /// 창 전체가 같은 값을 쓰고, 바꾸면 설정에 남습니다.
    /// </summary>
    public bool SearchLocalDrives
    {
        get => _searchSettings.IncludeLocalDrives;
        set
        {
            if (_searchSettings.IncludeLocalDrives == value)
            {
                return;
            }

            _searchSettings.IncludeLocalDrives = value;
            _owner.SaveSettings();
            OnPropertyChanged(nameof(SearchLocalDrives));

            if (IsSearching && _searchTerm.Length > 0)
            {
                RunSearchAsync(_searchTerm).Fire("검색", this);
            }
        }
    }

    /// <summary>
    /// 네트워크 드라이브의 결과도 담을지입니다. 검색 줄의 토글 단추가 이 값을 켜고 끕니다.
    /// 창 전체가 같은 값을 쓰고, 바꾸면 설정에 남습니다.
    /// </summary>
    public bool SearchNetworkDrives
    {
        get => _searchSettings.IncludeNetworkDrives;
        set
        {
            if (_searchSettings.IncludeNetworkDrives == value)
            {
                return;
            }

            _searchSettings.IncludeNetworkDrives = value;
            _owner.SaveSettings();
            OnPropertyChanged(nameof(SearchNetworkDrives));

            // 켜고 끈 것이 곧바로 보이도록 같은 검색어로 한 번 더 찾습니다.
            if (IsSearching && _searchTerm.Length > 0)
            {
                RunSearchAsync(_searchTerm).Fire("검색", this);
            }
        }
    }

    /// <summary>
    /// 클라우드(MYBOX 같은 가상 드라이브, OneDrive 같은 동기화 폴더)의 결과도 담을지입니다.
    /// 검색 줄의 구름 단추가 이 값을 켜고 끕니다. 창 전체가 같은 값을 쓰고, 바꾸면 설정에 남습니다.
    /// </summary>
    public bool SearchCloudDrives
    {
        get => _searchSettings.IncludeCloudDrives;
        set
        {
            if (_searchSettings.IncludeCloudDrives == value)
            {
                return;
            }

            _searchSettings.IncludeCloudDrives = value;
            _owner.SaveSettings();
            OnPropertyChanged(nameof(SearchCloudDrives));

            if (IsSearching && _searchTerm.Length > 0)
            {
                RunSearchAsync(_searchTerm).Fire("검색", this);
            }
        }
    }

    /// <summary>지금 휴지통을 보고 있는지입니다. 목록의 오른쪽 단추 메뉴가 이것을 보고 달라집니다.</summary>
    public bool IsRecycleBin => SpecialPaths.IsRecycleBin(CurrentPath);

    /// <summary>
    /// 휴지통을 읽어 목록에 쓸 모양으로 바꿉니다.
    /// 이름은 지우기 전 이름으로 보이고, 날짜 칸에는 지운 때가 들어갑니다.
    /// </summary>
    private (IReadOnlyList<FileSystemEntry> Entries, Dictionary<string, string> Originals) ReadRecycleBin()
    {
        var items = RecycleBinReader.Read();
        var originals = new Dictionary<string, string>(items.Count, StringComparer.OrdinalIgnoreCase);
        var entries = new List<FileSystemEntry>(items.Count);

        foreach (var item in items)
        {
            originals[item.RecycledPath] = item.OriginalPath;

            entries.Add(new FileSystemEntry
            {
                FullPath = item.RecycledPath,
                Name = item.Name,
                IsDirectory = item.IsDirectory,
                Size = item.Size,
                LastWriteTime = item.DeletedAt,
                CreationTime = item.DeletedAt,
                Attributes = item.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal
            });
        }

        return (entries, originals);
    }

    /// <summary>고른 휴지통 항목을 원래 자리로 되돌립니다.</summary>
    public async Task RestoreFromRecycleBinAsync(IReadOnlyList<FileItemViewModel> items, IntPtr owner)
    {
        if (!IsRecycleBin || items is null || items.Count == 0)
        {
            return;
        }

        var recycled = new List<string>(items.Count);
        var originals = new List<string>(items.Count);

        foreach (var item in items)
        {
            if (!_recycled.TryGetValue(item.FullPath, out var original))
            {
                continue;
            }

            recycled.Add(item.FullPath);
            originals.Add(original);
        }

        if (recycled.Count == 0)
        {
            StatusText = "되돌릴 자리를 알 수 없습니다.";
            return;
        }

        var done = await Task
            .Run(() => ShellRecycleBin.Restore(recycled, originals, owner))
            .ConfigureAwait(true);

        StatusText = done == recycled.Count
            ? $"{done}개를 제자리로 돌려놓았습니다."
            : $"{recycled.Count}개 가운데 {done}개만 돌려놓았습니다.";

        await NavigateAsync(CurrentPath, recordHistory: false).ConfigureAwait(true);
    }

    /// <summary>고른 휴지통 항목을 아주 지웁니다. 되돌릴 수 없습니다.</summary>
    public async Task PurgeFromRecycleBinAsync(IReadOnlyList<FileItemViewModel> items)
    {
        if (!IsRecycleBin || items is null || items.Count == 0)
        {
            return;
        }

        var targets = new List<string>(items.Count);

        foreach (var item in items)
        {
            targets.Add(item.FullPath);
        }

        var done = await Task.Run(() => RecycleBinReader.DeleteForever(targets)).ConfigureAwait(true);

        StatusText = $"{done}개를 아주 지웠습니다.";
        await NavigateAsync(CurrentPath, recordHistory: false).ConfigureAwait(true);
    }

    /// <summary>화면 아래 상태 줄에 한 줄 띄웁니다.</summary>
    public void ReportStatus(string message) => StatusText = message;

    /// <summary>붙여넣을 수 있는 자리인지 알려 줍니다.</summary>
    public bool CanPaste => CanModifyCurrentFolder() && FileClipboard.HasFiles;

    private bool CanModifyCurrentFolder() =>
        CurrentPath.Length > 0 && !SpecialPaths.IsVirtual(CurrentPath);

    /// <summary>
    /// 현재 폴더에 새 폴더를 만들고 바로 이름 편집을 시작합니다.
    /// 만들자마자 감시기가 새로고침을 걸면 편집 상태가 날아가므로 잠시 억제합니다.
    /// </summary>
    public async Task CreateFolderAsync()
    {
        if (!CanCreateFolder())
        {
            return;
        }

        var name = FindAvailableFolderName(CurrentPath, "새 폴더");

        try
        {
            Directory.CreateDirectory(Path.Combine(CurrentPath, name));
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            StatusText = "폴더를 만들지 못했습니다: " + ex.Message;
            return;
        }

        SuppressWatcher();
        await NavigateAsync(CurrentPath, recordHistory: false).ConfigureAwait(true);

        foreach (var item in Items)
        {
            if (string.Equals(item.Name, name, StringComparison.Ordinal))
            {
                SelectedItem = item;
                EnsureVisibleRequested?.Invoke(this, item);
                item.BeginEdit();
                break;
            }
        }
    }

    /// <summary>
    /// 셸 메뉴의 "새로 만들기"로 방금 생긴 항목을 고르고 이름 편집을 엽니다. 윈도우 탐색기와 같은 동작입니다.
    /// 셸이 만들었으므로 여기서는 만들지 않고 목록만 다시 읽습니다.
    /// 감시기의 새로고침이 편집 상태를 날리지 않도록 넉넉히 억제합니다.
    /// </summary>
    public async Task EditNewItemAsync(string name)
    {
        SuppressWatcher(2000);
        _owner.RefreshTree();
        await NavigateAsync(CurrentPath, recordHistory: false).ConfigureAwait(true);

        foreach (var item in Items)
        {
            if (string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                SelectedItem = item;
                EnsureVisibleRequested?.Invoke(this, item);
                item.BeginEdit();
                return;
            }
        }

        DiagnosticLog.Write($"  새로 만든 항목을 목록에서 찾지 못했습니다: {name}");
    }

    /// <summary>
    /// 편집 중인 이름을 실제 파일 이름에 반영합니다.
    /// 셸에 맡기므로 되돌리기와 이름 충돌 대화상자가 그대로 따라옵니다.
    /// </summary>
    /// <param name="owner">셸 대화상자의 부모가 될 창입니다.</param>
    public async Task CommitRenameAsync(FileItemViewModel item, IntPtr owner)
    {
        if (!item.IsEditing)
        {
            return;
        }

        item.IsEditing = false;

        var newName = (item.EditingName ?? string.Empty).Trim();

        if (newName.Length == 0 || string.Equals(newName, item.Name, StringComparison.Ordinal))
        {
            return;
        }

        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            StatusText = @"이름에 \ / : * ? "" < > | 는 쓸 수 없습니다.";
            return;
        }

        var target = Path.Combine(CurrentPath, newName);

        // 셸에 맡깁니다. 그래야 Ctrl+Z 되돌리기가 듣고, 이름이 겹칠 때도 윈도우가 물어봅니다.
        // 직접 옮기면 되돌릴 수 없고 대화상자도 우리가 다 만들어야 합니다.
        if (ShellFileOperations.Rename(item.FullPath, newName, owner))
        {
            _owner.Undo.Record(new FileAction(
                FileActionKind.Rename,
                new[] { item.FullPath },
                NewName: newName));
        }
        else
        {
            StatusText = "이름을 바꾸지 못했습니다.";
        }

        SuppressWatcher();
        await NavigateAsync(CurrentPath, recordHistory: false).ConfigureAwait(true);
        SelectByPath(target);
    }

    public void CancelRename(FileItemViewModel item)
    {
        item.IsEditing = false;
        item.EditingName = item.Name;
    }

    private void SelectByPath(string fullPath)
    {
        foreach (var item in Items)
        {
            if (string.Equals(item.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                SelectedItem = item;
                return;
            }
        }
    }

    private async Task GoBackAsync()
    {
        var target = _history.GoBack();
        if (target is not null)
        {
            await NavigateAsync(target, recordHistory: false).ConfigureAwait(true);
            RaiseNavigationState();
        }
    }

    private async Task GoForwardAsync()
    {
        var target = _history.GoForward();
        if (target is not null)
        {
            await NavigateAsync(target, recordHistory: false).ConfigureAwait(true);
            RaiseNavigationState();
        }
    }

    private async Task GoUpAsync()
    {
        var parent = GetParent();
        if (parent is not null)
        {
            await NavigateAsync(parent).ConfigureAwait(true);
        }
    }

    private bool CanCreateFolder() => CanModifyCurrentFolder();

    private string? GetParent()
    {
        if (CurrentPath.Length == 0 || SpecialPaths.IsMyComputer(CurrentPath))
        {
            return null;
        }

        // 휴지통에서 위로 가면 내 PC 입니다. 휴지통은 어느 폴더 아래에도 있지 않습니다.
        if (SpecialPaths.IsRecycleBin(CurrentPath))
        {
            return SpecialPaths.MyComputer;
        }

        try
        {
            // 드라이브 뿌리에서 위로 가면 "내 PC"입니다.
            return Directory.GetParent(CurrentPath)?.FullName ?? SpecialPaths.MyComputer;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            return null;
        }
    }

    private bool HasParent() => GetParent() is not null;

    private void RebuildItems(bool preserveSelection)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var keepPath = preserveSelection ? SelectedItem?.FullPath : null;

        var sorted = _entries.ToList();
        sorted.Sort(new FileSystemEntryComparer(_pane.SortColumn, _pane.SortDirection, _pane.FoldersFirst));

        var sortedAt = watch.ElapsedMilliseconds;

        var items = new List<FileItemViewModel>(sorted.Count);
        foreach (var entry in sorted)
        {
            items.Add(new FileItemViewModel(entry));
        }

        var madeAt = watch.ElapsedMilliseconds;

        // 화면에 넘기는 이 한 줄이 오래 걸립니다.
        // 쓰레기 수거가 그 사이에 끼어든 것인지 가리려고 수거 횟수와 멈춘 시간을 함께 잽니다.
        var sweeps = GC.CollectionCount(0);
        var deepSweeps = GC.CollectionCount(2);
        var paused = GC.GetTotalPauseDuration();

        Items = items;

        var boundAt = watch.ElapsedMilliseconds;
        var sweptBy = GC.CollectionCount(0) - sweeps;
        var deepSweptBy = GC.CollectionCount(2) - deepSweeps;
        var pausedBy = (GC.GetTotalPauseDuration() - paused).TotalMilliseconds;

        SelectedItem = null;
        SelectionText = string.Empty;

        var clearedAt = watch.ElapsedMilliseconds;


        if (keepPath is not null)
        {
            SelectByPath(keepPath);
        }

        var folderCount = 0;
        var fileCount = 0;
        var totalBytes = 0L;

        foreach (var entry in sorted)
        {
            if (entry.IsDirectory)
            {
                folderCount++;
            }
            else
            {
                fileCount++;
                totalBytes += entry.Size;
            }
        }

        if (SpecialPaths.IsRecycleBin(CurrentPath))
        {
            StatusText = string.Format(
                CultureInfo.CurrentCulture,
                "휴지통 {0}개 · {1}",
                folderCount + fileCount,
                ByteSizeFormatter.Format(totalBytes));
            return;
        }

        StatusText = SpecialPaths.IsMyComputer(CurrentPath)
            ? string.Format(CultureInfo.CurrentCulture, "드라이브 {0}개", folderCount)
            : string.Format(
                CultureInfo.CurrentCulture,
                "폴더 {0}개, 파일 {1}개 · {2}",
                folderCount,
                fileCount,
                ByteSizeFormatter.Format(totalBytes));

        if (watch.ElapsedMilliseconds > 100)
        {
            DiagnosticLog.Write(
                $"목록 세우기 {sorted.Count}개 모두 {watch.ElapsedMilliseconds}ms"
                    + $" — 줄 세우기 {sortedAt}ms, 만들기 {madeAt - sortedAt}ms,"
                    + $" 화면에 넘기기 {boundAt - madeAt}ms, 고름 지우기 {clearedAt - boundAt}ms"
                    + $" · 수거 {sweptBy}/{deepSweptBy}회 멈춤 {pausedBy:F0}ms,"
                    + $" 쓰는 메모리 {GC.GetTotalMemory(false) / (1024 * 1024)}MB"
                    + $", 접근성 듣는 프로그램 {(ListeningForStructure() ? "있음" : "없음")}");
        }
    }

    /// <summary>
    /// 두 목록이 같은 것을 담고 있는지 봅니다.
    /// 이름·크기·고친 때가 모두 같으면 같은 것으로 봅니다. 읽어 오는 차례는 늘 같습니다.
    /// </summary>
    private static bool SameEntries(IReadOnlyList<FileSystemEntry> before, IReadOnlyList<FileSystemEntry> after)
    {
        if (before.Count != after.Count)
        {
            return false;
        }

        for (var index = 0; index < before.Count; index++)
        {
            var left = before[index];
            var right = after[index];

            if (left.Size != right.Size
                || left.LastWriteTime != right.LastWriteTime
                || !string.Equals(left.FullPath, right.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private void OnDirectoryChanged(object? sender, EventArgs e)
    {
        // 감시 이벤트는 스레드 풀에서 올라오므로 UI 스레드로 옮깁니다.
        _dispatcher.BeginInvoke(new Action(() =>
        {
            // 검색 결과를 보고 있으면 폴더가 바뀌어도 그대로 둡니다. 다시 읽으면 결과가 날아갑니다.
            if (_disposed || IsSearching || DateTime.UtcNow < _suppressWatchUntil)
            {
                return;
            }

            DiagnosticLog.Write($"감시기가 깨움 {SpecialPaths.ToDisplay(CurrentPath)}");
            RefreshAsync().Fire("새로 고침", this);
        }));
    }

    private void RaiseNavigationState()
    {
        BackCommand.RaiseCanExecuteChanged();
        ForwardCommand.RaiseCanExecuteChanged();
        UpCommand.RaiseCanExecuteChanged();
        NewFolderCommand.RaiseCanExecuteChanged();
    }

    /// <summary>이미 있는 이름을 피해 "새 폴더", "새 폴더 (2)" 순으로 고릅니다.</summary>
    private static string FindAvailableFolderName(string parent, string baseName)
    {
        var candidate = baseName;
        var index = 2;

        while (Directory.Exists(Path.Combine(parent, candidate))
               || File.Exists(Path.Combine(parent, candidate)))
        {
            candidate = $"{baseName} ({index})";
            index++;

            if (index > 1000)
            {
                break;
            }
        }

        return candidate;
    }

    private void SuppressWatcher(int milliseconds = 800) =>
        _suppressWatchUntil = DateTime.UtcNow.AddMilliseconds(milliseconds);

    private static bool IsFileSystemFailure(Exception ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher.Changed -= OnDirectoryChanged;
        _watcher.Dispose();
        _navigation?.Cancel();
        _navigation?.Dispose();
    }
}
