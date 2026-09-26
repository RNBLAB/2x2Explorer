using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Explorer.App.ViewModels;
using Explorer.Core.Models;
using Explorer.Core.Services;
using Explorer.Shell.Operations;

namespace Explorer.App.Views.Dialogs;

/// <summary>
/// 편집 창 오른쪽의 간단한 탐색기입니다. PDF 편집·그림 편집·일괄 편집·일괄 인쇄·일괄 이름 바꾸기가 함께 씁니다.
///
/// 탭마다 뒤로·앞으로 기록을 따로 가집니다. 폴더는 늘 보이고, 파일은 <see cref="FileFilter"/> 를 통과한 것만 보입니다.
/// 파일을 두 번 누르거나 Enter 를 누르면 <see cref="FilesActivated"/> 로 알리고, 끌면 FileDrop 으로 넘깁니다.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class PopupExplorer : UserControl
{
    public static readonly DependencyProperty HintProperty =
        DependencyProperty.Register(nameof(Hint), typeof(string), typeof(PopupExplorer), new PropertyMetadata(string.Empty));

    /// <summary>
    /// 목록의 한 줄입니다. 아이콘은 메인 탐색기와 같은 셸 아이콘을 늦게 받아 채웁니다.
    /// 자세히 보기 열(수정한 날짜·유형·크기)과 정렬은 메인 탐색기와 같은 <see cref="FileSystemEntry"/> 로 합니다.
    /// </summary>
    public sealed class FileEntry : ObservableObject
    {
        private ImageSource? _icon;

        /// <summary>드라이브(내 PC)처럼 파일 정보가 없는 줄입니다.</summary>
        public FileEntry(string name, string path, bool isFolder)
            : this(new FileSystemEntry { FullPath = path, Name = name, IsDirectory = isFolder, Attributes = isFolder ? FileAttributes.Directory : FileAttributes.Normal }, name)
        {
            IsDrive = true;
        }

        public FileEntry(FileSystemEntry entry, string? label = null)
        {
            Entry = entry;
            Name = label ?? entry.Name;
        }

        public FileSystemEntry Entry { get; }

        public string Name { get; }

        public string Path => Entry.FullPath;

        public bool IsFolder => Entry.IsDirectory;

        public bool IsDrive { get; }

        /// <summary>바로가기(.lnk)입니다. 폴더와 함께 위에 모이고, 두 번 누르면 대상으로 갑니다.</summary>
        public bool IsShortcut => string.Equals(Entry.Extension, ".lnk", StringComparison.OrdinalIgnoreCase);

        public string Tip => Path;

        public string ModifiedText => IsDrive || Entry.LastWriteTime == DateTime.MinValue
            ? string.Empty
            : Entry.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

        public string TypeText => IsDrive ? "드라이브"
            : IsFolder ? "폴더"
            : IsShortcut ? "바로 가기"
            : Entry.Extension.Length > 1 ? Entry.Extension.TrimStart('.').ToUpperInvariant() + " 파일"
            : "파일";

        public string SizeText => IsFolder || IsDrive ? string.Empty : ByteSizeFormatter.Format(Entry.Size);

        /// <summary>셸 아이콘을 받기 전이나 받지 못했을 때 보이는 글자 아이콘입니다.</summary>
        public string Glyph => IsFolder ? "\uE8B7"
            : IsShortcut ? "\uE71B"
            : string.Equals(System.IO.Path.GetExtension(Path), ".pdf", StringComparison.OrdinalIgnoreCase) ? "\uEA90"
            : ImageMath.IsEditable(Path) ? "\uEB9F"
            : "\uE8A5";

        public bool ShowGlyph => _icon is null;

        public ImageSource? Icon
        {
            get => _icon;
            set
            {
                if (SetProperty(ref _icon, value))
                {
                    OnPropertyChanged(nameof(ShowGlyph));
                }
            }
        }
    }

    /// <summary>탭 하나입니다.</summary>
    public sealed class BrowserTab : ObservableObject
    {
        private string _location = string.Empty;

        public List<string> Back { get; } = new();

        public List<string> Forward { get; } = new();

        /// <summary>지금 폴더입니다. 내 PC 는 SpecialPaths.MyComputer 표시입니다.</summary>
        public string Location
        {
            get => _location;
            set
            {
                if (SetProperty(ref _location, value))
                {
                    OnPropertyChanged(nameof(Header));
                }
            }
        }

        public string Header
        {
            get
            {
                if (SpecialPaths.IsMyComputer(Location))
                {
                    return SpecialPaths.MyComputerDisplayName;
                }

                var name = System.IO.Path.GetFileName(Location.TrimEnd('\\', '/'));
                return name.Length > 0 ? name : Location;
            }
        }
    }

    private readonly ObservableCollection<FileEntry> _files = new();
    private readonly ObservableCollection<BrowserTab> _tabs = new();
    private int _listVersion;
    private Point _dragStart;
    private ListBoxItem? _deferredItem;
    private bool _dragArmed;

    // 정렬 기준입니다. 편집 창을 여러 번 열어도 이 실행 동안은 마지막 선택을 따릅니다.
    private static SortColumn s_sortColumn = SortColumn.Name;
    private static SortDirection s_sortDirection = SortDirection.Ascending;

    public PopupExplorer()
    {
        InitializeComponent();

        Files.ItemsSource = _files;
        Tabs.ItemsSource = _tabs;
        UpdateHeaders();

        Loaded += (_, _) => ApplySide(!global::Explorer.App.App.Host.Settings.View.PopupExplorerOnLeft);
    }

    // ── 배치 (왼쪽·오른쪽) ──

    private ColumnDefinition[]? _homeColumns;
    private readonly Dictionary<UIElement, int> _homeCells = new();
    private Thickness _homeMargin;

    /// <summary>탐색기를 반대쪽으로 옮기고, 그 선택을 설정에 남깁니다. 다음에 여는 편집 창도 같은 쪽에 둡니다.</summary>
    private void OnToggleSide(object sender, RoutedEventArgs e)
    {
        var host = global::Explorer.App.App.Host;
        host.Settings.View.PopupExplorerOnLeft = !host.Settings.View.PopupExplorerOnLeft;
        host.SettingsService.Save(host.Settings);
        ApplySide(!host.Settings.View.PopupExplorerOnLeft);
    }

    /// <summary>
    /// 탐색기가 들어 있는 격자의 칸 차례를 바꿔 탐색기를 왼쪽 또는 오른쪽 끝에 둡니다.
    /// XAML 에서는 탐색기를 맨 오른쪽 칸에, 그 바로 왼쪽에 경계선을 둡니다(이것이 "오른쪽" 모양).
    /// 왼쪽으로 옮길 때는 탐색기와 경계선을 맨 앞으로 보내고, 나머지 칸은 차례를 지킨 채 뒤로 밉니다.
    /// 칸 정의(ColumnDefinition)를 통째로 옮기므로, 창 쪽 코드가 이름으로 잡은 칸 너비 셈은 그대로 맞습니다.
    /// </summary>
    private void ApplySide(bool right)
    {
        if (Parent is not Grid grid)
        {
            return;
        }

        var count = grid.ColumnDefinitions.Count;

        if (count < 3 || (_homeColumns is null && Grid.GetColumn(this) != count - 1))
        {
            return;
        }

        if (_homeColumns is null)
        {
            _homeColumns = new ColumnDefinition[count];
            grid.ColumnDefinitions.CopyTo(_homeColumns, 0);
            _homeMargin = Margin;

            foreach (UIElement child in grid.Children)
            {
                _homeCells[child] = Grid.GetColumn(child);
            }
        }

        int Place(int home) => right ? home : home == count - 1 ? 0 : home == count - 2 ? 1 : home + 2;

        var ordered = new ColumnDefinition[count];

        for (var i = 0; i < count; i++)
        {
            ordered[Place(i)] = _homeColumns[i];
        }

        grid.ColumnDefinitions.Clear();

        foreach (var column in ordered)
        {
            grid.ColumnDefinitions.Add(column);
        }

        foreach (var (child, home) in _homeCells)
        {
            Grid.SetColumn(child, Place(home));
        }

        // 바깥 여백도 좌우를 맞바꿔 창 가장자리 쪽에 여백이 오게 합니다.
        Margin = right
            ? _homeMargin
            : new Thickness(_homeMargin.Right, _homeMargin.Top, _homeMargin.Left, _homeMargin.Bottom);

        SideButton.ToolTip = right ? "탐색기를 왼쪽으로 옮깁니다" : "탐색기를 오른쪽으로 옮깁니다";
    }

    /// <summary>파일 가운데 무엇을 보일지입니다. 비워 두면 모든 파일을 보입니다. 폴더는 늘 보입니다.</summary>
    public Func<string, bool>? FileFilter { get; set; }

    /// <summary>목록 아래 안내 글입니다.</summary>
    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    /// <summary>파일을 두 번 누르거나 골라서 Enter 를 누르면 알립니다. 목록 차례로 담아 줍니다.</summary>
    public event Action<IReadOnlyList<string>>? FilesActivated;

    /// <summary>폴더를 못 읽었을 때처럼 상태 줄에 알릴 말이 생기면 알립니다.</summary>
    public event Action<string>? StatusMessage;

    /// <summary>지금 탭의 폴더입니다. 내 PC 를 보고 있으면 null 입니다.</summary>
    public string? CurrentFolder { get; private set; }

    private BrowserTab? CurrentTab => Tabs.SelectedItem as BrowserTab;

    /// <summary>처음 탭을 엽니다. 폴더가 없으면 문서 폴더로 엽니다.</summary>
    public void Start(string? folder)
    {
        if (_tabs.Count > 0)
        {
            return;
        }

        OpenInNewTab(folder is not null && Directory.Exists(folder)
            ? folder
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
    }

    /// <summary>새 탭으로 엽니다. 즐겨찾기 단추가 이것을 부릅니다.</summary>
    public void OpenInNewTab(string location)
    {
        var tab = new BrowserTab();
        _tabs.Add(tab);
        Tabs.SelectedItem = tab;
        Navigate(location, record: false);

        // 폴더가 없어 열지 못했으면 빈 탭을 남기지 않습니다.
        if (tab.Location.Length == 0 && _tabs.Count > 1)
        {
            _tabs.Remove(tab);
            Tabs.SelectedIndex = _tabs.Count - 1;
        }
    }

    /// <summary>즐겨찾기 표시(바탕 화면·내 PC 등)를 알아듣고 새 탭으로 엽니다.</summary>
    public void OpenFavorite(string favorite)
    {
        if (SpecialPaths.IsRecycleBin(favorite))
        {
            StatusMessage?.Invoke("휴지통은 이 목록에서 열 수 없습니다.");
            return;
        }

        OpenInNewTab(SpecialPaths.IsMyComputer(favorite) ? SpecialPaths.MyComputer : PathHelper.Expand(favorite));
    }

    /// <summary>지금 탭을 다시 읽습니다. 저장·내보내기로 파일이 생겼을 때 부릅니다.</summary>
    public void Refresh()
    {
        if (CurrentTab is { } tab)
        {
            Navigate(tab.Location, record: false);
        }
    }

    // ── 탭 ──

    private void OnNewTab(object sender, RoutedEventArgs e) => OpenInNewTab(CurrentTab?.Location ?? SpecialPaths.MyComputer);

    private void OnCloseTab(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BrowserTab tab })
        {
            CloseTab(tab);
        }
    }

    private void CloseTab(BrowserTab? tab)
    {
        // 탭은 하나는 남겨 둡니다.
        if (tab is null || _tabs.Count <= 1)
        {
            return;
        }

        var index = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        Tabs.SelectedIndex = Math.Clamp(index, 0, _tabs.Count - 1);
    }

    /// <summary>가운데 단추로 탭을 누르면 닫습니다.</summary>
    private void OnTabsMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(Tabs, source) is ListBoxItem { DataContext: BrowserTab tab })
        {
            CloseTab(tab);
            e.Handled = true;
        }
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CurrentTab is { Location.Length: > 0 } tab)
        {
            Navigate(tab.Location, record: false);
        }
    }

    // ── 이동 ──

    /// <summary>
    /// 지금 탭을 그 폴더로 옮깁니다. <paramref name="record"/>가 켜져 있으면 떠나는 폴더를 뒤로 기록에 넣습니다.
    /// "내 PC" 는 드라이브 목록을 보입니다.
    /// </summary>
    public void Navigate(string location, bool record = true)
    {
        var tab = CurrentTab;

        if (tab is null || string.IsNullOrWhiteSpace(location))
        {
            return;
        }

        var myComputer = SpecialPaths.IsMyComputer(location);

        if (!myComputer)
        {
            location = PathHelper.Expand(location);

            if (!Directory.Exists(location))
            {
                StatusMessage?.Invoke($"폴더가 없습니다: {location}");
                FolderBox.Text = SpecialPaths.ToDisplay(tab.Location);
                return;
            }
        }

        var entries = new List<FileEntry>();

        try
        {
            if (myComputer)
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    var label = drive.Name;

                    try
                    {
                        if (drive.IsReady && drive.VolumeLabel.Length > 0)
                        {
                            label = $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";
                        }
                    }
                    catch (IOException)
                    {
                        // 준비되지 않은 드라이브는 이름만 보입니다.
                    }

                    entries.Add(new FileEntry(label, drive.Name, true));
                }
            }
            else
            {
                var found = new List<FileSystemEntry>();

                foreach (var info in new DirectoryInfo(location).EnumerateFileSystemInfos())
                {
                    var entry = FileSystemEntry.FromInfo(info);

                    // 탐색기처럼 숨김 속성만 봅니다. 시스템 속성만 있는 항목(예: MYBOX N:\ 의 폴더)은 보여 줍니다.
                    if ((entry.Attributes & FileAttributes.Hidden) != 0)
                    {
                        continue;
                    }

                    // 폴더와 바로가기는 파일 거르개와 상관없이 늘 보입니다. 바로가기는 다른 폴더로 건너가는 문일 때가 많습니다.
                    var shortcut = string.Equals(entry.Extension, ".lnk", StringComparison.OrdinalIgnoreCase);

                    if (entry.IsDirectory || shortcut || FileFilter is null || FileFilter(entry.FullPath))
                    {
                        found.Add(entry);
                    }
                }

                // 메인 탐색기와 같은 비교자입니다. 폴더와 바로가기를 위에 모으고, 고른 열·방향으로 정렬합니다.
                found.Sort(new FileSystemEntryComparer(s_sortColumn, s_sortDirection));

                foreach (var entry in found)
                {
                    entries.Add(new FileEntry(entry));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage?.Invoke($"폴더를 읽지 못했습니다: {ex.Message}");
            FolderBox.Text = SpecialPaths.ToDisplay(tab.Location);
            return;
        }

        var target = myComputer ? SpecialPaths.MyComputer : location;

        if (record && tab.Location.Length > 0 && !string.Equals(tab.Location, target, StringComparison.OrdinalIgnoreCase))
        {
            tab.Back.Add(tab.Location);
            tab.Forward.Clear();
        }

        tab.Location = target;
        CurrentFolder = myComputer ? null : location;
        FolderBox.Text = SpecialPaths.ToDisplay(target);

        _files.Clear();

        foreach (var entry in entries)
        {
            _files.Add(entry);
        }

        BackButton.IsEnabled = tab.Back.Count > 0;
        ForwardButton.IsEnabled = tab.Forward.Count > 0;
        UpButton.IsEnabled = !myComputer;

        LoadIcons(entries);
    }

    /// <summary>목록의 아이콘을 셸에게 받아 채웁니다. 그 사이 다른 폴더로 가면 멈춥니다.</summary>
    private async void LoadIcons(IReadOnlyList<FileEntry> entries)
    {
        var version = ++_listVersion;

        foreach (var entry in entries)
        {
            if (version != _listVersion)
            {
                return;
            }

            try
            {
                entry.Icon = await global::Explorer.App.App.Host.Thumbnails
                    .GetAsync(entry.Path, 32, iconOnly: true, CancellationToken.None)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"아이콘을 받지 못했습니다 {entry.Path}: {ex.Message}");
            }
        }
    }

    private void OnBack(object sender, RoutedEventArgs e) => GoBack();

    private void OnForward(object sender, RoutedEventArgs e) => GoForward();

    public void GoBack()
    {
        if (CurrentTab is not { Back.Count: > 0 } tab)
        {
            return;
        }

        var previous = tab.Back[^1];
        tab.Back.RemoveAt(tab.Back.Count - 1);
        tab.Forward.Add(tab.Location);
        Navigate(previous, record: false);
    }

    public void GoForward()
    {
        if (CurrentTab is not { Forward.Count: > 0 } tab)
        {
            return;
        }

        var next = tab.Forward[^1];
        tab.Forward.RemoveAt(tab.Forward.Count - 1);
        tab.Back.Add(tab.Location);
        Navigate(next, record: false);
    }

    private void OnFolderUp(object sender, RoutedEventArgs e) => GoUp();

    /// <summary>위 폴더로 갑니다. 드라이브 뿌리에서는 내 PC 로 갑니다.</summary>
    public void GoUp()
    {
        if (CurrentTab is not { } tab || SpecialPaths.IsMyComputer(tab.Location))
        {
            return;
        }

        var parent = Directory.GetParent(tab.Location.TrimEnd('\\'));
        Navigate(parent is null ? SpecialPaths.MyComputer : parent.FullName);
    }

    private void OnFolderBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Navigate(FolderBox.Text.Trim().Trim('"'));
            e.Handled = true;
        }
    }

    // ── 목록 ──

    private void OnFileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(Files, source) is ListBoxItem { DataContext: FileEntry entry })
        {
            Open(entry);
        }
    }

    /// <summary>
    /// 한 줄을 엽니다. 폴더는 그 폴더로 갑니다.
    /// 바로가기는 대상을 읽어 폴더면 이 탭에서 그 폴더로 가고(메인 탐색기와 같음), 파일이면 대상 파일을 알립니다.
    /// 대상이 네트워크에 있으면 있는지 보는 데도 시간이 걸려 다른 갈래에서 봅니다.
    /// </summary>
    private async void Open(FileEntry entry)
    {
        if (entry.IsFolder)
        {
            Navigate(entry.Path);
            return;
        }

        if (!entry.IsShortcut)
        {
            FilesActivated?.Invoke(new[] { entry.Path });
            return;
        }

        var path = entry.Path;
        var (target, isFolder, isFile) = await System.Threading.Tasks.Task.Run(() =>
        {
            var resolved = ShellShortcut.ReadTarget(path);
            return resolved is null
                ? (null, false, false)
                : (resolved, Directory.Exists(resolved), File.Exists(resolved));
        }).ConfigureAwait(true);

        if (target is null || (!isFolder && !isFile))
        {
            StatusMessage?.Invoke($"바로가기 대상을 찾지 못했습니다: {entry.Name}");
            return;
        }

        if (isFolder)
        {
            DiagnosticLog.Write($"편집 창 탐색기: 바로가기 따라가기 {entry.Name} → {target}");
            Navigate(target);
            return;
        }

        if (FileFilter is not null && !FileFilter(target))
        {
            StatusMessage?.Invoke($"이 창에서 다룰 수 없는 파일입니다: {Path.GetFileName(target)}");
            return;
        }

        FilesActivated?.Invoke(new[] { target });
    }

    // ── 정렬 ──

    /// <summary>머리글을 누르면 그 열로 정렬합니다. 같은 열을 다시 누르면 방향을 바꿉니다.</summary>
    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Tag: string tag } || !Enum.TryParse<SortColumn>(tag, out var column))
        {
            return;
        }

        if (column == s_sortColumn)
        {
            s_sortDirection = s_sortDirection == SortDirection.Ascending ? SortDirection.Descending : SortDirection.Ascending;
        }
        else
        {
            s_sortColumn = column;
            s_sortDirection = SortDirection.Ascending;
        }

        ResortList();
        e.Handled = true;
    }

    /// <summary>지금 목록을 다시 정렬합니다. 폴더를 다시 읽지 않고, 고른 것도 지킵니다. 드라이브 목록은 그대로 둡니다.</summary>
    private void ResortList()
    {
        UpdateHeaders();

        if (_files.Count == 0 || _files[0].IsDrive)
        {
            return;
        }

        var chosen = new List<object>(Files.SelectedItems.Cast<object>());
        var comparer = new FileSystemEntryComparer(s_sortColumn, s_sortDirection);
        var sorted = _files.OrderBy(entry => entry.Entry, comparer).ToList();

        _files.Clear();

        foreach (var entry in sorted)
        {
            _files.Add(entry);
        }

        foreach (var item in chosen)
        {
            Files.SelectedItems.Add(item);
        }
    }

    /// <summary>정렬한 열 머리글에 방향 표시(▲▼)를 붙입니다.</summary>
    private void UpdateHeaders()
    {
        foreach (var (header, label, column) in new[]
                 {
                     (NameHeader, "이름", SortColumn.Name),
                     (ModifiedHeader, "수정한 날짜", SortColumn.LastWriteTime),
                     (TypeHeader, "유형", SortColumn.Type),
                     (SizeHeader, "크기", SortColumn.Size)
                 })
        {
            header.Content = column == s_sortColumn
                ? label + (s_sortDirection == SortDirection.Ascending ? " ▲" : " ▼")
                : label;
        }
    }

    private void OnFilesKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when Files.SelectedItems.Count == 1 && Files.SelectedItem is FileEntry single
                                && (single.IsFolder || single.IsShortcut):
                Open(single);
                break;
            case Key.Enter:
                var chosen = ChosenFiles();

                if (chosen.Length > 0)
                {
                    FilesActivated?.Invoke(chosen);
                }

                break;
            case Key.Back:
                GoUp();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>이 탐색기 안에서만 쓰는 단축키입니다. 초점이 탐색기 안에 있을 때만 받습니다.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        switch (e.Key)
        {
            case Key.T when ctrl:
                OnNewTab(this, e);
                break;
            case Key.W when ctrl:
                CloseTab(CurrentTab);
                break;
            case Key.System when e.SystemKey == Key.Left:
                GoBack();
                break;
            case Key.System when e.SystemKey == Key.Right:
                GoForward();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>고른 파일들입니다. 고른 차례가 아니라 목록 차례로 돌려줍니다.</summary>
    private string[] ChosenFiles()
    {
        var paths = new List<string>();

        foreach (var item in Files.SelectedItems)
        {
            if (item is FileEntry { IsFolder: false } entry)
            {
                paths.Add(entry.Path);
            }
        }

        paths.Sort(NaturalStringComparer.Instance);
        return paths.ToArray();
    }

    /// <summary>끌 때 넘길 것입니다. 일괄 이름 바꾸기처럼 폴더도 받는 창을 위해 고른 폴더도 함께 넘깁니다.</summary>
    private string[] ChosenForDrag()
    {
        var paths = new List<string>();

        foreach (var item in Files.SelectedItems)
        {
            if (item is FileEntry entry && !SpecialPaths.IsMyComputer(CurrentTab?.Location))
            {
                paths.Add(entry.Path);
            }
        }

        paths.Sort(NaturalStringComparer.Instance);
        return paths.ToArray();
    }

    /// <summary>
    /// 누른 자리를 끌기 기준으로 기억합니다.
    /// 여러 개를 고른 채 그 가운데 하나를 그냥 누르면 ListBox 는 누르는 순간 그 하나만 남기고 선택을 풉니다.
    /// 그래서 여러 파일을 한꺼번에 끌 수 없었습니다. 이때는 선택을 떼는 순간까지 미루고, 끌면 고른 것 전부를 넘깁니다.
    /// </summary>
    private void OnFilesMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Files);
        _deferredItem = null;

        // 머리글이나 열 너비 손잡이를 끄는 것은 파일 끌기가 아닙니다. 줄을 눌렀을 때만 끌기를 준비합니다.
        _dragArmed = e.OriginalSource is DependencyObject pressed
                     && ItemsControl.ContainerFromElement(Files, pressed) is ListBoxItem;

        if (e.ClickCount > 1 || Keyboard.Modifiers != ModifierKeys.None || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(Files, source) is ListBoxItem { IsSelected: true } item
            && Files.SelectedItems.Count > 1)
        {
            _deferredItem = item;
            item.Focus();
            e.Handled = true;
        }
    }

    /// <summary>끌지 않고 뗐으면 미뤄 둔 선택(누른 하나만 고르기)을 이제 합니다.</summary>
    private void OnFilesMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_deferredItem is { } item)
        {
            _deferredItem = null;
            Files.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    private void OnFilesMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !_dragArmed)
        {
            return;
        }

        var moved = e.GetPosition(Files) - _dragStart;

        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _deferredItem = null;
        _dragArmed = false;

        var paths = ChosenForDrag();

        if (paths.Length == 0)
        {
            return;
        }

        DragDrop.DoDragDrop(Files, new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy);
    }
}
