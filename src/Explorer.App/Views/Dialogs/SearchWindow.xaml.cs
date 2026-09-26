using System;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Input;
using Explorer.App.ViewModels;

namespace Explorer.App.Views.Dialogs;

/// <summary>
/// 검색 창입니다. 탭을 차지하지 않고 따로 떠서 검색만 합니다.
///
/// 크기는 창을 넷으로 나눈 한 칸만 하고, 자리는 부모 창의 한가운데입니다.
/// 안에 든 것은 탐색기 목록 그대로라, 찾은 것을 열거나 오른쪽 단추 메뉴를 그대로 쓸 수 있습니다.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class SearchWindow : Window
{
    private readonly FileListViewModel _list;

    /// <param name="owner">이 창을 연 칸입니다. 목록이 이 칸의 보기 설정을 따릅니다.</param>
    /// <param name="startFolder">Everything 이 꺼져 있을 때 훑을 폴더입니다.</param>
    public SearchWindow(PaneViewModel owner, string? startFolder)
    {
        ArgumentNullException.ThrowIfNull(owner);

        InitializeComponent();

        DataContext = owner;

        // 찾기만 하는 창입니다. 새 폴더나 휴지통 같은 단추는 둘 자리가 아닙니다.
        Results.CompactTools = true;

        _list = owner.CreateDetachedList();
        Results.DataContext = _list;

        Loaded += (_, _) => Start(startFolder);
    }

    /// <summary>부모 창을 넷으로 나눈 한 칸만 하게 잡습니다.</summary>
    public void SizeToQuarter()
    {
        var width = Owner?.ActualWidth ?? SystemParameters.WorkArea.Width;
        var height = Owner?.ActualHeight ?? SystemParameters.WorkArea.Height;

        Width = Math.Max(width / 2, 520);
        Height = Math.Max(height / 2, 360);

        if (Owner is null)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    private async void Start(string? startFolder)
    {
        // Everything 이 꺼져 있으면 이 폴더 아래를 훑습니다. 열어 두지 않으면 훑을 자리가 없습니다.
        if (!string.IsNullOrWhiteSpace(startFolder))
        {
            await _list.NavigateAsync(startFolder, recordHistory: false).ConfigureAwait(true);
        }

        _list.BeginSearch();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        // 검색어를 지우는 Esc 와 창을 닫는 Esc 가 겹칩니다. 검색 중이면 목록이 먼저 씁니다.
        if (_list.IsSearching)
        {
            return;
        }

        e.Handled = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _list.Dispose();
        base.OnClosed(e);
    }
}
