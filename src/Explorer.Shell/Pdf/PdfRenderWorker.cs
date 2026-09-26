using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Explorer.Core.Services;

namespace Explorer.Shell.Pdf;

/// <summary>
/// PDF 쪽을 그림으로 그립니다. 윈도우에 들어 있는 PDF 부품(Windows.Data.Pdf)을 씁니다.
///
/// 이 부품을 C# 에서 부르는 보통 길은 윈도우 SDK 연결 꾸러미를 받아 프로젝트 대상을 바꾸는 것인데,
/// 그 꾸러미는 .NET 판이 오를 때마다 새로 받아야 할 수 있습니다. 그래서 예전 미리보기(PdfRenderer, 지금은 휴지통)처럼
/// 꾸러미 없이 부품의 함수 표를 손으로 짚어 부릅니다.
///
/// 모든 일은 오래 사는 작업 스레드 하나에서 차례로 합니다. 부품의 비동기 작업을 짧게 쉬어 가며 기다리므로
/// 화면 스레드에서 돌리면 안 되고, 열어 둔 문서 포인터를 한 스레드에서만 만지게 하려는 뜻도 있습니다.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PdfRenderWorker : IDisposable
{
    // ── 이름표(IID). windows.data.pdf.h, windows.storage.streams.h 에 적힌 값입니다. ──

    private static Guid _randomAccessStreamId = new("905A0FE1-BC53-11DF-8C49-001E4FC686DA");
    private static Guid _documentStaticsId = new("433A0B5F-C007-4788-90F2-08143D922599");
    private static Guid _renderOptionsId = new("3C98056F-B7CF-4C29-9A04-52D90267F425");
    private static Guid _asyncInfoId = new("00000036-0000-0000-C000-000000000046");

    // ── 함수 표의 칸 번호. 0~2 는 IUnknown, 3~5 는 IInspectable 입니다. ──

    /// <summary>IPdfDocumentStatics: LoadFromFileAsync(6), LoadFromFileWithPasswordAsync(7), LoadFromStreamAsync(8)</summary>
    private const int LoadFromStreamSlot = 8;

    /// <summary>IPdfDocument: GetPage(6), get_PageCount(7), get_IsPasswordProtected(8)</summary>
    private const int GetPageSlot = 6;

    private const int PageCountSlot = 7;

    /// <summary>IPdfPage: RenderToStreamAsync(6), RenderWithOptionsToStreamAsync(7), PreparePageAsync(8), get_Index(9), get_Size(10)</summary>
    private const int RenderToStreamSlot = 6;

    private const int RenderWithOptionsSlot = 7;

    private const int PageSizeSlot = 10;

    /// <summary>IPdfPageRenderOptions: get/put_SourceRect(6,7), get/put_DestinationWidth(8,9), get/put_DestinationHeight(10,11)</summary>
    private const int PutDestinationWidthSlot = 9;

    private const int PutDestinationHeightSlot = 11;

    /// <summary>IAsyncOperation·IAsyncAction: put_Completed(6), get_Completed(7), GetResults(8)</summary>
    private const int GetResultsSlot = 8;

    /// <summary>IAsyncInfo: get_Id(6), get_Status(7), get_ErrorCode(8), Cancel(9), Close(10)</summary>
    private const int StatusSlot = 7;

    private const int ErrorCodeSlot = 8;

    private const int CancelSlot = 9;

    /// <summary>한 단계를 기다리는 최대 시간입니다.</summary>
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinRtSize
    {
        public float Width;
        public float Height;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int WithStreamReturningPointer(IntPtr self, IntPtr stream, out IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int WithTwoPointersReturningPointer(IntPtr self, IntPtr first, IntPtr second, out IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReturningPointer(IntPtr self, out IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReturningNothing(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReturningNumber(IntPtr self, out int value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReturningSize(IntPtr self, out WinRtSize value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int TakingNumber(IntPtr self, uint value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int WithIndexReturningPointer(IntPtr self, uint index, out IntPtr result);

    private sealed class OpenDocument
    {
        public IntPtr Stream;
        public IntPtr Document;
        public int PageCount;
    }

    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly Dictionary<string, OpenDocument> _documents = new(StringComparer.OrdinalIgnoreCase);
    private bool _optionsUnavailable;

    public PdfRenderWorker()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "PdfRender" };
        _thread.Start();
    }

    // ── 바깥에서 부르는 일 ──

    /// <summary>쪽수입니다. 열 수 없으면(암호, 깨진 파일) 예외가 납니다.</summary>
    public Task<int> PageCountAsync(string path) => Enqueue(() => Open(path).PageCount);

    /// <summary>쪽 크기입니다. 1/96 인치 단위이고, 쪽의 회전 값을 반영한 모양입니다.</summary>
    public Task<(double Width, double Height)> PageSizeAsync(string path, int index) =>
        Enqueue(() => WithPage(path, index, page =>
        {
            var getSize = Slot<ReturningSize>(page, PageSizeSlot);
            Check(getSize(page, out var size), "쪽 크기 읽기");
            return ((double)size.Width, (double)size.Height);
        }));

    /// <summary>
    /// 쪽을 가로 <paramref name="widthPixels"/> 픽셀로 그립니다. 세로는 쪽 비율대로 정해집니다.
    /// 크기를 지정하는 길이 막힌 윈도우라면 기본 크기(96 DPI)로 그립니다.
    /// </summary>
    public Task<BitmapSource> RenderAsync(string path, int index, int widthPixels, CancellationToken cancellation = default) =>
        Enqueue(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            return WithPage(path, index, page => RenderPage(page, widthPixels, cancellation));
        });

    /// <summary>열어 둔 문서를 닫습니다. 그 파일을 다른 프로그램이 쓸 수 있게 됩니다.</summary>
    public Task CloseAsync(string path) => Enqueue(() =>
    {
        if (_documents.Remove(path, out var document))
        {
            Release(document);
        }

        return true;
    });

    public void Dispose()
    {
        if (_queue.IsAddingCompleted)
        {
            return;
        }

        _queue.Add(() =>
        {
            foreach (var document in _documents.Values)
            {
                Release(document);
            }

            _documents.Clear();
        });

        _queue.CompleteAdding();
    }

    // ── 작업 스레드 ──

    private void Run()
    {
        // 이 스레드가 WinRT 부품을 쓸 수 있게 합니다 (1 = 여러 스레드 아파트).
        _ = RoInitialize(1);

        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                // 일마다 자기 Task 로 예외를 넘기므로 여기까지 오는 것은 없어야 합니다.
                DiagnosticLog.Write($"PDF 작업 스레드: {ex.Message}");
            }
        }
    }

    private Task<T> Enqueue<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            _queue.Add(() =>
            {
                try
                {
                    completion.SetResult(work());
                }
                catch (OperationCanceledException)
                {
                    completion.SetCanceled();
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });
        }
        catch (InvalidOperationException)
        {
            completion.SetException(new ObjectDisposedException(nameof(PdfRenderWorker)));
        }

        return completion.Task;
    }

    private OpenDocument Open(string path)
    {
        if (_documents.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var document = new OpenDocument();

        try
        {
            Check(CreateRandomAccessStreamOnFile(path, 0, ref _randomAccessStreamId, out document.Stream), "파일 열기");

            var statics = DocumentStatics();

            try
            {
                var load = Slot<WithStreamReturningPointer>(statics, LoadFromStreamSlot);
                Check(load(statics, document.Stream, out var loading), "문서 읽기 시작");

                try
                {
                    Wait(loading, CancellationToken.None);
                    Check(Slot<ReturningPointer>(loading, GetResultsSlot)(loading, out document.Document), "문서 받기");
                }
                finally
                {
                    Marshal.Release(loading);
                }
            }
            finally
            {
                Marshal.Release(statics);
            }

            Check(Slot<ReturningNumber>(document.Document, PageCountSlot)(document.Document, out document.PageCount), "쪽수 읽기");
        }
        catch
        {
            Release(document);
            throw;
        }

        _documents[path] = document;
        return document;
    }

    private T WithPage<T>(string path, int index, Func<IntPtr, T> work)
    {
        var document = Open(path);

        if (index < 0 || index >= document.PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), $"{index + 1}쪽은 없습니다 (모두 {document.PageCount}쪽).");
        }

        var getPage = Slot<WithIndexReturningPointer>(document.Document, GetPageSlot);
        Check(getPage(document.Document, (uint)index, out var page), "쪽 꺼내기");

        try
        {
            return work(page);
        }
        finally
        {
            Marshal.Release(page);
        }
    }

    private BitmapSource RenderPage(IntPtr page, int widthPixels, CancellationToken cancellation)
    {
        IntPtr memory = IntPtr.Zero;
        IntPtr output = IntPtr.Zero;
        IntPtr options = IntPtr.Zero;

        try
        {
            Check(CreateStreamOnHGlobal(IntPtr.Zero, true, out memory), "그릇 만들기");
            Check(CreateRandomAccessStreamOverStream(memory, 0, ref _randomAccessStreamId, out output), "그릇 감싸기");

            IntPtr rendering;
            options = widthPixels > 0 ? CreateOptions(page, widthPixels) : IntPtr.Zero;

            if (options != IntPtr.Zero)
            {
                var render = Slot<WithTwoPointersReturningPointer>(page, RenderWithOptionsSlot);
                Check(render(page, output, options, out rendering), "쪽 그리기 시작");
            }
            else
            {
                var render = Slot<WithStreamReturningPointer>(page, RenderToStreamSlot);
                Check(render(page, output, out rendering), "쪽 그리기 시작");
            }

            try
            {
                Wait(rendering, cancellation);
                Check(Slot<ReturningNothing>(rendering, GetResultsSlot)(rendering), "쪽 그리기 마무리");
            }
            finally
            {
                Marshal.Release(rendering);
            }

            var bytes = ReadAll(memory);

            if (bytes.Length == 0)
            {
                throw new InvalidOperationException("쪽을 그렸는데 받은 것이 비어 있습니다.");
            }

            using var stream = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgr24, null, 0);
            var copy = new WriteableBitmap(frame);
            copy.Freeze();
            return copy;
        }
        finally
        {
            ReleaseIfSet(options);
            ReleaseIfSet(output);
            ReleaseIfSet(memory);
        }
    }

    /// <summary>
    /// 그릴 크기를 담은 설정 개체를 만듭니다. 이 길이 막히면 0 을 돌려주고, 다음부터는 시도하지 않습니다.
    /// </summary>
    private IntPtr CreateOptions(IntPtr page, int widthPixels)
    {
        if (_optionsUnavailable)
        {
            return IntPtr.Zero;
        }

        IntPtr instance = IntPtr.Zero;
        IntPtr name = IntPtr.Zero;

        try
        {
            var getSize = Slot<ReturningSize>(page, PageSizeSlot);
            Check(getSize(page, out var size), "쪽 크기 읽기");

            var heightPixels = size.Width > 0
                ? Math.Max(1, (int)Math.Round(widthPixels * (double)size.Height / size.Width))
                : widthPixels;

            const string className = "Windows.Data.Pdf.PdfPageRenderOptions";
            Check(WindowsCreateString(className, className.Length, out name), "이름 만들기");
            Check(RoActivateInstance(name, out instance), "그리기 설정 만들기");
            Check(Marshal.QueryInterface(instance, in _renderOptionsId, out var options), "그리기 설정 창구");

            Check(Slot<TakingNumber>(options, PutDestinationWidthSlot)(options, (uint)widthPixels), "가로 크기");
            Check(Slot<TakingNumber>(options, PutDestinationHeightSlot)(options, (uint)heightPixels), "세로 크기");

            return options;
        }
        catch (Exception ex) when (ex is COMException or EntryPointNotFoundException or DllNotFoundException or InvalidCastException)
        {
            _optionsUnavailable = true;
            DiagnosticLog.Write($"PDF 그리기 크기를 정하지 못해 기본 크기로 그립니다: {ex.Message}");
            return IntPtr.Zero;
        }
        finally
        {
            ReleaseIfSet(instance);

            if (name != IntPtr.Zero)
            {
                WindowsDeleteString(name);
            }
        }
    }

    // ── 도우미 ──

    private static IntPtr DocumentStatics()
    {
        const string className = "Windows.Data.Pdf.PdfDocument";

        Check(WindowsCreateString(className, className.Length, out var name), "이름 만들기");

        try
        {
            Check(RoGetActivationFactory(name, ref _documentStaticsId, out var statics), "PDF 부품 찾기");
            return statics;
        }
        finally
        {
            WindowsDeleteString(name);
        }
    }

    /// <summary>비동기 작업이 끝나기를 짧게 쉬어 가며 기다립니다. 이 스레드는 화면 스레드가 아닙니다.</summary>
    private static void Wait(IntPtr operation, CancellationToken cancellation)
    {
        Check(Marshal.QueryInterface(operation, in _asyncInfoId, out var info), "작업 상태 창구");

        try
        {
            var status = Slot<ReturningNumber>(info, StatusSlot);
            var started = System.Diagnostics.Stopwatch.StartNew();

            while (true)
            {
                Check(status(info, out var state), "작업 상태 읽기");

                switch (state)
                {
                    case 1:
                        return;
                    case 2:
                        throw new OperationCanceledException();
                    case 3:
                        Slot<ReturningNumber>(info, ErrorCodeSlot)(info, out var code);
                        throw new COMException(Describe(code), code);
                }

                if (cancellation.IsCancellationRequested || started.Elapsed > StepTimeout)
                {
                    Slot<ReturningNothing>(info, CancelSlot)(info);

                    if (cancellation.IsCancellationRequested)
                    {
                        throw new OperationCanceledException();
                    }

                    throw new TimeoutException("PDF 부품이 제때 답하지 않았습니다.");
                }

                Thread.Sleep(3);
            }
        }
        finally
        {
            Marshal.Release(info);
        }
    }

    /// <summary>자주 보는 실패를 사람이 읽을 말로 바꿉니다.</summary>
    private static string Describe(int code) => unchecked((uint)code) switch
    {
        0x80070005 => "파일에 접근할 수 없습니다.",
        0x8007052B or 0x80004005 => "PDF 를 열지 못했습니다. 암호가 걸렸거나 깨진 파일일 수 있습니다.",
        _ => $"PDF 부품이 실패했습니다 0x{code:X8}"
    };

    private static byte[] ReadAll(IntPtr memory)
    {
        var stream = (System.Runtime.InteropServices.ComTypes.IStream)Marshal.GetObjectForIUnknown(memory);

        try
        {
            stream.Stat(out var stat, 1);
            var size = (int)Math.Min(stat.cbSize, int.MaxValue);

            if (size <= 0)
            {
                return Array.Empty<byte>();
            }

            stream.Seek(0, 0, IntPtr.Zero);

            var bytes = new byte[size];
            stream.Read(bytes, size, IntPtr.Zero);
            return bytes;
        }
        finally
        {
            Marshal.ReleaseComObject(stream);
        }
    }

    private static T Slot<T>(IntPtr instance, int slot) where T : Delegate
    {
        var table = Marshal.ReadIntPtr(instance);
        var function = Marshal.ReadIntPtr(table, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(function);
    }

    private static void Check(int result, string step)
    {
        if (result < 0)
        {
            throw new COMException($"{step} 실패: {Describe(result)}", result);
        }
    }

    private static void Release(OpenDocument document)
    {
        ReleaseIfSet(document.Document);
        ReleaseIfSet(document.Stream);
        document.Document = IntPtr.Zero;
        document.Stream = IntPtr.Zero;
    }

    private static void ReleaseIfSet(IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
        {
            Marshal.Release(pointer);
        }
    }

    [DllImport("combase.dll")]
    private static extern int RoInitialize(int initType);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr text);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr text);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr classId, ref Guid interfaceId, out IntPtr factory);

    [DllImport("combase.dll")]
    private static extern int RoActivateInstance(IntPtr classId, out IntPtr instance);

    [DllImport("shcore.dll", CharSet = CharSet.Unicode)]
    private static extern int CreateRandomAccessStreamOnFile(string filePath, uint accessMode, ref Guid interfaceId, out IntPtr stream);

    [DllImport("shcore.dll")]
    private static extern int CreateRandomAccessStreamOverStream(IntPtr stream, uint options, ref Guid interfaceId, out IntPtr result);

    [DllImport("ole32.dll")]
    private static extern int CreateStreamOnHGlobal(IntPtr global, [MarshalAs(UnmanagedType.Bool)] bool deleteOnRelease, out IntPtr stream);
}
