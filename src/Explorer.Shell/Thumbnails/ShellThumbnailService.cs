using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Explorer.Core.Services;

namespace Explorer.Shell.Thumbnails;

/// <summary>
/// 썸네일 요청을 STA 작업자 스레드에서 처리합니다.
///
/// 셸 썸네일 핸들러 중에는 STA에서만 도는 것이 있어 스레드 풀(MTA)에서 부르면 실패합니다.
/// 요청마다 스레드를 만들면 스크롤할 때 스레드가 수백 개 생기므로,
/// 오래 사는 작업자 몇 개가 일감을 나눠 갖습니다.
///
/// 일감은 늦게 들어온 것부터 꺼냅니다.
/// 스크롤을 빨리 내리면 지나간 줄의 요청이 잔뜩 쌓이는데,
/// 먼저 들어온 것부터 처리하면 지금 화면에 보이는 줄이 그 뒤에서 하염없이 기다립니다.
/// 늦게 들어온 것이 곧 지금 보이는 줄입니다.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ShellThumbnailService : IDisposable
{
    /// <summary>이보다 오래 걸린 요청은 기록에 남깁니다. 느린 파일을 찾아내기 위한 것입니다.</summary>
    private const long SlowMilliseconds = 500;

    private readonly ConcurrentStack<Request> _pending = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();

    private int _queued;
    private int _done;
    private int _empty;
    private bool _disposed;

    public ShellThumbnailService(int concurrency = 4)
    {
        var workerCount = Math.Clamp(concurrency, 1, 8);

        for (var i = 0; i < workerCount; i++)
        {
            var worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"shell-thumbnail-{i}"
            };

            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }

        DiagnosticLog.Write($"썸네일 작업자 {workerCount}개 시작");
    }

    /// <summary>
    /// 썸네일을 비동기로 요청합니다. 취소된 요청은 꺼낼 때 건너뜁니다.
    /// 실패하거나 취소되면 null이 돌아옵니다.
    /// </summary>
    public Task<BitmapSource?> GetAsync(
        string path,
        int size,
        bool iconOnly,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<BitmapSource?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (_disposed || cancellationToken.IsCancellationRequested)
        {
            completion.TrySetResult(null);
            return completion.Task;
        }

        _pending.Push(new Request(path, size, iconOnly, cancellationToken, completion));
        Interlocked.Increment(ref _queued);
        _signal.Release();

        return completion.Task;
    }

    private void WorkerLoop()
    {
        while (true)
        {
            try
            {
                _signal.Wait(_shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!_pending.TryPop(out var request))
            {
                continue;
            }

            // 폴더를 벗어난 뒤 남아 있는 요청은 만들지 않고 버립니다.
            if (request.Cancellation.IsCancellationRequested)
            {
                request.Completion.TrySetResult(null);
                continue;
            }

            BitmapSource? image = null;
            var clock = Stopwatch.StartNew();

            try
            {
                image = ShellThumbnailProvider.TryGet(request.Path, request.Size, request.IconOnly);
            }
            catch (Exception ex)
            {
                // 썸네일 하나가 실패해도 작업자 스레드는 살아 있어야 합니다.
                DiagnosticLog.Write($"썸네일 예외 {request.Path}: {ex.GetType().Name} {ex.Message}");
            }

            clock.Stop();
            Report(request, image, clock.ElapsedMilliseconds);

            request.Completion.TrySetResult(image);
        }
    }

    private void Report(Request request, BitmapSource? image, long elapsed)
    {
        var done = Interlocked.Increment(ref _done);

        if (image is null)
        {
            Interlocked.Increment(ref _empty);
        }

        if (elapsed >= SlowMilliseconds)
        {
            DiagnosticLog.Write($"썸네일 느림 {elapsed}ms · {request.Size}px · {request.Path}");
        }

        // 오백 건마다 한 줄만 남깁니다. 줄마다 남기면 기록이 쓸모없어집니다.
        if (done % 500 == 0)
        {
            DiagnosticLog.Write(
                $"썸네일 진행 {done}/{Volatile.Read(ref _queued)} · 빈 결과 {Volatile.Read(ref _empty)}개 · 대기 {_pending.Count}개");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
    }

    private sealed record Request(
        string Path,
        int Size,
        bool IconOnly,
        CancellationToken Cancellation,
        TaskCompletionSource<BitmapSource?> Completion);
}
