using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using Explorer.Core.Services;
using Explorer.Core.Settings;
using Microsoft.Win32;

namespace Explorer.App.Views.Dialogs;

/// <summary>
/// 즐겨찾기 열네 칸을 직접 고쳐 쓰는 창입니다.
///
/// 1~5번은 기본값(내 PC · 바탕 화면 · 다운로드 · 문서 · 휴지통)이 정해진 칸입니다.
/// 고칠 수 있고, 아래의 단추로 언제든 기본값으로 되돌릴 수 있습니다.
/// 줄마다 "바로가기 표시" 체크가 있어, 체크한 즐겨찾기만 제목 표시줄에 단추로 뜹니다.
///
/// 새로 넣을 폴더를 들고 열 수도 있습니다.
/// 빈 칸이 있으면 거기에 미리 채워 두고, 칸이 다 찼으면 어느 자리를 내줄지 사용자가 고르게 합니다.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class FavoritesWindow : Window
{
    private readonly TextBox[] _inputs = new TextBox[AppSettings.MaxFavorites];
    private readonly CheckBox[] _pins = new CheckBox[AppSettings.MaxFavorites];
    private readonly string? _pendingPath;

    /// <param name="settings">지금 즐겨찾기와 제목 표시줄 체크를 읽어 올 설정입니다. 여기서는 고치지 않습니다.</param>
    /// <param name="pendingPath">새로 넣으려고 들고 온 폴더입니다. 없으면 null 입니다.</param>
    public FavoritesWindow(AppSettings settings, string? pendingPath = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _pendingPath = string.IsNullOrWhiteSpace(pendingPath) ? null : pendingPath.Trim();

        InitializeComponent();
        BuildRows(settings);
        UpdateHeadline(settings.Favorites);
    }

    /// <summary>확인을 눌렀을 때의 즐겨찾기입니다. 빈 칸은 빠집니다.</summary>
    public List<string> Result { get; } = new();

    /// <summary>확인을 눌렀을 때 제목 표시줄에 띄우기로 체크한 즐겨찾기입니다.</summary>
    public List<string> TitleBar { get; } = new();

    private void UpdateHeadline(IReadOnlyList<string> favorites)
    {
        if (_pendingPath is null)
        {
            return;
        }

        var name = SpecialPaths.ToDisplay(_pendingPath);
        var full = favorites.Count >= AppSettings.MaxFavorites;

        Headline.Text = full
            ? $"즐겨찾기 {AppSettings.MaxFavorites}칸이 모두 찼습니다. \"{name}\"을(를) 넣을 자리를 고르세요. 각 줄의 \"현재 경로\"를 누르면 그 줄이 바뀝니다."
            : $"\"{name}\"을(를) 빈 칸에 넣어 두었습니다. 확인을 누르면 저장됩니다.";

        Title = "즐겨찾기에 추가";
    }

    /// <summary>그 칸의 기본값 이름입니다. 기본값이 없는 칸이면 빈 글입니다.</summary>
    private static string DefaultNote(int index) =>
        index < AppSettings.DefaultFavoriteCount
            ? "기본값: " + SpecialPaths.ToDisplay(AppSettings.DefaultFavorites[index])
            : string.Empty;

    private void BuildRows(AppSettings settings)
    {
        var favorites = settings.Favorites;

        // 빈 칸이 있으면 새 폴더를 거기에 미리 채웁니다.
        var fillIndex = -1;

        if (_pendingPath is not null && favorites.Count < AppSettings.MaxFavorites)
        {
            fillIndex = favorites.Count;
        }

        for (var i = 0; i < _inputs.Length; i++)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var number = new TextBlock
            {
                Text = (i + 1).ToString(CultureInfo.CurrentCulture),
                Style = (Style)FindResource("PaneSubLabel"),
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(number, 0);
            row.Children.Add(number);

            // 순서 바꾸기 단추입니다. 위아래 줄과 내용을 맞바꿉니다.
            var order = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 6, 0)
            };

            var index = i;

            var up = new Button
            {
                Content = "▲",
                Width = 24,
                Height = 24,
                Margin = new Thickness(0, 0, 2, 0),
                IsEnabled = i > 0,
                ToolTip = "위로"
            };

            up.Click += (_, _) => Swap(index, index - 1);
            order.Children.Add(up);

            var down = new Button
            {
                Content = "▼",
                Width = 24,
                Height = 24,
                IsEnabled = i < _inputs.Length - 1,
                ToolTip = "아래로"
            };

            down.Click += (_, _) => Swap(index, index + 1);
            order.Children.Add(down);

            Grid.SetColumn(order, 1);
            row.Children.Add(order);

            var current = i < favorites.Count ? favorites[i] : string.Empty;
            var text = SpecialPaths.ToDisplay(current);

            if (i == fillIndex)
            {
                text = SpecialPaths.ToDisplay(_pendingPath!);
            }

            var input = new TextBox
            {
                Style = (Style)FindResource("AddressBox"),
                Text = text,
                Margin = new Thickness(0, 0, 6, 0)
            };

            Grid.SetColumn(input, 2);
            row.Children.Add(input);
            _inputs[i] = input;

            // 1~5번에는 기본값이 무엇인지 적어 둡니다. 경로 대신 이름만 적습니다.
            var note = new TextBlock
            {
                Text = DefaultNote(i),
                Style = (Style)FindResource("PaneSubLabel"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };

            Grid.SetColumn(note, 3);
            row.Children.Add(note);

            // 글자를 앞에, 체크 상자를 뒤에 둡니다.
            var pinArea = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "체크하면 제목 표시줄에 바로가기 단추로 띄웁니다."
            };

            pinArea.Children.Add(new TextBlock
            {
                Text = "바로가기 표시",
                Foreground = (System.Windows.Media.Brush)FindResource("Text.Primary"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            });

            var pin = new CheckBox
            {
                IsChecked = current.Length > 0 && settings.IsOnTitleBar(current),
                VerticalAlignment = VerticalAlignment.Center
            };

            pinArea.Children.Add(pin);

            Grid.SetColumn(pinArea, 4);
            row.Children.Add(pinArea);
            _pins[i] = pin;

            if (_pendingPath is not null)
            {
                var place = new Button
                {
                    Content = "현재 경로",
                    ToolTip = "들고 온 폴더(지금 보고 있는 경로)를 이 칸에 넣습니다.",
                    Width = 74,
                    Height = 24,
                    Margin = new Thickness(0, 0, 6, 0)
                };

                var target = input;
                place.Click += (_, _) => target.Text = SpecialPaths.ToDisplay(_pendingPath);

                Grid.SetColumn(place, 5);
                row.Children.Add(place);
            }

            var browse = new Button
            {
                Content = "찾아보기",
                Width = 74,
                Height = 24,
                Margin = new Thickness(0, 0, 6, 0)
            };

            var browseTarget = input;
            browse.Click += (_, _) => Browse(browseTarget);

            Grid.SetColumn(browse, 6);
            row.Children.Add(browse);

            // 이 칸을 비웁니다. 바로가기 체크도 함께 풉니다. 비운 칸은 확인할 때 건너뜁니다.
            var clear = new Button
            {
                Content = "지우기",
                Width = 56,
                Height = 24,
                ToolTip = "이 칸의 주소를 지웁니다."
            };

            var clearInput = input;
            var clearPin = pin;
            clear.Click += (_, _) =>
            {
                clearInput.Text = string.Empty;
                clearPin.IsChecked = false;
            };

            Grid.SetColumn(clear, 7);
            row.Children.Add(clear);

            Rows.Items.Add(row);
        }
    }

    /// <summary>두 줄의 내용과 체크를 맞바꿉니다. 빈 줄과 바꿔도 괜찮습니다.</summary>
    private void Swap(int first, int second)
    {
        if (first < 0 || second < 0 || first >= _inputs.Length || second >= _inputs.Length)
        {
            return;
        }

        (_inputs[first].Text, _inputs[second].Text) = (_inputs[second].Text, _inputs[first].Text);
        (_pins[first].IsChecked, _pins[second].IsChecked) = (_pins[second].IsChecked, _pins[first].IsChecked);
    }

    /// <summary>1~5번을 기본값으로 되돌립니다. 제목 표시줄 체크도 켭니다. 5번부터는 건드리지 않습니다.</summary>
    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        for (var i = 0; i < AppSettings.DefaultFavoriteCount; i++)
        {
            _inputs[i].Text = SpecialPaths.ToDisplay(AppSettings.DefaultFavorites[i]);
            _pins[i].IsChecked = true;
        }
    }

    private void Browse(TextBox input)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "즐겨찾기에 넣을 폴더",
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(input.Text)
            && !SpecialPaths.IsVirtual(input.Text))
        {
            try
            {
                dialog.InitialDirectory = PathHelper.Expand(SpecialPaths.ToStored(input.Text));
            }
            catch (ArgumentException)
            {
            }
        }

        if (dialog.ShowDialog(this) == true)
        {
            input.Text = dialog.FolderName;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Result.Clear();
        TitleBar.Clear();

        for (var i = 0; i < _inputs.Length; i++)
        {
            var text = _inputs[i].Text.Trim();

            if (text.Length == 0)
            {
                continue;
            }

            // "내 PC", "바탕 화면"처럼 이름으로 적힌 것은 표시로 바꿔 적습니다.
            var stored = SpecialPaths.ToStored(text);
            Result.Add(stored);

            if (_pins[i].IsChecked == true)
            {
                TitleBar.Add(stored);
            }
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
