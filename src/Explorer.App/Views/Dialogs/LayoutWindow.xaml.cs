using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Explorer.Core.Models;
using Explorer.Core.Settings;

namespace Explorer.App.Views.Dialogs;

/// <summary>
/// 설정 창입니다. 갈피 셋으로 나뉩니다.
/// 창을 몇으로 나눌지와 칸마다의 역할, 창 전체에 걸리는 값들, 검색, 그리고 단축키 목록입니다.
/// 확인을 눌러야 설정에 옮겨 적습니다. 취소하면 아무것도 바뀌지 않습니다.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class LayoutWindow : Window
{
    /// <summary>한 칸이 격자에서 차지하는 자리입니다.</summary>
    private readonly record struct Cell(int Column, int Row, int ColumnSpan, int RowSpan);

    private static readonly (LayoutMode Mode, string Label)[] Choices =
    {
        (LayoutMode.Quad, "4분할"),
        (LayoutMode.TopOneBottomTwo, "3분할 (상단1, 하단2)"),
        (LayoutMode.TopTwoBottomOne, "3분할 (상단2, 하단1)"),
        (LayoutMode.LeftOneRightTwo, "3분할 (좌측1, 우측2)"),
        (LayoutMode.LeftTwoRightOne, "3분할 (좌측2, 우측1)"),
        (LayoutMode.TwoRows, "2분할 (가로)"),
        (LayoutMode.TwoColumns, "2분할 (세로)")
    };

    private readonly AppSettings _settings;
    private readonly List<RadioButton> _layoutButtons = new();
    private readonly List<ComboBox> _roleBoxes = new();
    private readonly List<CheckBox> _previewBoxes = new();
    private readonly List<CheckBox> _treeBoxes = new();

    private LayoutMode _chosen;

    public LayoutWindow(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _chosen = settings.Layout;

        InitializeComponent();

        BuildLayoutChoices();
        BuildPaneChoices();
        BuildShortcuts();
        LoadGeneral();
        Refresh();
    }


    /// <summary>
    /// 단축키 목록입니다. 묶음 제목과 줄들을 코드에서 만듭니다.
    /// XAML에 줄마다 적으면 글쇠 하나 늘 때마다 여는 태그 넷이 따라 늘어납니다.
    /// </summary>
    private static readonly (string Group, (string Keys, string What)[] Rows)[] ShortcutGroups =
    {
        ("파일 목록", new[]
        {
            ("Enter", "고른 것을 엽니다"),
            ("Alt+Enter", "속성 창을 엽니다"),
            ("Backspace", "위 폴더로 갑니다"),
            ("마우스 옆 단추", "뒤로 · 앞으로"),
            ("F2", "이름을 바꿉니다"),
            ("F5", "목록과 탐색 창을 다시 읽습니다"),
            ("Ctrl+L", "주소 표시줄로 갑니다"),
            ("Ctrl+F", "검색합니다"),
            ("Esc", "검색을 끝내고 폴더로 돌아갑니다")
        }),
        ("고치기", new[]
        {
            ("Ctrl+C", "복사"),
            ("Ctrl+X", "잘라내기"),
            ("Ctrl+V", "붙여넣기"),
            ("Ctrl+Z", "되돌리기 (삭제 · 옮기기 · 이름 바꾸기)"),
            ("Delete", "휴지통으로 보냅니다"),
            ("Shift+Delete", "휴지통을 거치지 않고 지웁니다"),
            ("Ctrl+Shift+R", "일괄 이름 바꾸기(베타) 창 — 설정 [일반]에서 켰을 때"),
            ("Ctrl+P", "일괄 인쇄 창")
        }),
        ("보기", new[]
        {
            ("Ctrl+휠", "큰 아이콘 보기에서 아이콘 크기를 바꿉니다"),
            ("Ctrl+휠", "미리보기 칸에서 확대 · 축소합니다")
        }),
        ("끌어다 놓기", new[]
        {
            ("그냥 끌기", "같은 드라이브 안에서는 옮기고, 드라이브를 넘으면 복사합니다"),
            ("Ctrl+끌기", "복사합니다"),
            ("Shift+끌기", "옮깁니다"),
            ("Alt+끌기 · Ctrl+Shift+끌기", "바로가기를 만듭니다"),
            ("오른쪽 단추로 끌기", "놓을 때 무엇을 할지 메뉴로 고릅니다")
        })
    };

    private void BuildShortcuts()
    {
        foreach (var (group, rows) in ShortcutGroups)
        {
            var title = new TextBlock
            {
                Text = group,
                Foreground = (Brush)FindResource("Text.Primary"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 12, 0, 6)
            };

            Shortcuts.Children.Add(title);

            foreach (var (keys, what) in rows)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var key = new TextBlock
                {
                    Text = keys,
                    Foreground = (Brush)FindResource("Text.Primary"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 10, 0)
                };

                Grid.SetColumn(key, 0);
                row.Children.Add(key);

                var text = new TextBlock
                {
                    Text = what,
                    Style = (Style)FindResource("PaneSubLabel"),
                    TextWrapping = TextWrapping.Wrap
                };

                Grid.SetColumn(text, 1);
                row.Children.Add(text);

                Shortcuts.Children.Add(row);
            }
        }
    }

    /// <summary>일반 칸의 값을 지금 설정대로 맞춥니다.</summary>
    private void LoadGeneral()
    {
        ThemeBox.SelectedIndex = _settings.Theme switch
        {
            AppTheme.Light => 1,
            AppTheme.Dark => 2,
            _ => 0
        };

        IconSizeBox.SelectedIndex = _settings.View.ThumbnailSize switch
        {
            <= 16 => 0,
            <= 20 => 1,
            <= 24 => 2,
            _ => 3
        };

        PreviewDelayBox.SelectedIndex = _settings.Preview.DebounceMs switch
        {
            <= 0 => 0,
            <= 100 => 1,
            <= 250 => 2,
            _ => 3
        };

        SearchLimitBox.Text = _settings.Search.MaxResults.ToString(CultureInfo.InvariantCulture);
        ChunkBox.Text = _settings.Search.DescribeChunk.ToString(CultureInfo.InvariantCulture);
        NetworkBox.IsChecked = _settings.Search.IncludeNetworkDrives;
        FallbackBox.IsChecked = _settings.Search.FallbackToEnumerate;
        StatusBarBox.IsChecked = _settings.View.ShowStatusBar;
        LogBox.IsChecked = _settings.View.WriteDiagnosticLog;
        BatchRenameBox.IsChecked = _settings.View.ShowBatchRename;
        ShowFavorites();
    }

    // ── 즐겨찾기 ──

    /// <summary>즐겨찾기 칸에 지금 목록을 보입니다. 제목 표시줄에 띄우는 것에는 표시를 붙입니다.</summary>
    private void ShowFavorites()
    {
        FavoritesList.Children.Clear();

        var favorites = _settings.Favorites;
        var pinned = 0;

        for (var i = 0; i < favorites.Count; i++)
        {
            var path = favorites[i];
            var onTitleBar = _settings.IsOnTitleBar(path);

            if (onTitleBar)
            {
                pinned++;
            }

            var line = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };

            line.Children.Add(new TextBlock
            {
                Text = $"{i + 1}.",
                Width = 28,
                Foreground = (Brush)FindResource("Text.Secondary")
            });

            var label = new TextBlock
            {
                Text = path + (onTitleBar ? "   · 제목 표시줄" : string.Empty),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = path,
                Foreground = (Brush)FindResource("Text.Primary")
            };

            line.Children.Add(label);
            FavoritesList.Children.Add(line);
        }

        FavoritesSummary.Text = favorites.Count == 0
            ? "등록된 즐겨찾기가 없습니다."
            : $"{favorites.Count} / {AppSettings.MaxFavorites}개 · 제목 표시줄 {pinned}개";
    }

    /// <summary>
    /// 즐겨찾기 편집 창을 띄웁니다. 확인하면 설정 창의 확인·취소와 상관없이 곧바로 저장하고
    /// 네 칸의 즐겨찾기 줄과 제목 표시줄 단추를 다시 세웁니다(메인 창 톱니 메뉴의 편집과 같은 길).
    /// </summary>
    private void OnEditFavorites(object sender, RoutedEventArgs e)
    {
        var dialog = new FavoritesWindow(_settings) { Owner = this };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _settings.SetFavorites(dialog.Result, dialog.TitleBar);

        var host = global::Explorer.App.App.Host;
        host.SettingsService.Save(_settings);
        host.NotifyFavoritesChanged();

        ShowFavorites();
    }

    /// <summary>일반 칸의 값을 설정에 옮겨 적습니다. 숫자가 이상하면 false 를 돌려줍니다.</summary>
    private bool SaveGeneral()
    {
        if (!int.TryParse(SearchLimitBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit)
            || limit is < 100 or > 200_000)
        {
            MessageBox.Show(
                this,
                "검색 결과 상한은 100에서 200000 사이의 수여야 합니다.",
                "설정",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            SearchLimitBox.Focus();
            SearchLimitBox.SelectAll();
            return false;
        }

        if (!int.TryParse(ChunkBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var chunk)
            || chunk is < 200 or > 50_000)
        {
            MessageBox.Show(
                this,
                "한 번에 읽을 건수는 200에서 50000 사이의 수여야 합니다.",
                "설정",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            ChunkBox.Focus();
            ChunkBox.SelectAll();
            return false;
        }

        _settings.Theme = ThemeBox.SelectedIndex switch
        {
            1 => AppTheme.Light,
            2 => AppTheme.Dark,
            _ => AppTheme.System
        };

        _settings.View.ThumbnailSize = IconSizeBox.SelectedIndex switch
        {
            0 => 16,
            1 => 20,
            2 => 24,
            _ => 32
        };

        _settings.Preview.DebounceMs = PreviewDelayBox.SelectedIndex switch
        {
            0 => 0,
            1 => 100,
            2 => 250,
            _ => 500
        };

        _settings.Search.MaxResults = limit;
        _settings.Search.DescribeChunk = chunk;
        _settings.Search.IncludeNetworkDrives = NetworkBox.IsChecked == true;
        _settings.Search.FallbackToEnumerate = FallbackBox.IsChecked == true;
        _settings.View.ShowStatusBar = StatusBarBox.IsChecked == true;
        _settings.View.WriteDiagnosticLog = LogBox.IsChecked == true;
        _settings.View.ShowBatchRename = BatchRenameBox.IsChecked == true;
        return true;
    }

    /// <summary>확인을 누르면 true입니다. 나눔 비율을 되돌렸으면 여기에도 담깁니다.</summary>
    public bool SplitsReset { get; private set; }

    private void BuildLayoutChoices()
    {
        foreach (var (mode, label) in Choices)
        {
            var button = new RadioButton
            {
                Content = label,
                Tag = mode,
                Margin = new Thickness(0, 0, 0, 8),
                IsChecked = mode == _chosen,
                Foreground = (Brush)FindResource("Text.Primary")
            };

            button.Checked += (_, _) =>
            {
                _chosen = mode;
                Refresh();
            };

            _layoutButtons.Add(button);
            LayoutChoices.Children.Add(button);
        }
    }

    private void BuildPaneChoices()
    {
        for (var index = 0; index < AppSettings.PaneCount; index++)
        {
            var pane = _settings.Panes[index];

            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = false };

            var label = new TextBlock
            {
                Text = $"{index + 1}번 칸",
                Width = 64,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("Text.Primary")
            };

            DockPanel.SetDock(label, Dock.Left);
            row.Children.Add(label);

            var role = new ComboBox
            {
                Width = 140,
                VerticalAlignment = VerticalAlignment.Center,
                ItemsSource = new[] { "탐색기", "검색기", "미리보기" },
                SelectedIndex = pane.Role switch
                {
                    PaneRole.Search => 1,
                    PaneRole.Preview => 2,
                    _ => 0
                }
            };

            role.SelectionChanged += (_, _) => Refresh();
            DockPanel.SetDock(role, Dock.Left);
            row.Children.Add(role);
            _roleBoxes.Add(role);

            var preview = new CheckBox
            {
                Content = "미리보기 표시",
                Margin = new Thickness(14, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = pane.ShowPreview,
                Foreground = (Brush)FindResource("Text.Primary")
            };

            DockPanel.SetDock(preview, Dock.Left);
            row.Children.Add(preview);
            _previewBoxes.Add(preview);

            var tree = new CheckBox
            {
                Content = "탐색 창 표시",
                Margin = new Thickness(14, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = pane.ShowTree,
                Foreground = (Brush)FindResource("Text.Primary")
            };

            DockPanel.SetDock(tree, Dock.Left);
            row.Children.Add(tree);
            _treeBoxes.Add(tree);

            PaneChoices.Children.Add(row);
        }
    }

    /// <summary>고른 모양을 그림으로 보여 주고, 쓰지 않는 칸의 설정은 흐리게 둡니다.</summary>
    private void Refresh()
    {
        var count = AppSettings.PaneCountFor(_chosen);

        for (var index = 0; index < _roleBoxes.Count; index++)
        {
            var inUse = index < count;

            _roleBoxes[index].IsEnabled = inUse;

            // 미리보기 전용 칸에는 옆에 붙이는 미리보기도, 탐색 창도 없습니다.
            var isExplorer = inUse && _roleBoxes[index].SelectedIndex == 0;

            _previewBoxes[index].IsEnabled = isExplorer;
            _previewBoxes[index].Opacity = isExplorer ? 1 : 0.4;

            // 탐색 창은 세로로 긴 칸에만 붙습니다.
            var canTree = isExplorer && AppSettings.CanShowTree(_chosen, index);

            _treeBoxes[index].IsEnabled = canTree;
            _treeBoxes[index].Opacity = canTree ? 1 : 0.4;
            _treeBoxes[index].ToolTip = canTree
                ? "이 칸 왼쪽에 폴더를 훑는 탐색 창을 붙입니다."
                : "탐색 창은 세로로 긴 칸에만 붙일 수 있습니다. 3분할의 긴 쪽이나 2분할(세로)에서 쓰십시오.";
            ((UIElement)PaneChoices.Children[index]).Opacity = inUse ? 1 : 0.4;
        }

        DrawShape(count);
    }

    private void DrawShape(int count)
    {
        Shape.Children.Clear();
        Shape.RowDefinitions.Clear();
        Shape.ColumnDefinitions.Clear();

        Shape.RowDefinitions.Add(new RowDefinition());
        Shape.RowDefinitions.Add(new RowDefinition());
        Shape.ColumnDefinitions.Add(new ColumnDefinition());
        Shape.ColumnDefinitions.Add(new ColumnDefinition());

        var cells = CellsFor(_chosen);

        for (var index = 0; index < cells.Length; index++)
        {
            var preview = _roleBoxes.Count > index && _roleBoxes[index].SelectedIndex == 1;

            var box = new Border
            {
                Margin = new Thickness(3),
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)FindResource("Pane.Border"),
                Background = (Brush)FindResource(preview ? "Row.Selected" : "List.Background"),
                Child = new TextBlock
                {
                    Text = preview ? $"{index + 1} 미리보기" : $"{index + 1} 탐색기",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 11,
                    Foreground = (Brush)FindResource("Text.Secondary")
                }
            };

            Grid.SetColumn(box, cells[index].Column);
            Grid.SetRow(box, cells[index].Row);
            Grid.SetColumnSpan(box, cells[index].ColumnSpan);
            Grid.SetRowSpan(box, cells[index].RowSpan);
            Shape.Children.Add(box);
        }
    }

    private static Cell[] CellsFor(LayoutMode mode) => mode switch
    {
        LayoutMode.TopOneBottomTwo => new[]
        {
            new Cell(0, 0, 2, 1), new Cell(0, 1, 1, 1), new Cell(1, 1, 1, 1)
        },
        LayoutMode.TopTwoBottomOne => new[]
        {
            new Cell(0, 0, 1, 1), new Cell(1, 0, 1, 1), new Cell(0, 1, 2, 1)
        },
        LayoutMode.LeftOneRightTwo => new[]
        {
            new Cell(0, 0, 1, 2), new Cell(1, 0, 1, 1), new Cell(1, 1, 1, 1)
        },
        LayoutMode.LeftTwoRightOne => new[]
        {
            new Cell(0, 0, 1, 1), new Cell(0, 1, 1, 1), new Cell(1, 0, 1, 2)
        },
        LayoutMode.TwoRows => new[]
        {
            new Cell(0, 0, 2, 1), new Cell(0, 1, 2, 1)
        },
        LayoutMode.TwoColumns => new[]
        {
            new Cell(0, 0, 1, 2), new Cell(1, 0, 1, 2)
        },
        _ => new[]
        {
            new Cell(0, 0, 1, 1), new Cell(1, 0, 1, 1), new Cell(0, 1, 1, 1), new Cell(1, 1, 1, 1)
        }
    };

    private void OnResetSplits(object sender, RoutedEventArgs e)
    {
        SplitsReset = true;
        _settings.RowSplit = AppSettings.DefaultSplit;
        _settings.ColumnSplit = AppSettings.DefaultSplit;
        _settings.TreeSplit = AppSettings.DefaultTreeSplit;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        var count = AppSettings.PaneCountFor(_chosen);
        var hasExplorer = false;

        for (var index = 0; index < count; index++)
        {
            if (_roleBoxes[index].SelectedIndex == 0)
            {
                hasExplorer = true;
                break;
            }
        }

        if (!hasExplorer)
        {
            MessageBox.Show(
                this,
                "탐색기로 쓰는 칸이 하나는 있어야 합니다.",
                "설정",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!SaveGeneral())
        {
            return;
        }

        _settings.Layout = _chosen;

        for (var index = 0; index < AppSettings.PaneCount; index++)
        {
            _settings.Panes[index].Role = _roleBoxes[index].SelectedIndex switch
            {
                1 => PaneRole.Search,
                2 => PaneRole.Preview,
                _ => PaneRole.Files
            };

            _settings.Panes[index].ShowPreview = _previewBoxes[index].IsChecked == true;
            _settings.Panes[index].ShowTree = _treeBoxes[index].IsChecked == true;
        }

        DialogResult = true;
    }
}
