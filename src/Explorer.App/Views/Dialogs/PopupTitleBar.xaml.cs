using System;
using System.ComponentModel;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using Explorer.Shell.Interop;

namespace Explorer.App.Views.Dialogs;

/// <summary>
/// 편집 창의 제목 줄입니다. 창 쪽에서는 <see cref="Apply"/> 를 생성자에서 한 번 부르고,
/// XAML 맨 위에 이 컨트롤을 두면 됩니다.
///
/// 윈도우가 그리는 제목 줄 대신 직접 그리는 까닭은 둘입니다. 메인 창과 모양을 맞추려는 것과,
/// 창이 화면보다 클 때 제목 줄이 화면 밖으로 나가 닫기 단추를 못 누르던 일을 막으려는 것입니다.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class PopupTitleBar : UserControl
{
    public static readonly DependencyProperty ExtraProperty =
        DependencyProperty.Register(nameof(Extra), typeof(object), typeof(PopupTitleBar));

    private Window? _window;

    public PopupTitleBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>제목과 창 단추 사이에 넣을 것입니다. PDF 편집기는 즐겨찾기 단추 줄을 넣습니다.</summary>
    public object? Extra
    {
        get => GetValue(ExtraProperty);
        set => SetValue(ExtraProperty, value);
    }

    /// <summary>
    /// 창을 직접 그리는 제목 줄 모양으로 바꿉니다. 창이 뜨기 전, 생성자에서 불러야 합니다.
    /// 창 크기를 화면 작업 영역 안으로 줄이고, 최대화할 때 작업 표시줄을 덮지 않게 합니다.
    /// </summary>
    public static void Apply(Window window) => Apply(window, fillScreen: false);

    /// <summary>
    /// <paramref name="fillScreen"/> 을 켜면 부른 창이 있는 화면의 작업 영역에 꽉 차게(최대화) 띄웁니다.
    /// PDF 편집·그림 편집·그림 일괄 편집·일괄 인쇄·일괄 이름 바꾸기 창이 씁니다(사용자 지정 2026-09-26).
    /// 먼저 그 화면 가운데에 보통 크기로 자리를 잡은 뒤 최대화하므로, [이전 크기로]를 누르면 그 크기로 돌아옵니다.
    /// </summary>
    public static void Apply(Window window, bool fillScreen)
    {
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.CanResize;
        window.BorderThickness = new Thickness(1);
        window.SetResourceReference(Control.BorderBrushProperty, "Pane.Border");

        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 0,
            CornerRadius = new CornerRadius(0),
            GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = new Thickness(6),
            UseAeroCaptionButtons = false
        });

        // 자리는 우리가 잡습니다. 부른 창(메인 창)이 있는 화면의 가운데에 띄웁니다.
        window.WindowStartupLocation = WindowStartupLocation.Manual;

        window.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(window) is HwndSource source)
            {
                source.AddHook(ClampMaximize);
            }

            PlaceOnOwnerScreen(window);

            if (fillScreen)
            {
                // 작업 표시줄을 덮지 않는 것은 ClampMaximize 가 맡습니다.
                window.WindowState = WindowState.Maximized;
            }
        };
    }

    /// <summary>
    /// 부른 창이 놓인 화면의 작업 영역 가운데에 둡니다. 화면보다 큰 창은 화면 안으로 줄입니다.
    /// 예전에는 주 모니터를 기준으로 삼아, 보조 모니터에서 부른 팝업이 주 모니터로 끌려가곤 했습니다.
    /// </summary>
    private static void PlaceOnOwnerScreen(Window window)
    {
        var owner = window.Owner ?? Application.Current?.MainWindow;
        var area = SystemParameters.WorkArea;

        if (owner is not null && !ReferenceEquals(owner, window))
        {
            var handle = new WindowInteropHelper(owner).Handle;

            if (WindowMaximize.TryGetWorkArea(handle, out var left, out var top, out var right, out var bottom))
            {
                // 물리 픽셀을 WPF 단위로 바꿉니다. 부른 창의 화면 배율을 씁니다.
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(owner);
                area = new Rect(
                    left / dpi.DpiScaleX,
                    top / dpi.DpiScaleY,
                    (right - left) / dpi.DpiScaleX,
                    (bottom - top) / dpi.DpiScaleY);
            }
        }

        var width = Math.Min(window.Width, Math.Max(200, area.Width - 24));
        var height = Math.Min(window.Height, Math.Max(200, area.Height - 24));

        window.MinWidth = Math.Min(window.MinWidth, width);
        window.MinHeight = Math.Min(window.MinHeight, height);
        window.Width = width;
        window.Height = height;
        window.Left = area.Left + ((area.Width - width) / 2);
        window.Top = area.Top + ((area.Height - height) / 2);
    }

    /// <summary>
    /// 제목 줄 가운데에 즐겨찾기 단추 줄을 둡니다. 메인 창에서 "상단"으로 체크한 즐겨찾기와 같습니다.
    /// 누르면 <paramref name="open"/> 을 부릅니다(보통 팝업 탐색기의 새 탭 열기). 즐겨찾기가 바뀌면 다시 만듭니다.
    /// </summary>
    public void EnableFavorites(Action<string> open)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        Extra = bar;

        void Build()
        {
            bar.Children.Clear();
            var settings = global::Explorer.App.App.Host.Settings;

            foreach (var favorite in settings.Favorites)
            {
                if (!settings.IsOnTitleBar(favorite) || Explorer.Core.Services.SpecialPaths.IsRecycleBin(favorite))
                {
                    continue;
                }

                var icon = new Image { Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
                System.Windows.Media.RenderOptions.SetBitmapScalingMode(icon, System.Windows.Media.BitmapScalingMode.HighQuality);

                var content = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 8, 0) };
                content.Children.Add(icon);
                content.Children.Add(new TextBlock { Text = DescribeFavorite(favorite), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });

                var button = new Button
                {
                    Style = (Style)FindResource("ToolButton"),
                    Width = double.NaN,
                    Height = 26,
                    Content = content,
                    ToolTip = $"{DescribeFavorite(favorite)} — 오른쪽 탐색기에 새 탭으로 엽니다"
                };

                var target = favorite;
                button.Click += (_, _) => open(target);
                bar.Children.Add(button);

                LoadFavoriteIcon(icon, favorite);
            }
        }

        EventHandler changed = (_, _) => Dispatcher.BeginInvoke(Build);
        global::Explorer.App.App.Host.FavoritesChanged += changed;

        // 창이 닫히면 알림을 끊습니다. 끊지 않으면 닫힌 창이 메모리에 남습니다.
        Loaded += (_, _) =>
        {
            if (Window.GetWindow(this) is { } window)
            {
                window.Closed += (_, _) => global::Explorer.App.App.Host.FavoritesChanged -= changed;
            }
        };

        Build();
    }

    private static async void LoadFavoriteIcon(Image icon, string favorite)
    {
        var shellPath = Explorer.Core.Services.SpecialPaths.IsMyComputer(favorite)
            ? "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"
            : Explorer.Core.Services.PathHelper.Expand(favorite);

        try
        {
            icon.Source = await global::Explorer.App.App.Host.Thumbnails
                .GetAsync(shellPath, 32, iconOnly: true, System.Threading.CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Explorer.Core.Services.DiagnosticLog.Write($"즐겨찾기 아이콘을 받지 못했습니다 {favorite}: {ex.Message}");
        }
    }

    private static string DescribeFavorite(string path)
    {
        if (Explorer.Core.Services.SpecialPaths.IsVirtual(path) || Explorer.Core.Services.SpecialPaths.IsKnownFolder(path))
        {
            return Explorer.Core.Services.SpecialPaths.ToDisplay(path);
        }

        var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        return name.Length > 0 ? name : path;
    }

    private static IntPtr ClampMaximize(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WindowMaximize.WindowMessageGetMinMaxInfo)
        {
            return IntPtr.Zero;
        }

        WindowMaximize.ClampToWorkArea(window, lParam);
        handled = true;
        return IntPtr.Zero;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            return;
        }

        _window = Window.GetWindow(this);

        if (_window is null)
        {
            return;
        }

        UpdateTitle();
        UpdateMaximizeButton();

        // 작업 표시줄에 없는 창(모달 대화상자)은 최소화하면 되찾을 길이 없습니다. 단추를 감춥니다.
        if (!_window.ShowInTaskbar)
        {
            MinimizeButton.Visibility = Visibility.Collapsed;
        }

        DependencyPropertyDescriptor.FromProperty(Window.TitleProperty, typeof(Window))
            .AddValueChanged(_window, (_, _) => UpdateTitle());

        _window.StateChanged += (_, _) => UpdateMaximizeButton();
    }

    private void UpdateTitle() => TitleText.Text = _window?.Title ?? string.Empty;

    private void UpdateMaximizeButton()
    {
        var maximized = _window?.WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "" : "";
        MaximizeButton.ToolTip = maximized ? "이전 크기로" : "최대화";
    }

    private void OnBarPressed(object sender, MouseButtonEventArgs e)
    {
        if (_window is null || e.OriginalSource is DependencyObject source && IsInsideButton(source))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        WindowDrag.Begin(new WindowInteropHelper(_window).Handle);
    }

    private static bool IsInsideButton(DependencyObject source)
    {
        for (var node = source; node is not null; node = node is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase)
            {
                return true;
            }

            if (node is PopupTitleBar)
            {
                return false;
            }
        }

        return false;
    }

    private void ToggleMaximize()
    {
        if (_window is not null)
        {
            _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.WindowState = WindowState.Minimized;
        }
    }

    private void OnMaximize(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnClose(object sender, RoutedEventArgs e) => _window?.Close();
}
