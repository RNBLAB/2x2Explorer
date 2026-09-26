using System;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Explorer.App.Services;
using Explorer.App.ViewModels;
using Explorer.Core.Models;
using Explorer.Core.Services;
using Explorer.Core.Settings;

namespace Explorer.App.Views.Panes;

/// <summary>
/// 분할 패널 하나의 껍데기입니다.
/// 상단 바(메뉴 + 탭)와 본문 자리를 제공하고, 역할에 맞는 내용을 본문에 꽂습니다.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class PaneHost : UserControl, IDisposable
{
    private bool _isActive;
    private bool _disposed;

    public PaneHost()
    {
        InitializeComponent();
    }

    /// <summary>이 패널이 클릭됐을 때 발생합니다. 미리보기 패널은 이 신호를 내지 않습니다.</summary>
    public event EventHandler<PaneSettings>? Activated;

    /// <summary>이 칸의 역할을 바꿔 달라는 요청입니다. 칸을 새로 만드는 일은 창이 합니다.</summary>
    public event Action<PaneHost, PaneRole>? RoleChangeRequested;

    /// <summary>이 칸을 다른 역할(탐색기·미리보기)로 바꿔 달라고 창에 알립니다.</summary>
    public void RequestRole(PaneRole role) => RoleChangeRequested?.Invoke(this, role);

    /// <summary>안쪽 요소에서 위로 올라가며 그것을 담은 칸을 찾습니다.</summary>
    public static PaneHost? FindOwner(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is PaneHost host)
            {
                return host;
            }

            element = element is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return null;
    }

    /// <summary>이 패널의 설정입니다. 초기화 전에는 null입니다.</summary>
    public PaneSettings? Settings { get; private set; }

    /// <summary>파일 패널일 때의 뷰모델입니다. 미리보기 패널은 null입니다.</summary>
    public PaneViewModel? Pane { get; private set; }

    /// <summary>미리보기 전용 패널일 때의 뷰모델입니다. 파일 패널은 null입니다.</summary>
    public PreviewViewModel? StandalonePreview { get; private set; }

    /// <summary>미리보기 전용 패널인지 여부입니다.</summary>
    public bool IsPreviewPane => StandalonePreview is not null;

    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            PaneBorder.BorderBrush = value
                ? (System.Windows.Media.Brush)FindResource("Pane.BorderActive")
                : (System.Windows.Media.Brush)FindResource("Pane.Border");
            PaneBorder.BorderThickness = new Thickness(value ? 2 : 1);
        }
    }

    public void Initialize(AppHost host, PaneSettings settings)
    {
        ArgumentNullException.ThrowIfNull(host);
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));

        if (settings.Role == PaneRole.Preview)
        {
            InitializePreview(host);
            return;
        }

        if (settings.Role == PaneRole.Search)
        {
            InitializeSearch(host, settings);
            return;
        }

        var pane = new PaneViewModel(host, settings);
        Pane = pane;
        DataContext = pane;

        // 상단 바는 탭이 다 씁니다.
        // 패널·탭 명령은 탭 스트립을 오른쪽 클릭해서 부릅니다.
        MenuHost.Visibility = Visibility.Collapsed;
        TabStripHost.Content = new PaneTabStrip { DataContext = pane };

        // 본문은 목록과 미리보기를 나란히 둡니다.
        // 목록 화면은 패널당 하나만 두고 선택된 탭에 연결합니다.
        BodyHost.Content = new PaneBody { DataContext = pane };

        InputBindings.Add(new KeyBinding(pane.NewTabCommand, Key.T, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(pane.CloseTabCommand, Key.W, ModifierKeys.Control));

        pane.Initialize();
    }

    /// <summary>
    /// 미리보기 전용 칸입니다. 탭도 목록도 없이 미리보기 하나만 둡니다.
    /// 무엇을 보여 줄지는 마지막으로 누른 탐색기 칸이 알려 줍니다.
    /// </summary>
    private void InitializePreview(AppHost host)
    {
        var preview = new PreviewViewModel(host.Settings.Preview, host.Thumbnails);
        StandalonePreview = preview;
        DataContext = preview;

        MenuHost.Visibility = Visibility.Collapsed;

        TabStripHost.Content = new TextBlock
        {
            Text = "미리보기",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (System.Windows.Media.Brush)FindResource("Text.Secondary"),
            FontSize = 11
        };

        BodyHost.Content = new PreviewPane { DataContext = preview };

        // 칸 어디서든 오른쪽 클릭으로 탐색기로 되돌립니다.
        // 문서 미리보기 프로그램이 그리는 자리는 마우스를 가져가므로, 그때는 위쪽 이름표 줄에서 누르면 됩니다.
        PaneBorder.ContextMenu = BuildPreviewMenu();
    }

    private ContextMenu BuildPreviewMenu()
    {
        var menu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };
        var itemStyle = (Style)FindResource("DarkMenuItem");

        var toExplorer = new MenuItem
        {
            Header = "미리보기 끄기 (이 칸을 탐색기로 전환)",
            Style = itemStyle
        };
        toExplorer.Click += (_, _) => RequestRole(PaneRole.Files);
        menu.Items.Add(toExplorer);

        return menu;
    }

    /// <summary>
    /// 찾기 전용 칸입니다. 탭 줄 자리에 이름표만 놓고, 본문은 탐색기 목록을 그대로 씁니다.
    /// 열자마자 주소 표시줄이 검색어 칸으로 바뀝니다.
    /// </summary>
    private void InitializeSearch(AppHost host, PaneSettings settings)
    {
        var pane = new PaneViewModel(host, settings);
        Pane = pane;
        DataContext = pane;

        MenuHost.Visibility = Visibility.Collapsed;

        TabStripHost.Content = new TextBlock
        {
            Text = "검색",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (System.Windows.Media.Brush)FindResource("Text.Secondary"),
            FontSize = 11
        };

        BodyHost.Content = new PaneBody { DataContext = pane };

        pane.Initialize();

        // 찾기 칸의 시작 자리는 내 PC 입니다. 어느 폴더에도 매이지 않은 자리라야 온 컴퓨터를 찾는 뜻이 맞습니다.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (pane.SelectedTab is not { } tab)
            {
                return;
            }

            tab.NavigateAsync(SpecialPaths.MyComputer, recordHistory: false)
                .ContinueWith(
                    _ => tab.BeginSearch(),
                    System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }));
    }

    /// <summary>미리보기 전용 칸에 보여 줄 파일을 건네줍니다.</summary>
    public void ShowSelection(FileItemViewModel? item) => StandalonePreview?.SetTarget(item);

    /// <summary>창을 닫을 때 지금 열려 있는 탭을 설정에 적어 둡니다.</summary>
    public void PersistState() => Pane?.PersistState();

    /// <summary>검색을 쓸 수 있게 됐다고 탭들에 알립니다. 검색 단추가 그때 켜집니다.</summary>
    public void NotifySearchReady() => Pane?.NotifySearchReady();

    /// <summary>미리보기를 켜고 끈 것을 화면에 옮깁니다.</summary>
    public void NotifyPreviewChanged() => Pane?.NotifyPreviewChanged();

    /// <summary>즐겨찾기를 고친 것을 탐색 창에 옮깁니다.</summary>
    public void RefreshFavorites() => Pane?.RefreshFavorites();

    /// <summary>탐색 창 너비를 되돌린 것을 화면에 옮깁니다.</summary>
    public void NotifyTreeSplitChanged() => Pane?.NotifyTreeSplitChanged();

    /// <summary>
    /// 이 칸에 폴더 나무를 붙일지, 붙인다면 목록의 어느 쪽에 세울지 정합니다.
    /// 세로로 긴 칸에서만 켜고, 창의 오른쪽 끝에 놓인 칸에서는 오른쪽에 세웁니다.
    /// </summary>
    public void SetTreeVisible(bool visible, bool onRight = false)
    {
        if (Pane is { } pane)
        {
            // 쪽을 먼저 정해야 나무를 펴는 순간에 이미 제자리로 열립니다.
            pane.TreeOnRight = onRight;
            pane.ShowTree = visible;
        }
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        RaiseActivated();

        // 마우스 옆면의 4·5번 버튼입니다. 브라우저와 같은 규칙으로 뒤로·앞으로에 씁니다.
        var command = e.ChangedButton switch
        {
            MouseButton.XButton1 => Pane?.SelectedTab?.BackCommand,
            MouseButton.XButton2 => Pane?.SelectedTab?.ForwardCommand,
            _ => null
        };

        if (command is null)
        {
            return;
        }

        e.Handled = true;

        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    protected override void OnPreviewGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        // 클릭뿐 아니라 키보드로 옮겨 와도 활성 패널이 됩니다.
        base.OnPreviewGotKeyboardFocus(e);
        RaiseActivated();
    }

    private void RaiseActivated()
    {
        if (Settings is not null)
        {
            Activated?.Invoke(this, Settings);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Pane?.Dispose();
        StandalonePreview?.Dispose();
    }
}
