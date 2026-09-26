using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Explorer.Core.Services;

namespace Explorer.Shell.Preview;

/// <summary>
/// 미리보기 프로그램(엑셀·워드·한글 등을 만든 회사의 미리보기 핸들러)만 맡는 갈래입니다.
///
/// 그 프로그램을 띄우고 문서를 건네는 동안 기다려야 하는데,
/// 그 기다림을 화면 갈래가 하면 창 전체가 멈춥니다. 그래서 이 갈래가 대신 기다립니다.
/// 화면 갈래는 "이 파일 보여 줘", "크기 바꿔"라는 쪽지만 건네고 곧바로 돌아갑니다.
///
/// 이 갈래는 스스로 메시지를 돌리는 STA 갈래입니다. 미리보기 프로그램이 그렇게 돌기를 바랍니다.
/// 쪽지가 여러 장 밀리면 마지막 "보여 줘"만 처리합니다. 가운데 것은 띄워 봐야 곧 내릴 것입니다.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PreviewHandlerWorker : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ShellPreviewHandler _handler = new();

    /// <summary>"보여 줘"를 받을 때마다 하나씩 올립니다. 처리할 때 이 값이 바뀌었으면 뒤에 온 요청이 있다는 뜻입니다.</summary>
    private int _version;

    private bool _disposed;

    public PreviewHandlerWorker()
    {
        Dispatcher? dispatcher = null;

        using var ready = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();

            // 이 갈래의 메시지를 돌립니다. 끝내라는 쪽지가 올 때까지 여기 머뭅니다.
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "preview-handler"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();

        _dispatcher = dispatcher!;
    }

    /// <summary>
    /// 그 창 안에 그 파일의 미리보기를 띄웁니다.
    /// 뒤에 다른 요청이 오면 이것은 건너뛰고 false 로 끝납니다.
    /// </summary>
    public Task<bool> ShowAsync(IntPtr window, string path, int width, int height)
    {
        if (_disposed)
        {
            return Task.FromResult(false);
        }

        var version = Interlocked.Increment(ref _version);

        return _dispatcher.InvokeAsync(() =>
        {
            if (version != Volatile.Read(ref _version))
            {
                return false;
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var shown = _handler.Show(window, path, width, height);

            DiagnosticLog.Write(
                $"미리보기 프로그램 띄우기 {System.IO.Path.GetFileName(path)} {watch.ElapsedMilliseconds}ms"
                    + (shown ? string.Empty : " — 실패"));

            return shown;
        }).Task;
    }

    /// <summary>그릴 자리의 크기가 바뀌었다고 알립니다.</summary>
    public void Resize(int width, int height)
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.BeginInvoke(() => _handler.Resize(width, height));
    }

    /// <summary>보여 주던 것을 내립니다. 밀려 있던 "보여 줘"도 함께 버립니다.</summary>
    public void Close()
    {
        if (_disposed)
        {
            return;
        }

        Interlocked.Increment(ref _version);
        _dispatcher.BeginInvoke(() => _handler.Close());
    }

    /// <summary>
    /// 내리고 갈래를 끝냅니다.
    ///
    /// 그릴 자리의 창을 없애기 전에 불러야 합니다. 미리보기 프로그램이 그 창에 붙어 있기 때문입니다.
    /// 프로그램이 먹통이면 오래 기다리지 않고 넘어갑니다.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Increment(ref _version);

        try
        {
            _dispatcher.Invoke(
                () => _handler.Dispose(),
                DispatcherPriority.Send,
                CancellationToken.None,
                TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or InvalidOperationException)
        {
            DiagnosticLog.Write($"미리보기 프로그램을 제때 내리지 못했습니다: {ex.Message}");
        }

        _dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
    }
}
