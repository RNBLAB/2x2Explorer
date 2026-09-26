using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Explorer.Core.Services;
using Explorer.Shell.Preview;

namespace Explorer.App.Views.Panes;

/// <summary>핸들러가 그리는 문서를 칸에 맞추는 방식입니다. 그림 미리보기의 맞춤과 같은 뜻입니다.</summary>
public enum HandlerFit
{
    Auto,
    Width,
    Height,
    Manual
}

/// <summary>
/// 윈도우 미리보기 핸들러가 문서를 그릴 자리입니다.
///
/// 핸들러는 WPF가 아니라 옛 방식의 창에 그립니다.
/// 그래서 빈 자식 창을 만들어 건네주고, 그 창을 WPF 화면에 끼워 넣습니다.
///
/// 창은 두 겹입니다.
/// 바깥 창은 칸 크기 그대로이고 넘치는 부분을 잘라 냅니다(보이는 틀).
/// 안쪽 창은 핸들러가 실제로 그리는 판이고, 확대하면 칸보다 커지며 바깥 창 안에서 위치를 옮겨 스크롤합니다.
/// 쪽 전체를 창에 맞춰 그리는 핸들러(한컴 등)는 판이 커지면 쪽도 커지므로 이것으로 확대·맞춤을 흉내 냅니다.
///
/// 핸들러와 주고받는 일은 전용 갈래(<see cref="PreviewHandlerWorker"/>)가 합니다.
/// 여기서는 쪽지만 건네고 기다리지 않습니다. 그래야 문서를 띄우는 동안에도 창이 멈추지 않습니다.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PreviewHandlerHost : HwndHost
{
    /// <summary>이만큼 기다려도 답이 없으면 칸에 알립니다.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    /// <summary>휠 한 칸에 굴리는 거리(화면 픽셀, 100% 배율 기준)입니다.</summary>
    private const int WheelStep = 60;

    private readonly PreviewHandlerWorker _worker = new();
    private readonly DispatcherTimer _patience;

    /// <summary>칸 크기 그대로인 바깥 창입니다. WPF 에 끼우는 것은 이것입니다.</summary>
    private IntPtr _window;

    /// <summary>핸들러가 그리는 안쪽 판입니다.</summary>
    private IntPtr _inner;

    private string? _wanted;

    /// <summary>핸들러에 이미 건넨 파일입니다. 크기만 바뀌었을 때 다시 띄우지 않으려고 기억합니다.</summary>
    private string? _requested;

    private double? _pageAspect;
    private HandlerFit _fit = HandlerFit.Auto;
    private double _zoom = 1.0;

    private int _innerX;
    private int _innerY;
    private int _innerWidth;
    private int _innerHeight;
    private int _offsetX;
    private int _offsetY;

    /// <summary>휠 훅 갈래가 읽습니다. 판이 칸보다 커서 우리가 굴려야 하는지입니다.</summary>
    private volatile bool _canScroll;

    /// <summary>휠 훅 갈래가 읽습니다. 확대·맞춤을 우리가 맡는 문서인지입니다.</summary>
    private volatile bool _zoomEnabled;

    public PreviewHandlerHost()
    {
        _patience = new DispatcherTimer { Interval = Patience };
        _patience.Tick += (_, _) =>
        {
            _patience.Stop();
            Stalled?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>미리보기 프로그램이 한참 답하지 않을 때 알립니다. 칸에 안내를 띄우는 데 씁니다.</summary>
    public event EventHandler? Stalled;

    /// <summary>미리보기 프로그램이 다 띄웠거나 실패했을 때 알립니다. 참이면 띄운 것입니다.</summary>
    public event EventHandler<bool>? Finished;

    /// <summary>문서 위에서 Ctrl+휠을 굴렸습니다. 화면 갈래에서 불립니다.</summary>
    public event EventHandler<WheelInput>? ZoomWheel;

    /// <summary>판 크기나 스크롤 위치가 바뀌었습니다. 스크롤 막대를 맞추는 데 씁니다.</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>판의 크기(픽셀)입니다.</summary>
    public int ExtentWidth => _innerWidth;

    public int ExtentHeight => _innerHeight;

    /// <summary>보이는 칸의 크기(픽셀)입니다.</summary>
    public int ViewportWidth => PixelWidth;

    public int ViewportHeight => PixelHeight;

    public int OffsetX => _offsetX;

    public int OffsetY => _offsetY;

    /// <summary>판 너비가 칸 너비의 몇 배인지입니다. 손 배율의 출발점입니다.</summary>
    public double WidthRatio => PixelWidth > 0 && _innerWidth > 0 ? _innerWidth / (double)PixelWidth : 1.0;

    /// <summary>
    /// 문서 쪽의 가로÷세로입니다. 알면 확대·맞춤을 켭니다. 모르면(null) 판은 늘 칸 크기입니다.
    /// </summary>
    public double? PageAspect
    {
        get => _pageAspect;
        set
        {
            _pageAspect = value is > 0 ? value : null;
            _zoomEnabled = _pageAspect is not null;
            Relayout(resetScroll: true);
        }
    }

    /// <summary>맞춤 방식을 정합니다. 손 배율이면 zoom 은 판 너비 ÷ 칸 너비입니다.</summary>
    public void SetFit(HandlerFit fit, double zoom = 1.0, WheelInput? anchor = null)
    {
        // 붙들 점: 마우스가 가리키는 판 위의 자리(비율)를 기억해 두었다가 크기를 바꾼 뒤 같은 자리가 마우스 아래 오게 합니다.
        double? fractionX = null;
        double? fractionY = null;
        var cursorX = 0;
        var cursorY = 0;

        if (anchor is { } input
            && _innerWidth > 0
            && _innerHeight > 0
            && ShellPreviewHandler.TryGetScreenOrigin(_window, out var originX, out var originY))
        {
            cursorX = input.ScreenX - originX;
            cursorY = input.ScreenY - originY;
            fractionX = (cursorX - _innerX) / (double)_innerWidth;
            fractionY = (cursorY - _innerY) / (double)_innerHeight;
        }

        _fit = fit;
        _zoom = zoom;
        Relayout(resetScroll: fractionX is null && fit != HandlerFit.Manual);

        if (fractionX is { } fx && fractionY is { } fy)
        {
            ScrollTo((int)Math.Round(fx * _innerWidth - cursorX), (int)Math.Round(fy * _innerHeight - cursorY));
        }
    }

    /// <summary>판을 그 위치로 굴립니다(픽셀).</summary>
    public void ScrollTo(int x, int y)
    {
        _offsetX = x;
        _offsetY = y;
        Relayout(resetScroll: false);
    }

    /// <summary>그 파일을 보여 줍니다. 창이 아직 없으면 생긴 뒤에 보여 줍니다.</summary>
    public void Show(string filePath)
    {
        _wanted = filePath;

        if (_inner == IntPtr.Zero || !HasRealSize)
        {
            // 창이 만들어지면 BuildWindowCore 가, 크기가 잡히면 OnRenderSizeChanged 가 이어서 띄웁니다.
            return;
        }

        Request(filePath);
    }

    /// <summary>보여 주던 것을 내립니다.</summary>
    public void Close()
    {
        _wanted = null;
        _requested = null;
        _patience.Stop();
        _worker.Close();
    }

    // 핸들러는 화면 픽셀로 크기를 받습니다. WPF 의 ActualWidth 는 96dpi 기준 단위라 배율(125%·150%)을 곱해야 합니다.
    private int PixelWidth => (int)Math.Max(Math.Round(ActualWidth * DpiScale().X), 1);

    private int PixelHeight => (int)Math.Max(Math.Round(ActualHeight * DpiScale().Y), 1);

    /// <summary>칸이 아직 펼쳐지지 않아 크기가 없는 동안입니다. 이때 띄우면 1×1 자리에 그리고 끝나는 핸들러가 있습니다.</summary>
    private bool HasRealSize => ActualWidth >= 8 && ActualHeight >= 8;

    private (double X, double Y) DpiScale()
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        return (dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1, dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1);
    }

    /// <summary>
    /// 맞춤 방식에 따라 판의 크기와 자리를 정하고 옮깁니다.
    /// 판이 칸보다 작으면 가운데에 두고, 크면 스크롤 위치만큼 왼쪽·위로 밀어 둡니다.
    /// </summary>
    private void Relayout(bool resetScroll)
    {
        var viewportWidth = PixelWidth;
        var viewportHeight = PixelHeight;
        var (width, height) = MeasureInner(viewportWidth, viewportHeight);

        if (resetScroll)
        {
            _offsetX = 0;
            _offsetY = 0;
        }

        _offsetX = Math.Clamp(_offsetX, 0, Math.Max(0, width - viewportWidth));
        _offsetY = Math.Clamp(_offsetY, 0, Math.Max(0, height - viewportHeight));

        _innerX = width < viewportWidth ? (viewportWidth - width) / 2 : -_offsetX;
        _innerY = height < viewportHeight ? (viewportHeight - height) / 2 : -_offsetY;

        var resized = width != _innerWidth || height != _innerHeight;
        _innerWidth = width;
        _innerHeight = height;
        _canScroll = _zoomEnabled && (width > viewportWidth || height > viewportHeight);

        if (_inner != IntPtr.Zero)
        {
            ShellPreviewHandler.PlaceWindow(_inner, _innerX, _innerY, width, height);

            if (resized)
            {
                // 크기 맞추기는 전용 갈래가 합니다. 여기서는 쪽지만 건넵니다.
                _worker.Resize(width, height);
            }
        }

        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private (int Width, int Height) MeasureInner(int viewportWidth, int viewportHeight)
    {
        if (_pageAspect is not { } aspect)
        {
            return (viewportWidth, viewportHeight);
        }

        var fit = _fit == HandlerFit.Auto
            ? (aspect > 1 ? HandlerFit.Width : HandlerFit.Height)
            : _fit;

        return fit switch
        {
            HandlerFit.Width => (viewportWidth, Math.Max(1, (int)Math.Round(viewportWidth / aspect))),
            HandlerFit.Height => (Math.Max(1, (int)Math.Round(viewportHeight * aspect)), viewportHeight),
            _ => Clamp((int)Math.Round(viewportWidth * _zoom), aspect)
        };
    }

    /// <summary>손 배율의 판 크기입니다. 핸들러가 감당할 수 있게 한 변 16000 픽셀에서 멈춥니다.</summary>
    private static (int Width, int Height) Clamp(int width, double aspect)
    {
        const int Largest = 16000;
        width = Math.Clamp(width, 16, Largest);
        var height = (int)Math.Round(width / aspect);

        if (height > Largest)
        {
            height = Largest;
            width = (int)Math.Round(height * aspect);
        }

        return (width, Math.Max(height, 16));
    }

    /// <summary>
    /// 휠 훅 갈래에서 불립니다. 먹을지만 빨리 정하고, 실제 일은 화면 갈래로 넘깁니다.
    /// Ctrl+휠은 확대·축소, 그냥 휠은 판이 칸보다 클 때만 우리가 굴립니다.
    /// 판이 칸에 다 들어오면 그냥 휠은 핸들러에게 보냅니다(여러 쪽 문서의 쪽 넘김은 핸들러 몫).
    /// </summary>
    private bool OnWheel(WheelInput input)
    {
        if (!_zoomEnabled)
        {
            return false;
        }

        if (input.Control)
        {
            Dispatcher.BeginInvoke(() => ZoomWheel?.Invoke(this, input));
            return true;
        }

        if (!_canScroll)
        {
            return false;
        }

        Dispatcher.BeginInvoke(() =>
        {
            var step = (int)Math.Round(WheelStep * DpiScale().Y * input.Delta / 120.0);

            if (input.Horizontal)
            {
                ScrollTo(_offsetX + step, _offsetY);
            }
            else
            {
                ScrollTo(_offsetX, _offsetY - step);
            }
        });

        return true;
    }

    /// <summary>전용 갈래에 쪽지를 건넵니다. 답은 기다리지 않고 나중에 받습니다.</summary>
    private async void Request(string filePath)
    {
        _requested = filePath;
        _patience.Stop();
        _patience.Start();

        Relayout(resetScroll: true);

        bool shown;

        try
        {
            shown = await _worker.ShowAsync(_inner, filePath, _innerWidth, _innerHeight).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"미리보기를 띄우지 못했습니다 {filePath}: {ex.GetType().Name} {ex.Message}");
            shown = false;
        }

        // 그사이 다른 파일로 넘어갔으면 이 답은 뜻이 없습니다.
        if (!string.Equals(_wanted, filePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _patience.Stop();
        Finished?.Invoke(this, shown);
    }

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        // 그림도 글자도 없는 빈 창 두 겹입니다. 핸들러는 안쪽 창을 채웁니다.
        _window = ShellPreviewHandler.CreateHostWindow(parent.Handle, PixelWidth, PixelHeight);
        _inner = _window == IntPtr.Zero
            ? IntPtr.Zero
            : ShellPreviewHandler.CreateHostWindow(_window, PixelWidth, PixelHeight);

        WheelInterceptor.Register(_window, OnWheel);
        Relayout(resetScroll: true);

        if (_wanted is { } path && _inner != IntPtr.Zero && HasRealSize)
        {
            Request(path);
        }

        return new HandleRef(this, _window);
    }

    protected override void DestroyWindowCore(HandleRef window)
    {
        _patience.Stop();
        WheelInterceptor.Unregister(_window);

        // 핸들러를 먼저 내리고 창을 없앱니다. 핸들러가 이 창에 붙어 있습니다.
        _worker.Dispose();

        ShellPreviewHandler.DestroyHostWindow(_inner);
        ShellPreviewHandler.DestroyHostWindow(_window);
        _inner = IntPtr.Zero;
        _window = IntPtr.Zero;
        _requested = null;
    }

    /// <summary>화면 배율이 다른 모니터로 옮겨 가면 픽셀 크기를 다시 계산합니다.</summary>
    protected override void OnDpiChanged(System.Windows.DpiScale oldDpi, System.Windows.DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Relayout(resetScroll: false);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);

        // 크기가 없어 미뤄 둔 파일이 있으면 이제 띄웁니다.
        if (_wanted is { } path
            && _inner != IntPtr.Zero
            && HasRealSize
            && !string.Equals(_requested, path, StringComparison.OrdinalIgnoreCase))
        {
            Request(path);
            return;
        }

        Relayout(resetScroll: false);
    }
}
