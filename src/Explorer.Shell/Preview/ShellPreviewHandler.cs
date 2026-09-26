using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Explorer.Core.Services;

namespace Explorer.Shell.Preview;

/// <summary>
/// 윈도우 미리보기 핸들러 하나를 띄워 두고 갈아 끼웁니다.
///
/// 문서를 그리는 일은 만든 회사가 등록해 둔 핸들러가 합니다.
/// 우리는 그릴 자리를 내어 주고 어떤 파일인지 알려 주기만 합니다.
/// 핸들러는 되도록 다른 프로세스에서 돌게 부릅니다. 말썽을 부려도 우리 창이 죽지 않습니다.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ShellPreviewHandler : IDisposable
{
    // 만드는 자리입니다. CoCreateInstance 에 둘을 함께 주면 COM 은 같은 프로세스 쪽을 먼저 고릅니다.
    // 그래서 예전 코드는 "다른 프로세스 먼저"라고 적어 놓고 실제로는 우리 프로세스 안에서 핸들러를 돌렸습니다.
    // 탐색기는 핸들러를 따로 떼어 낸 대리 프로세스(prevhost.exe)에서 돌립니다. 같은 순서를 따르려고 하나씩 따로 시도합니다.
    private const int LocalServer = 0x4;
    private const int InProcess = 0x1;
    private const int Activate32Bit = 0x40000;

    private readonly PreviewHandlerFrame _frame = new();
    private IntPtr _stream;

    private object? _handler;
    private bool _disposed;

    /// <summary>지금 보여 주고 있는 파일입니다.</summary>
    public string? Path { get; private set; }

    /// <summary>그 확장자를 맡은 미리보기 핸들러가 있는지 봅니다.</summary>
    public static bool CanPreview(string extension) => PreviewHandlerNative.FindHandler(extension) is not null;

    /// <summary>
    /// 핸들러가 그릴 빈 자식 창을 만듭니다.
    /// 핸들러는 WPF가 아니라 옛 방식의 창에 그리므로 이런 자리가 있어야 합니다.
    /// </summary>
    public static IntPtr CreateHostWindow(IntPtr parent, int width, int height) =>
        PreviewHandlerNative.CreateWindowEx(
            0,
            "static",
            null,
            PreviewHandlerNative.ChildWindowStyle,
            0,
            0,
            Math.Max(width, 1),
            Math.Max(height, 1),
            parent,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

    /// <summary>자식 창을 부모 안의 그 자리·크기로 옮깁니다. 앞뒤 차례와 초점은 건드리지 않습니다.</summary>
    public static void PlaceWindow(IntPtr window, int x, int y, int width, int height)
    {
        if (window != IntPtr.Zero)
        {
            PreviewHandlerNative.SetWindowPos(window, IntPtr.Zero, x, y, Math.Max(width, 1), Math.Max(height, 1), 0x0004 | 0x0010);
        }
    }

    /// <summary>창의 왼쪽 위가 화면의 어디인지 픽셀로 알려 줍니다.</summary>
    public static bool TryGetScreenOrigin(IntPtr window, out int x, out int y)
    {
        x = 0;
        y = 0;

        if (window == IntPtr.Zero || !PreviewHandlerNative.GetWindowRect(window, out var rect))
        {
            return false;
        }

        x = rect.Left;
        y = rect.Top;
        return true;
    }

    /// <summary>만들어 둔 자식 창을 없앱니다.</summary>
    public static void DestroyHostWindow(IntPtr window)
    {
        if (window != IntPtr.Zero)
        {
            PreviewHandlerNative.DestroyWindow(window);
        }
    }

    /// <summary>주어진 창 안에 그 파일의 미리보기를 띄웁니다.</summary>
    public bool Show(IntPtr window, string filePath, int width, int height)
    {
        Close();

        if (window == IntPtr.Zero || string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        var extension = System.IO.Path.GetExtension(filePath);

        if (PreviewHandlerNative.FindHandler(extension) is not { } handlerId)
        {
            return false;
        }

        object? created = null;

        try
        {
            created = CreateHandler(handlerId, out var where);

            if (created is null)
            {
                return false;
            }

            var how = Initialize(created, filePath);

            if (how is null)
            {
                DiagnosticLog.Write($"미리보기 핸들러가 파일을 받는 방법을 하나도 지원하지 않습니다: {extension}");
                Release(created);
                return false;
            }

            if (created is not IPreviewHandler preview)
            {
                Release(created);
                ReleaseStream();
                return false;
            }

            // 탐색기처럼 틀을 먼저 건넵니다. 못 받는 핸들러도 있으니 실패는 넘어갑니다.
            if (created is IObjectWithSite withSite)
            {
                try
                {
                    withSite.SetSite(_frame);
                }
                catch (Exception ex) when (IsHandlerFailure(ex))
                {
                    DiagnosticLog.Write($"미리보기 핸들러가 틀을 받지 않았습니다: {ex.Message}");
                }
            }

            var bounds = new HandlerRect { Left = 0, Top = 0, Right = width, Bottom = height };
            preview.SetWindow(window, ref bounds);
            preview.DoPreview();

            // 문서를 다 읽은 뒤 자리를 한 번 더 알려 줍니다.
            // 읽는 도중에 받은 크기를 버리는 핸들러가 있어, 이것이 없으면 0 크기로 그리고 끝납니다.
            try
            {
                preview.SetRect(ref bounds);
            }
            catch (Exception ex) when (IsHandlerFailure(ex))
            {
                // 받지 않아도 이미 SetWindow 로 준 크기가 있습니다.
            }

            DiagnosticLog.Write($"미리보기 핸들러 {handlerId:B} {where}, {how}, {width}x{height}px");

            _handler = created;
            Path = filePath;
            return true;
        }
        catch (Exception ex) when (IsHandlerFailure(ex))
        {
            DiagnosticLog.Write($"미리보기 핸들러 실패 {filePath}: {ex.GetType().Name} {ex.Message}");

            if (created is not null && !ReferenceEquals(created, _handler))
            {
                Release(created);
            }

            ReleaseStream();
            Close();
            return false;
        }
    }

    /// <summary>
    /// 핸들러에게 파일을 건넵니다. 경로 → 흐름 → 셸 항목 순서로 해 봅니다.
    /// 어느 길로 건넸는지 돌려주고, 하나도 안 되면 null 입니다.
    /// </summary>
    private string? Initialize(object handler, string filePath)
    {
        if (handler is IInitializeWithFile withFile)
        {
            try
            {
                withFile.Initialize(filePath, PreviewHandlerNative.StorageRead);
                return "경로";
            }
            catch (Exception ex) when (IsHandlerFailure(ex))
            {
                DiagnosticLog.Write($"미리보기 핸들러가 경로를 받지 않았습니다: {ex.Message}");
            }
        }

        if (handler is IInitializeWithStream withStream)
        {
            var result = PreviewHandlerNative.SHCreateStreamOnFileEx(
                filePath, PreviewHandlerNative.StreamReadShared, 0, false, IntPtr.Zero, out var stream);

            if (result >= 0 && stream != IntPtr.Zero)
            {
                try
                {
                    withStream.Initialize(stream, PreviewHandlerNative.StorageRead);
                    _stream = stream;
                    return "흐름";
                }
                catch (Exception ex) when (IsHandlerFailure(ex))
                {
                    Marshal.Release(stream);
                    DiagnosticLog.Write($"미리보기 핸들러가 흐름을 받지 않았습니다: {ex.Message}");
                }
            }
        }

        if (handler is IInitializeWithItem withItem)
        {
            var itemId = PreviewHandlerNative.ShellItemId;
            var result = PreviewHandlerNative.SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref itemId, out var item);

            if (result >= 0 && item != IntPtr.Zero)
            {
                try
                {
                    withItem.Initialize(item, PreviewHandlerNative.StorageRead);
                    return "셸 항목";
                }
                catch (Exception ex) when (IsHandlerFailure(ex))
                {
                    DiagnosticLog.Write($"미리보기 핸들러가 셸 항목을 받지 않았습니다: {ex.Message}");
                }
                finally
                {
                    // 핸들러가 필요하면 스스로 붙들었습니다.
                    Marshal.Release(item);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 창 크기가 바뀌면 그릴 자리도 알려 줍니다.
    ///
    /// 핸들러가 이 요청을 거절하는 일이 흔합니다. 아직 그릴 준비가 안 됐거나 이미 내려갔을 때입니다.
    /// 화면 배치 중에 불리는 자리라, 여기서 예외가 새어 나가면 창이 통째로 죽습니다.
    /// </summary>
    public void Resize(int width, int height)
    {
        if (_handler is not IPreviewHandler preview || width <= 0 || height <= 0)
        {
            return;
        }

        try
        {
            var bounds = new HandlerRect { Left = 0, Top = 0, Right = width, Bottom = height };
            preview.SetRect(ref bounds);
        }
        catch (Exception ex) when (IsHandlerFailure(ex))
        {
            // 핸들러가 받지 않았습니다. 그림이 한 박자 늦게 맞춰질 뿐입니다.
        }
    }

    /// <summary>보여 주던 것을 내립니다.</summary>
    public void Close()
    {
        if (_handler is null)
        {
            return;
        }

        try
        {
            if (_handler is IPreviewHandler preview)
            {
                preview.Unload();
            }

            if (_handler is IObjectWithSite withSite)
            {
                withSite.SetSite(null);
            }
        }
        catch (Exception ex) when (IsHandlerFailure(ex))
        {
            // 이미 내려갔습니다.
        }

        try
        {
            Marshal.ReleaseComObject(_handler);
        }
        catch (Exception ex) when (IsHandlerFailure(ex))
        {
            // COM 개체가 아니었거나 이미 놓인 경우입니다.
        }

        _handler = null;
        Path = null;
        ReleaseStream();
    }

    private void ReleaseStream()
    {
        if (_stream != IntPtr.Zero)
        {
            Marshal.Release(_stream);
            _stream = IntPtr.Zero;
        }
    }

    private static void Release(object handler)
    {
        try
        {
            Marshal.ReleaseComObject(handler);
        }
        catch (Exception ex) when (IsHandlerFailure(ex))
        {
            // 이미 놓였습니다.
        }
    }

    /// <summary>
    /// 핸들러를 만듭니다. 탐색기처럼 대리 프로세스(다른 프로세스)를 먼저, 안 되면 우리 프로세스 안에서 만듭니다.
    /// 32비트로만 깔린 핸들러는 32비트 대리 프로세스로 한 번 더 시도합니다.
    /// </summary>
    private static object? CreateHandler(Guid handlerId, out string where)
    {
        (int Context, string Name)[] tries =
        {
            (LocalServer, "다른 프로세스"),
            (InProcess, "같은 프로세스"),
            (LocalServer | Activate32Bit, "32비트 다른 프로세스")
        };

        var results = new System.Text.StringBuilder();

        foreach (var (context, name) in tries)
        {
            var result = NativeCreate(ref handlerId, IntPtr.Zero, context, ref UnknownInterfaceId, out var pointer);

            if (result >= 0 && pointer != IntPtr.Zero)
            {
                var handler = Marshal.GetObjectForIUnknown(pointer);
                Marshal.Release(pointer);
                where = name;
                return handler;
            }

            results.Append($" {name} 0x{result:X8}");
        }

        DiagnosticLog.Write($"미리보기 핸들러를 만들지 못했습니다 {handlerId:B}:{results}");
        where = string.Empty;
        return null;
    }

    /// <summary>
    /// 핸들러가 말을 듣지 않을 때 나오는 예외들입니다.
    /// COM 쪽 실패는 ArgumentException 으로도 올라옵니다. E_INVALIDARG 가 그렇게 옮겨집니다.
    /// </summary>
    private static bool IsHandlerFailure(Exception ex) =>
        ex is COMException
            or InvalidCastException
            or ArgumentException
            or NotSupportedException
            or UnauthorizedAccessException
            or ExternalException
            or System.IO.FileNotFoundException
            or System.IO.IOException;

    private static Guid UnknownInterfaceId = new("00000000-0000-0000-C000-000000000046");

    [DllImport("ole32.dll", EntryPoint = "CoCreateInstance")]
    private static extern int NativeCreate(
        ref Guid classId,
        IntPtr outer,
        int context,
        ref Guid interfaceId,
        out IntPtr instance);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Close();
    }
}
