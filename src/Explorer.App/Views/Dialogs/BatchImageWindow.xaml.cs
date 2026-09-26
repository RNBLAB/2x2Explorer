using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Explorer.App.Services.Imaging;
using Explorer.Core.Services;

namespace Explorer.App.Views.Dialogs;

/// <summary>
/// 여러 그림에 같은 편집(크기, 가운데 자르기, 회전, 형식, EXIF)을 걸어 새 파일로 저장하는 창입니다.
/// 원본은 건드리지 않습니다. 저장 위치는 원본마다 하위 폴더를 만들거나, 한 폴더를 골라 모읍니다.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class BatchImageWindow : Window
{
    /// <summary>목록의 한 줄입니다.</summary>
    public sealed record ImageRow(int Number, string Name, string Folder, string Path);

    /// <summary>이 실행 동안 마지막으로 고른 저장 폴더입니다. 창을 다시 열어도 이어 씁니다.</summary>
    private static string? _lastFolder;

    private readonly List<string> _paths = new();
    private readonly ObservableCollection<ImageRow> _rows = new();
    private readonly Dictionary<ExifField, (CheckBox Check, TextBox Box)> _exif = new();
    private readonly Action<string>? _openFolder;

    private CancellationTokenSource? _running;
    private string? _lastOutputFolder;

    /// <param name="paths">처음부터 담을 파일과 폴더.</param>
    /// <param name="openFolder">[결과 폴더 열기]가 부를 일. 탐색기 칸에 새 탭으로 엽니다.</param>
    public BatchImageWindow(IReadOnlyList<string> paths, Action<string>? openFolder)
    {
        InitializeComponent();
        PopupTitleBar.Apply(this, fillScreen: true);

        _openFolder = openFolder;
        Items.ItemsSource = _rows;

        BuildExifRows();

        // 비율 목록은 단일 편집과 같은 것을 씁니다. 처음에는 4:3 을 고릅니다.
        foreach (var ratio in ImageMath.CropRatios)
        {
            CropRatioBox.Items.Add(ratio.Label);
        }

        CropRatioBox.SelectedIndex = Math.Max(0, IndexOfRatio("4:3"));

        if (_lastFolder is not null)
        {
            FolderBox.Text = _lastFolder;
        }

        Note("원본은 덮어쓰지 않습니다. 같은 이름이 있으면 (2)처럼 번호를 붙여 새로 만듭니다.");
        Add(paths);

        // 오른쪽 탐색기: 그림만 보이고, 두 번 누르면 편집 목록에 넣습니다.
        SideExplorer.FileFilter = ImageMath.IsEditable;
        SideExplorer.FilesActivated += found =>
        {
            if (!IsRunning)
            {
                Add(found);
            }
        };
        SideExplorer.StatusMessage += Note;
        SideExplorer.Start(paths.Count > 0 ? Path.GetDirectoryName(paths[0]) : null);
        TitleBar.EnableFavorites(SideExplorer.OpenFavorite);
    }

    /// <summary>일괄 편집 창을 엽니다. 고른 파일이 있으면 담고, 없으면 빈 목록으로 엽니다.</summary>
    public static void ShowFor(Window? owner, IReadOnlyList<string> selected, Action<string>? openFolder)
    {
        var dialog = new BatchImageWindow(selected, openFolder) { Owner = owner };
        dialog.ShowDialog();
    }

    private bool IsRunning => _running is not null;

    // ── EXIF 칸 ──

    private void BuildExifRows()
    {
        ExifGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        ExifGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var row = 0;

        foreach (ExifField field in Enum.GetValues(typeof(ExifField)))
        {
            ExifGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var check = new CheckBox
            {
                Content = ExifEdit.Label(field),
                Margin = new Thickness(0, 0, 6, 4),
                ToolTip = "체크하면 이 항목을 바꿉니다"
            };

            var box = new TextBox
            {
                Style = (Style)FindResource("AddressBox"),
                Margin = new Thickness(0, 0, 0, 4)
            };

            // 칸에 무언가 적으면 체크를 켭니다. 적어 놓고 체크를 잊는 일이 잦습니다.
            box.TextChanged += (_, _) =>
            {
                if (box.IsKeyboardFocusWithin && box.Text.Length > 0)
                {
                    check.IsChecked = true;
                }
            };

            if (field == ExifField.DateTaken)
            {
                box.ToolTip = "예: 2026-09-25 14:30 또는 2026.09.25 14:30:05";
            }
            else if (field is ExifField.Author or ExifField.Keywords)
            {
                box.ToolTip = "여럿이면 ; 로 나눕니다";
            }

            Grid.SetRow(check, row);
            Grid.SetColumn(check, 0);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);

            ExifGrid.Children.Add(check);
            ExifGrid.Children.Add(box);

            _exif[field] = (check, box);
            row++;
        }
    }

    /// <summary>고른 파일(없으면 첫 파일)의 EXIF 값을 칸에 채웁니다. 무엇을 바꿀지는 사람이 체크로 정합니다.</summary>
    private void OnReadExif(object sender, RoutedEventArgs e)
    {
        var path = (Items.SelectedItem as ImageRow)?.Path ?? (_paths.Count > 0 ? _paths[0] : null);

        if (path is null)
        {
            Note("읽어 올 파일이 없습니다.");
            return;
        }

        try
        {
            var loaded = ImageCodec.Load(path);
            var metadata = loaded.Metadata;

            if (metadata is null || !ImageCodec.CanHoldExif(loaded.Container))
            {
                Note($"{Path.GetFileName(path)}: EXIF가 없습니다.");
                return;
            }

            Fill(ExifField.DateTaken, () => FormatDate(metadata.DateTaken));
            Fill(ExifField.Author, () => metadata.Author is null ? string.Empty : string.Join("; ", metadata.Author));
            Fill(ExifField.Copyright, () => metadata.Copyright);
            Fill(ExifField.Title, () => metadata.Title);
            Fill(ExifField.Subject, () => metadata.Subject);
            Fill(ExifField.Comment, () => metadata.Comment);
            Fill(ExifField.Keywords, () => metadata.Keywords is null ? string.Empty : string.Join("; ", metadata.Keywords));
            Fill(ExifField.CameraMaker, () => metadata.CameraManufacturer);
            Fill(ExifField.CameraModel, () => metadata.CameraModel);

            Note($"{Path.GetFileName(path)} 의 EXIF를 칸에 채웠습니다. 바꿀 항목을 체크하십시오.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException || ImageCodec.IsCodecError(ex))
        {
            Note($"EXIF를 읽지 못했습니다: {ex.Message}");
        }
    }

    private void Fill(ExifField field, Func<string?> read)
    {
        string? value;

        try
        {
            value = read();
        }
        catch (Exception ex) when (ImageCodec.IsCodecError(ex))
        {
            value = null;
        }

        _exif[field].Box.Text = value ?? string.Empty;
    }

    private static string FormatDate(string? text) =>
        DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : text ?? string.Empty;

    // ── 목록 ──

    private void Add(IReadOnlyList<string> paths)
    {
        var added = 0;
        var skipped = 0;
        var known = new HashSet<string>(_paths, StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var files = new List<string>();

                try
                {
                    files.AddRange(Directory.EnumerateFiles(path));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Note($"폴더를 읽지 못했습니다: {path} — {ex.Message}");
                    continue;
                }

                files.Sort(NaturalStringComparer.Instance);

                foreach (var file in files)
                {
                    if (ImageMath.IsEditable(file) && known.Add(file))
                    {
                        _paths.Add(file);
                        added++;
                    }
                }
            }
            else if (ImageMath.IsEditable(path) && File.Exists(path))
            {
                if (known.Add(path))
                {
                    _paths.Add(path);
                    added++;
                }
            }
            else
            {
                skipped++;
            }
        }

        Refresh();

        if (paths.Count > 0)
        {
            Note(skipped > 0 ? $"{added}개를 담았습니다. 그림이 아닌 {skipped}개는 뺐습니다." : $"{added}개를 담았습니다.");
        }
    }

    private void Refresh()
    {
        _rows.Clear();

        for (var i = 0; i < _paths.Count; i++)
        {
            var path = _paths[i];
            _rows.Add(new ImageRow(i + 1, Path.GetFileName(path), Path.GetDirectoryName(path) ?? string.Empty, path));
        }

        CountLabel.Text = _paths.Count == 0 ? "담긴 그림이 없습니다" : $"모두 {_paths.Count}개";
    }

    private void OnFilesDragOver(object sender, DragEventArgs e)
    {
        // 오른쪽 탐색기 위에 놓는 것은 받지 않습니다. 탐색기에서 끌어 온 것을 제자리에 놓는 경우입니다.
        if (e.OriginalSource is DependencyObject over && SideExplorer.IsAncestorOf(over))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = !IsRunning && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFilesDropped(object sender, DragEventArgs e)
    {
        // 오른쪽 탐색기 위에 놓는 것은 받지 않습니다. 탐색기에서 끌어 온 것을 제자리에 놓는 경우입니다.
        if (e.OriginalSource is DependencyObject over && SideExplorer.IsAncestorOf(over))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Handled = true;

        if (!IsRunning && e.Data.GetData(DataFormats.FileDrop) is string[] dropped)
        {
            Add(dropped);
        }
    }

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = "편집할 그림 고르기",
            Filter = ImageCodec.OpenFilter
        };

        if (dialog.ShowDialog(this) == true)
        {
            Add(dialog.FileNames);
        }
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "그림이 든 폴더 고르기" };

        if (dialog.ShowDialog(this) == true)
        {
            Add(new[] { dialog.FolderName });
        }
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        var chosen = new List<int>();

        foreach (var item in Items.SelectedItems)
        {
            if (item is ImageRow row)
            {
                chosen.Add(row.Number - 1);
            }
        }

        chosen.Sort();

        for (var i = chosen.Count - 1; i >= 0; i--)
        {
            if (chosen[i] >= 0 && chosen[i] < _paths.Count)
            {
                _paths.RemoveAt(chosen[i]);
            }
        }

        Refresh();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _paths.Clear();
        Refresh();
        Note("목록을 비웠습니다.");
    }

    // ── 저장 위치 ──

    private void OnSubfolderFocus(object sender, RoutedEventArgs e) => SubfolderRadio.IsChecked = true;

    private void OnFolderFocus(object sender, RoutedEventArgs e) => FolderRadio.IsChecked = true;

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "저장할 폴더 고르기" };

        if (Directory.Exists(FolderBox.Text))
        {
            dialog.InitialDirectory = FolderBox.Text;
        }
        else if (_paths.Count > 0 && Path.GetDirectoryName(_paths[0]) is { } first)
        {
            dialog.InitialDirectory = first;
        }

        if (dialog.ShowDialog(this) == true)
        {
            FolderBox.Text = dialog.FolderName;
            FolderRadio.IsChecked = true;
        }
    }

    private void OnOpenOutput(object sender, RoutedEventArgs e)
    {
        if (_lastOutputFolder is not null && Directory.Exists(_lastOutputFolder))
        {
            _openFolder?.Invoke(_lastOutputFolder);
        }
    }

    // ── 시작 ──

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (IsRunning)
        {
            _running?.Cancel();
            Note("멈추라고 알렸습니다. 지금 파일이 끝나면 멈춥니다.");
            return;
        }

        if (_paths.Count == 0)
        {
            MessageBox.Show(this, "편집할 그림이 없습니다.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryReadOptions(out var options, out var problem))
        {
            MessageBox.Show(this, problem, Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var cancellation = new CancellationTokenSource();
        _running = cancellation;
        SetBusy(true);

        Note(new string('─', 40));
        Note(Describe(options));

        try
        {
            var results = await BatchImageJob
                .RunAsync(new List<string>(_paths), options, new Progress<string>(Note), cancellation.Token)
                .ConfigureAwait(true);

            var ok = 0;

            foreach (var result in results)
            {
                if (result.Succeeded)
                {
                    ok++;

                    if (result.Output is not null)
                    {
                        _lastOutputFolder = Path.GetDirectoryName(result.Output);
                    }
                }
            }

            OpenOutputButton.IsEnabled = _lastOutputFolder is not null && _openFolder is not null;
            Note($"끝났습니다 — 성공 {ok} / 실패·건너뜀 {results.Count - ok}");
        }
        catch (OperationCanceledException)
        {
            Note("멈췄습니다.");
        }
        catch (Exception ex)
        {
            Note($"멈췄습니다: {ex.Message}");
            DiagnosticLog.Write($"일괄 편집이 멈췄습니다: {ex}");
        }
        finally
        {
            _running = null;
            SetBusy(false);
        }
    }

    /// <summary>화면의 값을 읽어 설정으로 묶습니다. 빠졌거나 틀린 값이 있으면 무엇인지 돌려줍니다.</summary>
    private bool TryReadOptions(out BatchImageOptions options, out string problem)
    {
        options = new BatchImageOptions();
        problem = string.Empty;

        var resize = ResizeCheck.IsChecked == true;
        double resizeValue = 0;

        if (resize && (!double.TryParse(ResizeValueBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out resizeValue)
            || resizeValue <= 0 || resizeValue > 30000))
        {
            problem = "크기 값을 확인하십시오. 픽셀이면 1~30000, 퍼센트면 1 이상입니다.";
            return false;
        }

        var crop = CropCheck.IsChecked == true;
        double ratioWidth = 4, ratioHeight = 3;

        if (crop)
        {
            var index = CropRatioBox.SelectedIndex;

            if (index < 0 || index >= ImageMath.CropRatios.Count)
            {
                problem = "자르기 비율을 고르십시오.";
                return false;
            }

            ratioWidth = ImageMath.CropRatios[index].Width;
            ratioHeight = ImageMath.CropRatios[index].Height;
        }

        if (!int.TryParse(QualityBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var quality) || quality is < 1 or > 100)
        {
            problem = "JPEG 품질은 1~100 사이로 넣으십시오.";
            return false;
        }

        var edit = new ExifEdit();

        foreach (var (field, (check, box)) in _exif)
        {
            if (check.IsChecked != true)
            {
                continue;
            }

            if (field == ExifField.DateTaken && box.Text.Trim().Length > 0 && !ExifEdit.TryParseDate(box.Text, out _))
            {
                problem = "촬영 일시를 읽지 못했습니다. 예: 2026-09-25 14:30";
                return false;
            }

            edit.Set(field, box.Text);
        }

        string folder;

        if (FolderRadio.IsChecked == true)
        {
            folder = FolderBox.Text.Trim();

            if (folder.Length == 0 || !Path.IsPathFullyQualified(folder))
            {
                problem = "저장할 폴더를 고르십시오. [찾아보기…]를 누르면 됩니다.";
                return false;
            }

            _lastFolder = folder;
        }
        else
        {
            folder = SubfolderBox.Text.Trim();

            if (folder.Length == 0 || folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                problem = "하위 폴더 이름을 확인하십시오. 비울 수 없고 \\ / : * ? \" < > | 는 쓸 수 없습니다.";
                return false;
            }
        }

        var suffix = SuffixBox.Text;

        if (suffix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            problem = "이름 끝에 붙일 말에 파일 이름으로 쓸 수 없는 글자가 있습니다.";
            return false;
        }

        var format = FormatBox.SelectedIndex switch
        {
            1 => ".jpg",
            2 => ".png",
            3 => ".bmp",
            4 => ".tif",
            _ => null
        };

        var policy = PolicyBox.SelectedIndex switch
        {
            1 => MetadataPolicy.StripGps,
            2 => MetadataPolicy.StripAll,
            _ => MetadataPolicy.Keep
        };

        var rotate = RotateBox.SelectedIndex switch
        {
            1 => 90,
            2 => 180,
            3 => 270,
            _ => 0
        };

        options = new BatchImageOptions
        {
            Resize = resize,
            ResizeMode = ResizeModeBox.SelectedIndex switch
            {
                1 => ImageResizeMode.Width,
                2 => ImageResizeMode.Height,
                3 => ImageResizeMode.Percent,
                _ => ImageResizeMode.LongSide
            },
            ResizeValue = resizeValue,
            NoUpscale = NoUpscaleCheck.IsChecked == true,
            Crop = crop,
            RatioWidth = ratioWidth,
            RatioHeight = ratioHeight,
            CropFollowsOrientation = FollowCheck.IsChecked == true,
            Rotate = rotate,
            Format = format,
            JpegQuality = quality,
            Policy = policy,
            Edit = edit.IsEmpty ? null : edit,
            OutputFolder = folder,
            Suffix = suffix
        };

        if (!options.ChangesPixels && format is null && policy == MetadataPolicy.Keep && edit.IsEmpty)
        {
            problem = "할 일이 없습니다. 크기·자르기·회전·형식·EXIF 가운데 하나 이상을 고르십시오.";
            return false;
        }

        return true;
    }

    private static int IndexOfRatio(string label)
    {
        for (var i = 0; i < ImageMath.CropRatios.Count; i++)
        {
            if (ImageMath.CropRatios[i].Label == label)
            {
                return i;
            }
        }

        return -1;
    }

    private static string Describe(BatchImageOptions options)
    {
        var parts = new List<string>();

        if (options.Resize)
        {
            var unit = options.ResizeMode == ImageResizeMode.Percent ? "%" : "px";
            var what = options.ResizeMode switch
            {
                ImageResizeMode.Width => "가로",
                ImageResizeMode.Height => "세로",
                ImageResizeMode.Percent => "배율",
                _ => "긴 변"
            };

            parts.Add($"크기 {what} {options.ResizeValue.ToString(CultureInfo.InvariantCulture)}{unit}");
        }

        if (options.Crop)
        {
            parts.Add($"가운데 자르기 {options.RatioWidth.ToString("0.##", CultureInfo.InvariantCulture)}:{options.RatioHeight.ToString("0.##", CultureInfo.InvariantCulture)}");
        }

        if (options.Rotate != 0)
        {
            parts.Add($"회전 {options.Rotate}°");
        }

        parts.Add(options.Format is null ? "형식 그대로" : $"형식 {options.Format}");

        parts.Add(options.Policy switch
        {
            MetadataPolicy.StripGps => "GPS 지움",
            MetadataPolicy.StripAll => "메타데이터 모두 지움",
            _ => "메타데이터 유지"
        });

        if (options.Edit is { } edit)
        {
            var fields = new List<string>();

            foreach (var field in edit.Changes.Keys)
            {
                fields.Add(ExifEdit.Label(field));
            }

            parts.Add("EXIF 고침: " + string.Join(", ", fields));
        }

        var where = Path.IsPathFullyQualified(options.OutputFolder)
            ? options.OutputFolder
            : $"원본 폴더\\{options.OutputFolder}";

        return string.Join(" · ", parts) + $"{Environment.NewLine}저장: {where}";
    }

    private void SetBusy(bool busy)
    {
        StartButton.Content = busy ? "멈추기" : "시작";

        AddFileButton.IsEnabled = !busy;
        AddFolderButton.IsEnabled = !busy;
        RemoveButton.IsEnabled = !busy;
        ClearButton.IsEnabled = !busy;
        ReadExifButton.IsEnabled = !busy;
        CloseButton.IsEnabled = !busy;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!IsRunning)
        {
            return;
        }

        e.Cancel = true;
        MessageBox.Show(this, "작업이 끝난 뒤에 닫아 주십시오. [멈추기]로 멈출 수 있습니다.", Title,
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Note(string message)
    {
        Log.AppendText(message + Environment.NewLine);
        Log.ScrollToEnd();
    }
}
