using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Explorer.App.Services;
using Explorer.App.Views;
using Explorer.Core.Services;

namespace Explorer.App;

[SupportedOSPlatform("windows")]
public partial class App : Application
{
    /// <summary>
    /// 앱이 쓰는 서비스 묶음입니다. 서비스가 늘어나면 생성자 주입으로 바꿉니다.
    /// </summary>
    public static AppHost Host { get; private set; } = null!;

    private ThemeService? _theme;

    /// <summary>알림 창을 몇 번 띄웠는지입니다. 같은 문제가 되풀이될 때 창으로 뒤덮지 않으려고 셉니다.</summary>
    private int _reported;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 설치 마법사가 "프로그램이 켜져 있다"고 알아보는 표식입니다. 되도록 먼저 겁니다.
        RunningMarker.Hold();

        DiagnosticLog.Reset();
        DiagnosticLog.Write("Explorer 시작");

        // 바인딩이 풀려도 WPF 는 조용히 넘어갑니다. 그 흔적을 로그에 남깁니다.
        BindingErrorLog.Start();

        // 창마다 아이콘을 달아 줍니다.
        // 이름 없는 스타일로는 걸리지 않습니다. 암묵 스타일은 형이 정확히 같은 것에만 붙는데
        // 우리 창은 모두 Window 를 물려받은 다른 형이기 때문입니다.
        // 형 단위 처리기는 물려받은 형에도 걸리므로 창이 뜰 때 한 번씩 들릅니다.
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnAnyWindowLoaded));

        // 아무도 받지 않은 예외가 여기까지 옵니다. 받지 않으면 프로그램이 그 자리에서 닫힙니다.
        DispatcherUnhandledException += OnScreenException;
        TaskScheduler.UnobservedTaskException += OnForgottenTaskException;

        // 다운로드 폴더는 셸에게 물어야 찾습니다. 설정을 읽기 전에 그 길을 꽂아 둡니다.
        SpecialPaths.DownloadsLocator = Explorer.Shell.Interop.KnownFolders.Downloads;

        Host = AppHost.Create();

        _theme = new ThemeService(this);
        _theme.Apply(Host.Settings.Theme);

        StartStallWatch();
        WatchDispatcherWork();

        var watch = System.Diagnostics.Stopwatch.StartNew();

        var window = new MainWindow(Host);
        MainWindow = window;
        window.Show();

        DiagnosticLog.Write($"창 띄우기까지 {watch.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// 화면 갈래가 얼마나 멈춰 있었는지 지켜봅니다.
    ///
    /// 0.1초마다 깨어나도록 시계를 걸어 두고, 깨어난 사이가 그보다 한참 벌어졌으면
    /// 그동안 화면이 멈춰 있었다는 뜻입니다. 누가 붙들었는지는 알 수 없지만 언제 얼마나인지는 남습니다.
    /// </summary>
    private void StartStallWatch()
    {
        var last = System.Diagnostics.Stopwatch.StartNew();

        var beat = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };

        beat.Tick += (_, _) =>
        {
            var gap = last.ElapsedMilliseconds;
            last.Restart();

            if (gap > 300)
            {
                DiagnosticLog.Write($"화면 갈래가 {gap}ms 밀렸습니다 (하던 일: {UiWatch.Doing})");
            }
        };

        beat.Start();

        // 위 시계는 뒷일 차례라, 그리기나 배치가 바쁘기만 해도 밀립니다.
        // 이 시계는 사람이 누른 것과 같은 차례로 깨어나므로, 이것까지 밀리면 갈래가 정말 막힌 것입니다.
        var touched = System.Diagnostics.Stopwatch.StartNew();

        var input = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };

        input.Tick += (_, _) =>
        {
            var gap = touched.ElapsedMilliseconds;
            touched.Restart();

            if (gap > 300)
            {
                DiagnosticLog.Write($"누름 차례가 {gap}ms 막혔습니다 (하던 일: {UiWatch.Doing})");
            }
        };

        input.Start();
    }

    /// <summary>
    /// 화면 갈래가 처리하는 일감 하나하나에 시계를 답니다.
    ///
    /// 멈춤 시계로는 언제 얼마나 멈췄는지만 알 수 있고 누가 붙들었는지는 모릅니다.
    /// 여기서는 일감이 시작하고 끝날 때마다 걸린 시간을 재어, 오래 걸린 일감의 이름을 남깁니다.
    /// 그리기·배치처럼 WPF 가 스스로 하는 일도 일감으로 오므로 함께 잡힙니다.
    /// 짧은 일감이 끝없이 몰려와 누름을 굶기는 경우도 있어, 1초마다 일감 수도 셉니다.
    /// </summary>
    private void WatchDispatcherWork()
    {
        var method = typeof(DispatcherOperation).GetField(
            "_method",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var starts = new System.Collections.Generic.Dictionary<DispatcherOperation, long>();
        var counts = new System.Collections.Generic.Dictionary<string, (int Count, double Total)>();
        var window = System.Diagnostics.Stopwatch.StartNew();

        string Describe(DispatcherOperation operation)
        {
            if (method?.GetValue(operation) is Delegate work)
            {
                return $"{work.Method.DeclaringType?.Name}.{work.Method.Name}";
            }

            return "알 수 없는 일감";
        }

        var hooks = Dispatcher.Hooks;

        hooks.OperationStarted += (_, e) => starts[e.Operation] = System.Diagnostics.Stopwatch.GetTimestamp();

        hooks.OperationAborted += (_, e) => starts.Remove(e.Operation);

        hooks.OperationCompleted += (_, e) =>
        {
            if (!starts.Remove(e.Operation, out var start))
            {
                return;
            }

            var spent = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0
                        / System.Diagnostics.Stopwatch.Frequency;

            var name = Describe(e.Operation);

            if (spent > 100)
            {
                DiagnosticLog.Write($"[긴 일감] {name} ({e.Operation.Priority}) {spent:F0}ms");
            }

            var (count, total) = counts.TryGetValue(name, out var seen) ? seen : (0, 0.0);
            counts[name] = (count + 1, total + spent);

            // 1초마다 무엇이 얼마나 몰려왔는지 셉니다. 한 번에 수백 개가 몰리면 누름이 굶습니다.
            if (window.ElapsedMilliseconds < 1000)
            {
                return;
            }

            var busiest = string.Empty;
            var busiestCount = 0;
            var busiestTotal = 0.0;
            var all = 0;
            var allTotal = 0.0;

            foreach (var pair in counts)
            {
                all += pair.Value.Count;
                allTotal += pair.Value.Total;

                if (pair.Value.Total > busiestTotal)
                {
                    busiest = pair.Key;
                    busiestCount = pair.Value.Count;
                    busiestTotal = pair.Value.Total;
                }
            }

            // 1초 가운데 절반 넘게 일감을 처리하느라 바빴을 때만 적습니다.
            if (allTotal > 500)
            {
                DiagnosticLog.Write(
                    $"[바쁜 1초] 일감 {all}개 {allTotal:F0}ms, 가장 오래 쓴 것 {busiest} {busiestCount}개 {busiestTotal:F0}ms");
            }

            counts.Clear();
            window.Restart();
        };
    }

    /// <summary>창들이 함께 쓰는 아이콘입니다. 한 번만 읽어 둡니다.</summary>
    private static System.Windows.Media.ImageSource? _icon;

    /// <summary>창이 뜰 때 아이콘을 답니다. 이미 제 아이콘을 단 창은 건드리지 않습니다.</summary>
    private static void OnAnyWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window || window.Icon is not null)
        {
            return;
        }

        _icon ??= LoadIcon();

        if (_icon is not null)
        {
            window.Icon = _icon;
        }
    }

    private static System.Windows.Media.ImageSource? LoadIcon()
    {
        try
        {
            var icon = new System.Windows.Media.Imaging.BitmapImage(
                new Uri("pack://application:,,,/Resources/app.ico", UriKind.Absolute));

            icon.Freeze();
            return icon;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            DiagnosticLog.WriteAlways($"창 아이콘을 읽지 못했습니다: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 화면 쪽에서 난 예외입니다. 여기까지 왔다는 것은 아무도 받지 않았다는 뜻입니다.
    /// 그대로 두면 창이 닫히므로, 기록을 남기고 한 번 알린 뒤 계속 돌립니다.
    /// </summary>
    private void OnScreenException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        DiagnosticLog.WriteAlways(
            $"화면 예외: {e.Exception.GetType().Name} {e.Exception.Message}{Environment.NewLine}{e.Exception.StackTrace}");

        e.Handled = true;

        // 같은 문제가 그리기마다 되풀이되면 창이 끝없이 뜹니다. 세 번까지만 알립니다.
        if (_reported >= 3)
        {
            return;
        }

        _reported++;

        MessageBox.Show(
            $"처리하지 못한 문제가 생겼습니다. 계속 쓸 수는 있습니다.{Environment.NewLine}{Environment.NewLine}"
                + $"{e.Exception.Message}{Environment.NewLine}{Environment.NewLine}"
                + $"자세한 내용은 다음 파일에 적었습니다.{Environment.NewLine}{DiagnosticLog.FilePath}",
            "2x2 Explorer",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    /// <summary>
    /// 기다리는 사람 없이 떠난 작업에서 난 예외입니다.
    /// 쓰레기 수거 때에야 올라오므로 알리지는 않고 기록만 남깁니다.
    /// </summary>
    private static void OnForgottenTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        DiagnosticLog.WriteAlways($"돌보지 않은 작업 예외: {e.Exception.Message}");
        e.SetObserved();
    }

    /// <summary>설정 창에서 테마를 바꿨을 때 곧바로 갈아 끼웁니다.</summary>
    public void ApplyTheme(Explorer.Core.Models.AppTheme theme) => _theme?.Apply(theme);

    protected override void OnExit(ExitEventArgs e)
    {
        // 창이 정상적으로 닫히지 않은 경로(로그오프 등)를 대비한 저장입니다.
        Host?.SettingsService.Save(Host.Settings);
        Host?.Dispose();
        _theme?.Dispose();
        RunningMarker.Release();
        base.OnExit(e);
    }
}
