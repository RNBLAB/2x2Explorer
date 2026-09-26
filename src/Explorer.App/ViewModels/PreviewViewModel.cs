using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using Explorer.App.Services;
using Explorer.Core.Models;
using Explorer.Core.Services;
using Explorer.Shell.Preview;
using Explorer.Core.Settings;
using Explorer.Shell.Text;
using Explorer.Shell.Thumbnails;

namespace Explorer.App.ViewModels;

/// <summary>
/// 미리보기 패널입니다. 활성 패널에서 고른 파일을 받아 형식에 맞게 보여 줍니다.
///
/// 실제로 펼쳐 보는 것은 이미지와 텍스트입니다. 코드는 색을 입혀 보여 줍니다.
/// PDF·오피스·한글은 만든 회사가 등록해 둔 미리보기 핸들러에 맡깁니다.
/// 동영상·음악은 여기서 틀지 않고 윈도우가 만든 장면 한 장만 보여 줍니다.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PreviewViewModel : ObservableObject, IDisposable
{
    private const int MaxImageDecodeWidth = 1600;
    // 패널 가득 채워 보여 주므로 작게 받으면 뭉갭니다. 윈도우는 이 크기까지 만들어 줍니다.
    private const int UnsupportedThumbnailSize = 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".ico", ".jfif", ".wdp", ".dib"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".log", ".json", ".xml", ".csv", ".tsv", ".ini", ".cfg", ".conf",
        ".yml", ".yaml", ".html", ".htm", ".css", ".js", ".ts", ".cs", ".c", ".h", ".cpp",
        ".hpp", ".java", ".py", ".rb", ".go", ".rs", ".php", ".sql", ".sh", ".bat", ".cmd",
        ".ps1", ".xaml", ".csproj", ".sln", ".props", ".targets", ".editorconfig", ".gitignore"
    };

    /// <summary>
    /// 동영상과 음악입니다.
    /// 윈도우에 재생기 미리보기 핸들러가 깔려 있으면 여기서 소리가 나 버립니다.
    /// 미리보기는 눈으로 훑는 자리라 그러면 곤란해서, 장면 한 장만 받아 보여 줍니다.
    /// </summary>
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".mpg", ".mpeg",
        ".ts", ".m2ts", ".3gp", ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".wma"
    };

    private readonly PreviewSettings _settings;
    private readonly ThumbnailCache _thumbnails;
    private readonly DispatcherTimer _timer;

    private FileItemViewModel? _pending;
    private CancellationTokenSource? _load;
    private string _loadedPath = string.Empty;

    private string _fileName = string.Empty;
    private string _details = string.Empty;
    private string _extraInfo = string.Empty;
    private string _message = "파일을 고르면 여기에 보여 줍니다.";
    private ImageSource? _image;
    private string _text = string.Empty;
    private IReadOnlyList<CodeSpan> _codeSpans = Array.Empty<CodeSpan>();
    private bool _showImage;
    private bool _showText;
    private bool _showCode;
    private bool _showMessage = true;
    private bool _disposed;

    public PreviewViewModel(PreviewSettings settings, ThumbnailCache thumbnails)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _thumbnails = thumbnails ?? throw new ArgumentNullException(nameof(thumbnails));

        // 방향키로 목록을 훑을 때 파일을 매번 여는 것을 막는 장치입니다.
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(settings.DebounceMs, 0))
        };
        _timer.Tick += OnTimerTick;
    }

    public string FileName
    {
        get => _fileName;
        private set => SetProperty(ref _fileName, value);
    }

    /// <summary>크기와 수정한 날짜입니다.</summary>
    public string Details
    {
        get => _details;
        private set => SetProperty(ref _details, value);
    }

    /// <summary>이미지면 해상도, 텍스트면 인코딩입니다.</summary>
    public string ExtraInfo
    {
        get => _extraInfo;
        private set => SetProperty(ref _extraInfo, value);
    }

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public ImageSource? Image
    {
        get => _image;
        private set => SetProperty(ref _image, value);
    }

    public string Text
    {
        get => _text;
        private set => SetProperty(ref _text, value);
    }

    public bool ShowImage
    {
        get => _showImage;
        private set => SetProperty(ref _showImage, value);
    }

    /// <summary>코드에 색을 입힐 구간입니다. 칠하지 않는 글이면 비어 있습니다.</summary>
    public IReadOnlyList<CodeSpan> CodeSpans
    {
        get => _codeSpans;
        private set => SetProperty(ref _codeSpans, value);
    }

    /// <summary>색을 입힌 코드로 보여 주는 중인지입니다.</summary>
    public bool ShowCode
    {
        get => _showCode;
        private set => SetProperty(ref _showCode, value);
    }

    public bool ShowText
    {
        get => _showText;
        private set => SetProperty(ref _showText, value);
    }

    /// <summary>윈도우 미리보기 핸들러가 그리는 칸을 보일지 여부입니다.</summary>
    public bool ShowHandler
    {
        get => _showHandler;
        private set => SetProperty(ref _showHandler, value);
    }

    /// <summary>핸들러에게 보여 달라고 할 파일입니다.</summary>
    public string HandlerPath
    {
        get => _handlerPath;
        private set => SetProperty(ref _handlerPath, value);
    }

    /// <summary>
    /// 핸들러가 그리는 문서의 쪽 가로÷세로입니다. 알면 칸이 확대·맞춤을 맡습니다(한글·PDF).
    /// </summary>
    public double? HandlerPageAspect
    {
        get => _handlerPageAspect;
        private set => SetProperty(ref _handlerPageAspect, value);
    }

    /// <summary>맞춤 드롭다운을 보일지입니다. 그림이거나, 쪽 모양을 아는 핸들러 문서일 때입니다.</summary>
    public bool ShowFit => ShowImage || (ShowHandler && HandlerPageAspect is not null);

    public bool ShowMessage
    {
        get => _showMessage;
        private set => SetProperty(ref _showMessage, value);
    }

    /// <summary>
    /// 보여 줄 대상을 바꿉니다. 바로 읽지 않고 설정한 시간만큼 기다립니다.
    /// </summary>
    public void SetTarget(FileItemViewModel? item)
    {
        if (_disposed)
        {
            return;
        }

        using var step = UiWatch.Step("미리보기 고르기");

        _timer.Stop();

        if (item is null || item.IsDirectory)
        {
            _pending = null;
            _loadedPath = string.Empty;
            ShowNothing(item is null
                ? "파일을 고르면 여기에 보여 줍니다."
                : "폴더는 미리보기가 없습니다.");
            return;
        }

        if (string.Equals(item.FullPath, _loadedPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _pending = item;

        if (_timer.Interval <= TimeSpan.Zero)
        {
            OnTimerTick(this, EventArgs.Empty);
            return;
        }

        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer.Stop();

        if (_pending is { } item)
        {
            LoadAsync(item).Fire("미리보기");
        }
    }

    private async Task LoadAsync(FileItemViewModel item)
    {
        _load?.Cancel();
        var cancellation = new CancellationTokenSource();
        _load = cancellation;

        _loadedPath = item.FullPath;

        FileName = item.Name;
        Details = string.Format(
            CultureInfo.CurrentCulture,
            "{0} · {1}",
            ByteSizeFormatter.Format(item.Entry.Size),
            item.ModifiedText);
        ExtraInfo = string.Empty;

        if (item.Entry.Size > _settings.ConfirmAboveBytes)
        {
            ShowNothing($"파일이 큽니다({ByteSizeFormatter.Format(item.Entry.Size)}). 자동으로 열지 않았습니다.");
            return;
        }

        var extension = item.Entry.Extension;
        var path = item.FullPath;

        if (ImageExtensions.Contains(extension))
        {
            await LoadImageAsync(path, cancellation.Token).ConfigureAwait(true);
            return;
        }

        if (TextExtensions.Contains(extension))
        {
            await LoadTextAsync(path, extension, cancellation.Token).ConfigureAwait(true);
            return;
        }

        // 동영상과 음악은 핸들러에 맡기지 않습니다. 틀어 버리는 핸들러가 있습니다.
        if (MediaExtensions.Contains(extension))
        {
            await LoadThumbnailAsync(item, cancellation.Token).ConfigureAwait(true);
            return;
        }

        // 한글 문서도 한컴이 등록한 미리보기 핸들러에 맡깁니다. 윈도우 탐색기 미리보기 창과 같은 그림입니다.
        // 핸들러가 없거나 못 띄우면 문서 안에 담긴 첫 쪽 그림으로 물러납니다(ReportHandlerFinished).
        if ((HwpPreview.IsHwp(path) || HwpPreview.IsHwpx(path)) && !ShellPreviewHandler.CanPreview(extension))
        {
            await LoadHwpAsync(item, cancellation.Token).ConfigureAwait(true);
            return;
        }

        // 오피스·한글·PDF처럼 만든 회사가 미리보기 핸들러를 등록해 둔 문서입니다.
        // 이 프로그램과 주고받는 일은 전용 갈래가 하므로 화면은 멈추지 않습니다.
        // 탐색기 오른쪽 칸에 문서가 보이는 것과 같은 구조를 그대로 씁니다.
        if (ShellPreviewHandler.CanPreview(extension))
        {
            ExtraInfo = string.Empty;
            _handlerItem = item;

            // 한글·PDF 문서는 쪽 모양을 알아 두어 칸이 확대·맞춤을 맡게 합니다.
            // 한글은 문서에 담긴 첫 쪽 그림의 비율, PDF 는 첫 쪽의 MediaBox 를 쓰고, 모르면 A4 세로로 봅니다.
            // 오피스 문서는 쪽 모양을 알 길이 없어 여전히 핸들러에 칸 전체를 맡깁니다.
            HandlerPageAspect = HwpPreview.IsHwp(path) || HwpPreview.IsHwpx(path)
                ? await Task.Run(() => ReadHwpAspect(path), cancellation.Token).ConfigureAwait(true)
                : string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase)
                    ? await Task.Run(() => PdfPageShape.ReadAspect(path) ?? A4Portrait, cancellation.Token).ConfigureAwait(true)
                    : (double?)null;

            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            HandlerPath = path;
            SetVisibleSection(handler: true);
            return;
        }

        await LoadThumbnailAsync(item, cancellation.Token).ConfigureAwait(true);
    }

    /// <summary>미리보기 프로그램이 한참 답하지 않는다고 칸에 알립니다.</summary>
    public void ReportHandlerStalled()
    {
        if (ShowHandler)
        {
            ExtraInfo = "미리보기 프로그램이 응답하지 않습니다. 다른 파일은 그대로 고를 수 있습니다.";
        }
    }

    /// <summary>미리보기 프로그램이 답을 마쳤을 때입니다. 못 띄웠으면 그림 미리보기로 물러납니다.</summary>
    public void ReportHandlerFinished(bool shown)
    {
        if (!ShowHandler)
        {
            return;
        }

        // 한글 문서는 핸들러가 못 띄웠을 때 문서에 담긴 첫 쪽 그림이라도 보여 줍니다.
        if (!shown
            && _handlerItem is { } item
            && string.Equals(item.FullPath, HandlerPath, StringComparison.OrdinalIgnoreCase)
            && (HwpPreview.IsHwp(item.FullPath) || HwpPreview.IsHwpx(item.FullPath)))
        {
            var cancellation = _load?.Token ?? CancellationToken.None;
            LoadHwpAsync(item, cancellation).Fire("한글 미리보기");
            return;
        }

        ExtraInfo = shown ? string.Empty : "미리보기 프로그램이 이 파일을 보여 주지 못했습니다.";
    }

    private async Task LoadImageAsync(string path, CancellationToken cancellationToken)
    {
        ShowNothing("읽는 중…");

        var width = 0;
        var height = 0;

        var image = await Task.Run(
            () => PreviewImageLoader.TryLoad(path, MaxImageDecodeWidth, out width, out height),
            cancellationToken).ConfigureAwait(true);

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (image is null)
        {
            ShowNothing("이미지를 읽지 못했습니다. 형식이 맞지 않거나 파일이 손상됐을 수 있습니다.");
            return;
        }

        Image = image;
        ExtraInfo = $"{width} x {height}";
        SetVisibleSection(image: true);
    }

    private async Task LoadTextAsync(string path, string extension, CancellationToken cancellationToken)
    {
        ShowNothing("읽는 중…");

        var encodingName = string.Empty;
        var truncated = false;
        var limit = _settings.MaxTextPreviewBytes;

        var text = await Task.Run(
            () => TextPreviewReader.TryRead(path, limit, out encodingName, out truncated),
            cancellationToken).ConfigureAwait(true);

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (text is null)
        {
            ShowNothing("텍스트로 읽을 수 없는 파일입니다.");
            return;
        }

        // 색을 입힐 구간은 글자 수에 비례해 걸리므로 목록 쪽 일을 막지 않게 따로 셉니다.
        var spans = await Task.Run(
            () => SyntaxHighlighter.Highlight(text, extension),
            cancellationToken).ConfigureAwait(true);

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        Text = text;
        CodeSpans = spans;

        ExtraInfo = truncated
            ? $"{encodingName} · 앞부분 {ByteSizeFormatter.Format(limit)}만 표시"
            : encodingName;

        // 칠할 것이 있으면 색이 들어가는 쪽으로, 없으면 지금까지처럼 민 글자로 보여 줍니다.
        SetVisibleSection(text: spans.Count == 0, code: spans.Count > 0);
    }

    /// <summary>
    /// 한글 문서 안의 미리보기를 보여 줍니다.
    /// 그림이 있으면 그림을, 없으면 앞부분 글자를 보여 주고, 둘 다 없으면 윈도우 미리보기 그림으로 물러납니다.
    /// </summary>
    private async Task LoadHwpAsync(FileItemViewModel item, CancellationToken cancellationToken)
    {
        ShowNothing("읽는 중…");

        var path = item.FullPath;
        var width = 0;
        var height = 0;

        var (preview, image) = await Task.Run(
            () =>
            {
                var data = HwpPreview.Read(path);
                var decoded = data?.Image is { Length: > 0 } bytes ? DecodeImage(bytes, out width, out height) : null;
                return (data, decoded);
            },
            cancellationToken).ConfigureAwait(true);

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (image is not null)
        {
            Image = image;
            ExtraInfo = $"문서에 담긴 첫 쪽 미리보기 ({width} x {height})";
            SetVisibleSection(image: true);
            return;
        }

        if (!string.IsNullOrEmpty(preview?.Text))
        {
            Text = preview.Text;
            ExtraInfo = "문서에 담긴 앞부분 글자 (그림 미리보기 없음)";
            SetVisibleSection(text: true);
            return;
        }

        await LoadThumbnailAsync(item, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>한글 문서 첫 쪽의 가로÷세로입니다. 담긴 그림이 없으면 A4 세로(210÷297)입니다.</summary>
    /// <summary>쪽 모양을 모를 때 쓰는 A4 세로의 가로÷세로입니다.</summary>
    private const double A4Portrait = 210.0 / 297.0;

    private static double ReadHwpAspect(string path)
    {

        try
        {
            if (HwpPreview.Read(path)?.Image is { Length: > 0 } bytes
                && DecodeImage(bytes, out var width, out var height) is not null
                && width > 0
                && height > 0)
            {
                return width / (double)height;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            DiagnosticLog.Write($"한글 쪽 모양을 읽지 못했습니다: {ex.Message}");
        }

        return A4Portrait;
    }

    /// <summary>메모리에 있는 그림(BMP·GIF·PNG 등)을 풉니다. 못 풀면 null 입니다.</summary>
    private static ImageSource? DecodeImage(byte[] bytes, out int width, out int height)
    {
        width = 0;
        height = 0;

        try
        {
            using var stream = new MemoryStream(bytes);
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            width = frame.PixelWidth;
            height = frame.PixelHeight;
            return frame;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or IOException)
        {
            DiagnosticLog.Write($"한글 미리보기 그림을 풀지 못했습니다: {ex.Message}");
            return null;
        }
    }

    private async Task LoadThumbnailAsync(FileItemViewModel item, CancellationToken cancellationToken)
    {
        ShowNothing("읽는 중…");

        // 미리보기 칸은 내용을 보여 주는 자리이므로 종류 아이콘이 아니라 실제 그림을 받습니다.
        var image = await _thumbnails
            .GetAsync(item.FullPath, UnsupportedThumbnailSize, iconOnly: false, cancellationToken)
            .ConfigureAwait(true);

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (image is null)
        {
            ShowNothing($"{item.TypeText}은(는) 아직 미리보기를 지원하지 않습니다.");
            return;
        }

        // 진짜 이미지와 같은 자리에 같은 방식으로 보여 줍니다.
        Image = image;
        ExtraInfo = "윈도우가 만든 미리보기 그림입니다.";
        SetVisibleSection(image: true);
    }

    private void ShowNothing(string message)
    {
        Message = message;
        Image = null;
        Text = string.Empty;
        SetVisibleSection();
    }

    private bool _showHandler;

    private string _handlerPath = string.Empty;

    private double? _handlerPageAspect;

    /// <summary>핸들러에 맡긴 파일의 줄입니다. 핸들러가 실패했을 때 물러날 길을 고르는 데 씁니다.</summary>
    private FileItemViewModel? _handlerItem;

    private void SetVisibleSection(bool image = false, bool text = false, bool handler = false, bool code = false)
    {
        ShowImage = image;
        ShowText = text;
        ShowCode = code;
        ShowHandler = handler;
        ShowMessage = !image && !text && !handler && !code;
        OnPropertyChanged(nameof(ShowFit));

        if (!code)
        {
            CodeSpans = Array.Empty<CodeSpan>();
        }

        if (!handler)
        {
            HandlerPath = string.Empty;
            _handlerItem = null;
            HandlerPageAspect = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _load?.Cancel();
        _load?.Dispose();
    }
}
