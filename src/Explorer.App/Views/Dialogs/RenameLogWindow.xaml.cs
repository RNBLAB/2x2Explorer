using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using Explorer.App.Services;
using Explorer.Shell.Operations;

namespace Explorer.App.Views.Dialogs;

/// <summary>
/// 지난 일괄 이름 바꾸기 기록을 보고 되돌리는 창입니다.
///
/// 되돌리기는 프로그램을 끄면 사라지지만 기록은 파일로 남습니다.
/// 그래서 한참 뒤에도 그때 일을 되짚어 볼 수 있습니다.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class RenameLogWindow : Window
{
    /// <summary>표에 한 줄로 놓을 짝입니다. 화면이 값을 읽어 가므로 드러내 둡니다.</summary>
    public sealed record Row(string Before, string After, string State)
    {
        public string BeforeName => Path.GetFileName(Before);

        public string AfterName => Path.GetFileName(After);
    }

    public RenameLogWindow()
    {
        InitializeComponent();
        Reload();
    }

    private void Reload()
    {
        var records = RenameLog.List();
        Records.ItemsSource = records;

        if (records.Count == 0)
        {
            Summary.Text = "남아 있는 기록이 없습니다.";
            return;
        }

        Summary.Text = $"기록 {records.Count}장";
        Records.SelectedIndex = 0;
    }

    private void OnRecordChosen(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (Records.SelectedItem is not RenameRecord record)
        {
            Pairs.ItemsSource = null;
            UndoButton.IsEnabled = false;
            return;
        }

        var pairs = RenameLog.Read(record.FilePath);
        var rows = new List<Row>(pairs.Count);
        var restorable = 0;

        foreach (var pair in pairs)
        {
            // 바꾼 뒤 이름이 그대로 있어야 되돌릴 수 있습니다.
            var there = Exists(pair.After);
            var taken = Exists(pair.Before);

            var state = !there ? "없어짐" : taken ? "자리 참" : "되돌릴 수 있음";

            if (there && !taken)
            {
                restorable++;
            }

            rows.Add(new Row(pair.Before, pair.After, state));
        }

        Pairs.ItemsSource = rows;
        UndoButton.IsEnabled = restorable > 0;

        Summary.Text = restorable == pairs.Count
            ? $"{pairs.Count}개 모두 되돌릴 수 있습니다."
            : $"{pairs.Count}개 가운데 {restorable}개를 되돌릴 수 있습니다.";
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (Records.SelectedItem is not RenameRecord record)
        {
            return;
        }

        var pairs = RenameLog.Read(record.FilePath);
        var requests = new List<ShellFileOperations.RenameRequest>();
        var reversed = new List<RenamePair>();

        foreach (var pair in pairs)
        {
            if (!Exists(pair.After) || Exists(pair.Before))
            {
                continue;
            }

            requests.Add(new ShellFileOperations.RenameRequest(pair.After, Path.GetFileName(pair.Before)));
            reversed.Add(new RenamePair(pair.After, pair.Before));
        }

        if (requests.Count == 0)
        {
            MessageBox.Show(
                this,
                "되돌릴 수 있는 것이 없습니다.",
                "이름 바꾼 기록",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var owner = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        if (!ShellFileOperations.RenameMany(requests, owner))
        {
            MessageBox.Show(
                this,
                "되돌리기를 마치지 못했습니다. 파일이 열려 있거나 권한이 없을 수 있습니다.",
                "이름 바꾼 기록",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        // 되돌린 것도 이름 바꾸기입니다. 그래야 이것마저 다시 되돌릴 수 있습니다.
        var written = RenameLog.Write(reversed);

        MessageBox.Show(
            this,
            written is null
                ? $"{requests.Count}개를 되돌렸습니다. 기록은 남기지 못했습니다."
                : $"{requests.Count}개를 되돌렸습니다.{Environment.NewLine}{Environment.NewLine}기록: {written}",
            "이름 바꾼 기록",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        Reload();
    }

    private static bool Exists(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
