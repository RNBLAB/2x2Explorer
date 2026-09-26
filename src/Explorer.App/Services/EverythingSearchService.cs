using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Explorer.Core.Models;
using Explorer.Core.Services;
using Explorer.Shell.Search;

namespace Explorer.App.Services;

/// <summary>
/// Everything 검색을 창에 붙여 둡니다.
///
/// 요청을 보내면 답이 창 메시지로 되돌아오므로, 창 하나가 그 메시지를 듣고 있어야 합니다.
/// 네 패널이 이 하나를 함께 쓰고, 한 번에 한 건만 처리합니다.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EverythingSearchService
{
    /// <summary>우리 답신임을 가리는 표식입니다. 아무 값이나 되지만 남과 겹치지 않으면 됩니다.</summary>
    private const int ReplyKind = 0x45585031;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();

    private HwndSource? _source;
    private TaskCompletionSource<IReadOnlyList<EverythingHit>>? _pending;

    /// <summary>Everything이 떠 있고 창도 준비됐는지 알려 줍니다.</summary>
    public bool IsAvailable => _source is not null && EverythingIpc.IsRunning;

    /// <summary>주 창에 메시지를 듣는 귀를 답니다. 창이 만들어진 뒤에 불러야 합니다.</summary>
    public void Attach(Window window)
    {
        if (_source is not null)
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;

        if (handle == IntPtr.Zero)
        {
            DiagnosticLog.Write("Everything: 창 손잡이가 아직 없습니다.");
            return;
        }

        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(OnWindowMessage);

        DiagnosticLog.Write($"Everything 준비 {(EverythingIpc.IsRunning ? "완료" : "— 프로그램이 꺼져 있음")}");
    }

    /// <summary>검색어를 보내고 결과를 기다립니다. 실패하면 빈 목록이 돌아옵니다.</summary>
    /// <param name="includeLocal">로컬(네트워크도 클라우드도 아닌 곳)의 결과도 담을지입니다.</param>
    /// <param name="includeNetwork">네트워크 드라이브의 결과도 담을지입니다.</param>
    /// <param name="includeCloud">클라우드(가상 드라이브·동기화 폴더)의 결과도 담을지입니다.</param>
    /// <param name="cloudLabels">클라우드로 볼 드라이브의 볼륨 레이블입니다.</param>
    /// <param name="chunk">한 번에 크기·날짜를 읽을 건수입니다. 한 묶음이 끝날 때마다 중간 결과를 올립니다.</param>
    /// <param name="progress">읽는 대로 올라오는 중간 결과입니다. 화면이 먼저 보이라고 둡니다.</param>
    public async Task<IReadOnlyList<FileSystemEntry>> SearchAsync(
        string query,
        int maxResults,
        bool includeLocal,
        bool includeNetwork,
        bool includeCloud,
        IReadOnlyCollection<string>? cloudLabels,
        int chunk,
        IProgress<IReadOnlyList<FileSystemEntry>>? progress,
        CancellationToken cancellationToken)
    {
        if (_source is null || string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<FileSystemEntry>();
        }

        var waiting = new TaskCompletionSource<IReadOnlyList<EverythingHit>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            // 앞선 검색이 아직 답을 기다리고 있으면 버립니다. 마지막 검색어만 뜻이 있습니다.
            _pending?.TrySetResult(Array.Empty<EverythingHit>());
            _pending = waiting;
        }

        if (!EverythingIpc.Send(_source.Handle, ReplyKind, query, maxResults))
        {
            lock (_gate)
            {
                _pending = null;
            }

            return Array.Empty<FileSystemEntry>();
        }

        IReadOnlyList<EverythingHit> hits;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            hits = await waiting.Task.WaitAsync(Patience, cancellationToken).ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
            DiagnosticLog.Write("Everything이 제때 답하지 않았습니다.");
            return Array.Empty<FileSystemEntry>();
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<FileSystemEntry>();
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pending, waiting))
                {
                    _pending = null;
                }
            }
        }

        var answered = watch.ElapsedMilliseconds;
        var kept = hits.Count;

        // 크기와 날짜는 Everything이 주지 않으므로 여기서 한 번씩 읽습니다.
        // 건수가 많을 수 있어 화면을 붙들지 않도록 다른 갈래에서 합니다.
        // 네트워크·클라우드 거르기도 드라이브 레이블을 읽느라 디스크를 만지므로 같은 갈래에서 합니다.
        var described = await Task
            .Run(
                () =>
                {
                    var wanted = includeLocal && includeNetwork && includeCloud
                        ? hits
                        : Narrow(hits, includeLocal, includeNetwork, includeCloud, cloudLabels);

                    kept = wanted.Count;
                    return Describe(wanted, chunk, progress, cancellationToken);
                },
                cancellationToken)
            .ConfigureAwait(true);

        DiagnosticLog.Write(
            $"Everything 답 {hits.Count}건({kept}건 읽음) {answered}ms,"
                + $" 크기·날짜 읽기 {watch.ElapsedMilliseconds - answered}ms");

        return described;
    }

    /// <summary>
    /// Everything 은 이름과 경로만 줍니다. 크기·날짜·속성은 파일마다 한 번씩 읽어야 합니다.
    ///
    /// 이 읽기가 검색 시간의 거의 전부입니다.
    /// 네트워크 드라이브의 파일은 한 건에 2ms 안팎이 걸려, 칠천 건이면 십여 초가 됩니다.
    /// 기다리는 시간이 대부분이므로 여러 갈래로 나누어 동시에 묻고,
    /// 정해진 건수만큼 읽을 때마다 중간 결과를 올려 화면이 먼저 차도록 합니다.
    /// </summary>
    private static IReadOnlyList<FileSystemEntry> Describe(
        IReadOnlyList<EverythingHit> hits,
        int chunk,
        IProgress<IReadOnlyList<FileSystemEntry>>? progress,
        CancellationToken cancellationToken)
    {
        if (hits.Count == 0)
        {
            return Array.Empty<FileSystemEntry>();
        }

        var step = chunk is > 0 and < 200_000 ? chunk : 5000;
        var entries = new List<FileSystemEntry>(Math.Min(hits.Count, 100_000));

        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,

            // 갈래를 더 늘려도 디스크가 아니라 응답을 기다리는 시간이라 크게 나아지지 않습니다.
            MaxDegreeOfParallelism = 16
        };

        for (var start = 0; start < hits.Count; start += step)
        {
            var count = Math.Min(step, hits.Count - start);
            var slots = new FileSystemEntry?[count];

            try
            {
                // 차례는 그대로 두려고 자리마다 칸을 미리 잡아 두고 제자리에 넣습니다.
                Parallel.For(0, count, options, index => slots[index] = Describe(hits[start + index]));
            }
            catch (OperationCanceledException)
            {
                // 다른 검색어로 넘어갔습니다. 여태 읽은 것만 추려 돌려줍니다.
                return entries;
            }

            foreach (var entry in slots)
            {
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }

            // 마지막 묶음은 부르는 쪽이 결과를 그대로 받으므로 따로 올리지 않습니다.
            if (start + count < hits.Count)
            {
                progress?.Report(entries.ToArray());
            }
        }

        return entries;
    }

    /// <summary>
    /// 끈 쪽에 있는 것을 걸러 냅니다. 모든 경로는 클라우드·네트워크·로컬 가운데 하나로만 갑니다.
    /// 클라우드를 먼저, 그다음 네트워크를 보고, 둘 다 아니면 로컬입니다.
    /// 네트워크이면서 클라우드인 경로는 클라우드 단추만 따릅니다.
    /// </summary>
    private static IReadOnlyList<EverythingHit> Narrow(
        IReadOnlyList<EverythingHit> hits,
        bool includeLocal,
        bool includeNetwork,
        bool includeCloud,
        IReadOnlyCollection<string>? cloudLabels)
    {
        var places = SearchLocations.Read(cloudLabels);
        var kept = new List<EverythingHit>(hits.Count);

        foreach (var hit in hits)
        {
            var keep = places.IsCloud(hit.FullPath)
                ? includeCloud
                : places.IsNetwork(hit.FullPath)
                    ? includeNetwork
                    : includeLocal;

            if (keep)
            {
                kept.Add(hit);
            }
        }

        return kept;
    }

    /// <summary>한 건을 읽습니다. 사라졌거나 손댈 수 없으면 null 입니다.</summary>
    private static FileSystemEntry? Describe(EverythingHit hit)
    {
        try
        {
            FileSystemInfo info = hit.IsDirectory
                ? new DirectoryInfo(hit.FullPath)
                : new FileInfo(hit.FullPath);

            return info.Exists ? FileSystemEntry.FromInfo(info) : null;
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or ArgumentException
                                       or NotSupportedException)
        {
            // 지워졌거나 손댈 수 없는 항목입니다. 목록에서 빼고 넘어갑니다.
            return null;
        }
    }

    private IntPtr OnWindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!EverythingIpc.TryReadReply(message, lParam, ReplyKind, out var hits))
        {
            return IntPtr.Zero;
        }

        handled = true;

        lock (_gate)
        {
            _pending?.TrySetResult(hits);
            _pending = null;
        }

        // 받았다는 뜻으로 1을 돌려줍니다.
        return new IntPtr(1);
    }
}
