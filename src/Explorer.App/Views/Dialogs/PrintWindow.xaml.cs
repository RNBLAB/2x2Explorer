using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Explorer.Core.Models;
using Explorer.Core.Services;
using Explorer.Shell.Operations;
using Explorer.Shell.Printing;

namespace Explorer.App.Views.Dialogs;

/// <summary>
/// 여러 파일을 한 대의 프린터로 차례대로 찍는 창입니다.
///
/// 목록에 담고, 차례를 손보고, 프린터를 한 번 고른 뒤 시작합니다.
/// 찍는 동안 진행 상황이 아래 기록 칸에 한 줄씩 쌓입니다.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class PrintWindow : Window
{
    /// <summary>목록에 보이는 한 줄입니다. 화면이 값을 읽어 가므로 드러내 둡니다.</summary>
    public sealed record PrintRow(int Number, string Kind, string Name, string Path);

    private readonly List<string> _paths = new();
    private readonly ObservableCollection<PrintRow> _rows = new();

    private readonly string? _folder;

    private CancellationTokenSource? _printing;

    /// <param name="paths">처음부터 담을 파일. 비어 있으면 빈 목록으로 엽니다.</param>
    /// <param name="folder">[탐색기 열기] 단추가 윈도우 탐색기로 열 폴더. 없으면 단추를 끕니다.</param>
    public PrintWindow(IReadOnlyList<string> paths, string? folder = null)
    {
        InitializeComponent();
        PopupTitleBar.Apply(this, fillScreen: true);

        Items.ItemsSource = _rows;

        _folder = folder is not null && Directory.Exists(folder) ? folder : null;
        OpenExplorerButton.IsEnabled = _folder is not null;

        if (_folder is not null)
        {
            OpenExplorerButton.ToolTip = $"{_folder}{Environment.NewLine}윈도우 탐색기에서 파일을 골라 이 창으로 끌어다 놓으십시오.";
        }

        FillPrinters();
        Note("이미지 : A4 / 페이지 방향 자동 / 비율 유지 꽉 차게 (직접 인쇄)");
        Note("문서   : 연결된 프로그램의 기본 인쇄 (용지와 배율은 그 프로그램 설정을 따릅니다)");

        WarnIfElevated();
        Add(paths);

        // 오른쪽 탐색기: 인쇄할 수 있는 파일만 보이고, 두 번 누르면 인쇄 목록에 넣습니다.
        SideExplorer.FileFilter = path => PrintItems.KindOf(path) != PrintItemKind.Unsupported;
        SideExplorer.FilesActivated += found =>
        {
            if (!IsPrinting)
            {
                Add(found);
            }
        };
        SideExplorer.StatusMessage += Note;
        SideExplorer.Start(_folder ?? (paths.Count > 0 ? Path.GetDirectoryName(paths[0]) : null));
        TitleBar.EnableFavorites(SideExplorer.OpenFavorite);
    }

    /// <summary>
    /// 일괄 인쇄 창을 엽니다. 칸의 Ctrl+P 와 제목 띠의 단추가 함께 씁니다.
    /// 고른 파일이 있으면 담고, 없으면 빈 목록으로 엽니다.
    /// 지금 폴더는 목록에 담지 않고 [탐색기 열기] 단추가 여는 자리로만 넘깁니다.
    /// </summary>
    public static void ShowFor(Window? owner, IReadOnlyList<string> selected, string? currentPath)
    {
        var folder = !string.IsNullOrEmpty(currentPath) && !SpecialPaths.IsVirtual(currentPath)
            ? currentPath
            : null;

        var dialog = new PrintWindow(selected, folder)
        {
            Owner = owner
        };

        dialog.ShowDialog();
    }

    private bool IsPrinting => _printing is not null;

    // ── 목록 ──

    private void Add(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            Refresh();
            return;
        }

        var added = PrintItems.Expand(paths, _paths, out var skipped);
        _paths.AddRange(added);

        Refresh();

        var message = $"{added.Count}개를 담았습니다.";

        if (skipped > 0)
        {
            message += $" 찍을 수 없는 {skipped}개는 뺐습니다.";
        }

        Note(message);
    }

    private void Refresh()
    {
        _rows.Clear();

        var images = 0;

        for (var i = 0; i < _paths.Count; i++)
        {
            var path = _paths[i];
            var kind = PrintItems.KindOf(path);

            if (kind == PrintItemKind.Image)
            {
                images++;
            }

            _rows.Add(new PrintRow(i + 1, PrintItems.Describe(kind), Path.GetFileName(path), path));
        }

        CountLabel.Text = _paths.Count == 0
            ? "담긴 것이 없습니다"
            : $"모두 {_paths.Count}개 (이미지 {images} · 문서 {_paths.Count - images})";
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

        e.Effects = !IsPrinting && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

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

        if (IsPrinting)
        {
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped)
        {
            Add(dropped);
        }
    }

    /// <summary>지금 폴더를 윈도우 탐색기로 엽니다. 거기서 끌어다 놓는 것이 가장 빠른 담기 방법입니다.</summary>
    private void OnOpenExplorer(object sender, RoutedEventArgs e)
    {
        if (_folder is null)
        {
            return;
        }

        if (!ShellLauncher.OpenInExplorer(_folder))
        {
            Note($"탐색기를 열지 못했습니다: {_folder}");
        }
    }

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = "인쇄할 파일 고르기",
            Filter = PrintItems.DialogFilter()
        };

        if (dialog.ShowDialog(this) == true)
        {
            Add(dialog.FileNames);
        }
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "인쇄할 파일이 든 폴더 고르기"
        };

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
            if (item is PrintRow row)
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

    private void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => Move(1);

    /// <summary>고른 한 줄을 위나 아래로 옮깁니다. 옮긴 줄을 그대로 고른 채로 둡니다.</summary>
    private void Move(int delta)
    {
        if (Items.SelectedItem is not PrintRow row)
        {
            return;
        }

        var index = row.Number - 1;

        if (!PrintItems.Move(_paths, index, delta))
        {
            return;
        }

        Refresh();

        var moved = index + delta;

        if (moved >= 0 && moved < _rows.Count)
        {
            Items.SelectedItem = _rows[moved];
            Items.ScrollIntoView(_rows[moved]);
        }
    }

    private void OnSort(object sender, RoutedEventArgs e)
    {
        var sorted = PrintItems.SortByName(_paths);
        _paths.Clear();
        _paths.AddRange(sorted);

        Refresh();
        Note("이름순으로 다시 늘어놓았습니다.");
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _paths.Clear();
        Refresh();
        Note("목록을 비웠습니다.");
    }

    // ── 프린터 ──

    private void FillPrinters()
    {
        var printers = BatchPrinter.InstalledPrinters();

        foreach (var printer in printers)
        {
            PrinterBox.Items.Add(printer);
        }

        if (printers.Count == 0)
        {
            Note("설치된 프린터가 없습니다.");
            return;
        }

        var preferred = BatchPrinter.DefaultPrinter();
        PrinterBox.SelectedItem = PrinterBox.Items.Contains(preferred) ? preferred : PrinterBox.Items[0];
    }

    // ── 인쇄 ──

    private async void OnPrint(object sender, RoutedEventArgs e)
    {
        if (IsPrinting)
        {
            _printing?.Cancel();
            Note("멈추라고 알렸습니다. 지금 파일이 끝나면 멈춥니다.");
            return;
        }

        if (_paths.Count == 0)
        {
            MessageBox.Show(this, "찍을 것이 없습니다.", "일괄 인쇄", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (PrinterBox.SelectedItem is not string printer || printer.Length == 0)
        {
            MessageBox.Show(this, "프린터를 고르십시오.", "일괄 인쇄", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!BatchPrinter.CanUse(printer))
        {
            MessageBox.Show(this, $"프린터를 쓸 수 없습니다: {printer}", "일괄 인쇄", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var documents = 0;

        foreach (var path in _paths)
        {
            if (PrintItems.KindOf(path) == PrintItemKind.Document)
            {
                documents++;
            }
        }

        var message = $"{_paths.Count}개를 [{printer}] 으로 찍습니다.{Environment.NewLine}목록에 보이는 차례대로 갑니다.";

        if (documents > 0)
        {
            // 이 부수효과는 반드시 미리 알려야 합니다. 찍는 동안 다른 프로그램의 인쇄도 이 프린터로 나갑니다.
            message += Environment.NewLine + Environment.NewLine
                + $"그 가운데 문서 {documents}개는 연결된 프로그램의 기본 인쇄로 나갑니다."
                + Environment.NewLine
                + $"그 동안 윈도우 기본 프린터가 [{printer}] 으로 잠시 바뀌었다가 되돌아옵니다.";
        }

        if (MessageBox.Show(this, message, "인쇄 확인", MessageBoxButton.OKCancel, MessageBoxImage.Information)
            != MessageBoxResult.OK)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _printing = cancellation;

        SetBusy(true);

        var progress = new Progress<string>(Note);

        try
        {
            var results = await BatchPrinter
                .PrintAsync(new List<string>(_paths), printer, progress, cancellation.Token)
                .ConfigureAwait(true);

            var ok = 0;

            foreach (var result in results)
            {
                if (result.Succeeded)
                {
                    ok++;
                }
            }

            Note($"끝났습니다 — 성공 {ok} / 실패 {results.Count - ok}");
            Note(new string('─', 40));

            // 문서 쪽은 보냈다는 것까지만 압니다. 실제로 종이가 나왔는지는 사람이 봐야 압니다.
            MessageBox.Show(
                this,
                $"{results.Count}개를 처리했습니다. (성공 {ok} / 실패 {results.Count - ok}){Environment.NewLine}{Environment.NewLine}"
                    + "문서는 연결된 프로그램에 보낸 것까지만 알 수 있습니다. 실제 인쇄 결과는 프린터에서 확인해 주십시오.",
                "일괄 인쇄",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            Note("멈췄습니다.");
        }
        finally
        {
            _printing = null;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        PrintButton.Content = busy ? "멈추기" : "인쇄 시작";

        AddFileButton.IsEnabled = !busy;
        AddFolderButton.IsEnabled = !busy;
        RemoveButton.IsEnabled = !busy;
        UpButton.IsEnabled = !busy;
        DownButton.IsEnabled = !busy;
        SortButton.IsEnabled = !busy;
        ClearButton.IsEnabled = !busy;
        PrinterBox.IsEnabled = !busy;
        CloseButton.IsEnabled = !busy;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!IsPrinting)
        {
            return;
        }

        // 찍는 중에 창이 사라지면 기본 프린터를 되돌릴 자리를 잃습니다.
        e.Cancel = true;
        MessageBox.Show(this, "인쇄가 끝난 뒤에 닫아 주십시오.", "일괄 인쇄", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ── 기록 ──

    private void Note(string message)
    {
        Log.AppendText(message + Environment.NewLine);
        Log.ScrollToEnd();
    }

    /// <summary>
    /// 관리자 권한으로 돌고 있으면 탐색기에서 이 창으로 끌어다 놓는 것이 윈도우 정책에 막힙니다.
    /// 그때는 단추로 담아야 하므로 미리 알려 줍니다.
    /// </summary>
    private void WarnIfElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();

            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                return;
            }

            Note(string.Empty);
            Note("※ 관리자 권한으로 돌고 있습니다. 윈도우 정책상 탐색기에서 이 창으로");
            Note("  끌어다 놓기가 막힙니다. [파일 추가] 단추를 쓰십시오.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            DiagnosticLog.Write($"권한을 살피지 못했습니다: {ex.Message}");
        }
    }
}
