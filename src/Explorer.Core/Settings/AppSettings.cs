using System;
using System.Collections.Generic;
using Explorer.Core.Models;
using Explorer.Core.Services;

namespace Explorer.Core.Settings;

/// <summary>
/// 설정 파일 전체입니다. %APPDATA%\2x2Explorer\settings.json 에 저장됩니다.
/// </summary>
public sealed class AppSettings
{
    /// <summary>창을 이루는 분할 패널의 개수입니다. 고정 2x2이므로 4입니다.</summary>
    public const int PaneCount = 4;

    /// <summary>지금 쓰는 설정 파일 형식의 판 번호입니다.</summary>
    public const int CurrentVersion = 8;

    /// <summary>설정 파일 형식의 판 번호입니다. 구조가 바뀌면 올리고 이전 값을 변환합니다.</summary>
    public int Version { get; set; } = CurrentVersion;

    public WindowSettings Window { get; set; } = new();

    public List<PaneSettings> Panes { get; set; } = new();

    public PreviewSettings Preview { get; set; } = new();

    public SearchSettings Search { get; set; } = new();

    public ViewSettings View { get; set; } = new();

    /// <summary>창을 몇으로 어떻게 나눌지입니다.</summary>
    public LayoutMode Layout { get; set; } = LayoutMode.Quad;

    /// <summary>위아래로 나눌 때 위쪽이 차지하는 몫입니다. 0.5면 반씩입니다.</summary>
    public double RowSplit { get; set; } = 0.5;

    /// <summary>좌우로 나눌 때 왼쪽이 차지하는 몫입니다. 0.5면 반씩입니다.</summary>
    public double ColumnSplit { get; set; } = 0.5;

    /// <summary>
    /// 칸 안에서 탐색 창이 차지하는 비율입니다. 나머지를 목록이 씁니다.
    /// 0.3 이면 탐색 창과 목록이 3 대 7 입니다.
    /// </summary>
    public double TreeSplit { get; set; } = DefaultTreeSplit;

    /// <summary>탐색 창의 기본 비율입니다. 탐색 창 3, 목록 7입니다.</summary>
    public const double DefaultTreeSplit = 0.3;

    /// <summary>나눔 비율의 기본값입니다. 되돌리기 단추가 이 값으로 맞춥니다.</summary>
    public const double DefaultSplit = 0.5;

    /// <summary>그 분할 방식에서 쓰는 창의 개수입니다.</summary>
    /// <summary>
    /// 그 칸에 탐색 창을 붙일 수 있는지입니다.
    /// 세로로 긴 칸에서만 씁니다. 납작한 칸에 붙이면 목록이 쓸 수 없을 만큼 좁아집니다.
    /// </summary>
    public static bool CanShowTree(LayoutMode mode, int index) => mode switch
    {
        LayoutMode.LeftOneRightTwo => index == 0,
        LayoutMode.LeftTwoRightOne => index == 2,
        LayoutMode.TwoColumns => index is 0 or 1,
        _ => false
    };

    /// <summary>
    /// 그 칸에서 탐색 창을 목록의 오른쪽에 붙일지입니다.
    /// 창의 오른쪽 끝에 붙은 칸이면 탐색 창도 오른쪽으로 보냅니다.
    /// 그래야 두 칸을 나란히 놓았을 때 탐색 창이 창의 바깥쪽에 서고 목록 둘이 가운데에서 맞닿습니다.
    /// </summary>
    public static bool TreeOnRight(LayoutMode mode, int index) => mode switch
    {
        LayoutMode.LeftTwoRightOne => index == 2,
        LayoutMode.TwoColumns => index == 1,
        _ => false
    };

    public static int PaneCountFor(LayoutMode mode) => mode switch
    {
        LayoutMode.Quad => 4,
        LayoutMode.TwoRows or LayoutMode.TwoColumns => 2,
        _ => 3
    };

    /// <summary>색 테마입니다. 기본은 윈도우 설정을 따릅니다.</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>즐겨찾기 가운데 기본값이 정해진 앞쪽 칸의 개수입니다.</summary>
    public const int DefaultFavoriteCount = 5;

    /// <summary>최대 즐겨찾기 개수입니다.</summary>
    public const int MaxFavorites = 14;

    /// <summary>
    /// 즐겨찾기 1~5번의 기본값입니다. 내 PC · 바탕 화면 · 다운로드 · 문서 · 휴지통입니다.
    /// 휴지통은 각 칸 도구 모음에 있던 휴지통 단추를 대신합니다.
    ///
    /// 경로가 아니라 표시로 적습니다. 실제 경로는 쓸 때마다 그 컴퓨터에 묻습니다.
    /// 그래서 사용자 이름이 다르거나 OneDrive 가 폴더를 옮겨 둔 컴퓨터에서도 제 폴더를 찾습니다.
    /// 내 PC 는 디스크 위의 폴더가 아니라 드라이브를 모아 보이는 자리라 어느 컴퓨터에서나 같습니다.
    /// </summary>
    public static IReadOnlyList<string> DefaultFavorites { get; } = new[]
    {
        SpecialPaths.MyComputer,
        SpecialPaths.Desktop,
        SpecialPaths.Downloads,
        SpecialPaths.Documents,
        SpecialPaths.RecycleBin
    };

    /// <summary>
    /// 즐겨찾기 폴더입니다. 1~5번도 고칠 수 있고, 편집 창에서 기본값으로 되돌릴 수 있습니다.
    /// 시작할 때 각 칸이 시작 위치 다음에 이 폴더들을 탭으로 엽니다.
    /// </summary>
    public List<string> Favorites { get; set; } = new(DefaultFavorites);

    /// <summary>
    /// 제목 표시줄에 단추로 띄울 즐겨찾기입니다. 차례가 아니라 경로로 기억합니다.
    /// 그래야 즐겨찾기 차례를 바꿔도 체크가 따라갑니다.
    /// 설정 파일에 이 항목이 없으면(처음 실행이거나 예전 판) 기본값 넷을 띄웁니다.
    /// </summary>
    public List<string>? TitleBarFavorites { get; set; }

    /// <summary>그 즐겨찾기를 제목 표시줄에 띄우는지입니다.</summary>
    public bool IsOnTitleBar(string favorite)
    {
        if (TitleBarFavorites is null)
        {
            return false;
        }

        var key = FavoriteKey(favorite);

        foreach (var pinned in TitleBarFavorites)
        {
            if (string.Equals(FavoriteKey(pinned), key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 즐겨찾기를 통째로 바꿉니다. 빈 칸과 같은 폴더가 두 번 든 것은 걸러 냅니다.
    /// 제목 표시줄 목록에서 즐겨찾기에 없는 것도 함께 걷어 냅니다.
    /// </summary>
    public void SetFavorites(IEnumerable<string> favorites, IEnumerable<string>? onTitleBar = null)
    {
        Favorites = CleanFavorites(favorites);

        if (onTitleBar is not null)
        {
            TitleBarFavorites = new List<string>(onTitleBar);
        }

        PruneTitleBar();
    }

    /// <summary>빈 칸과 같은 폴더가 두 번 든 것을 걸러 내고 최대 개수로 자릅니다.</summary>
    private static List<string> CleanFavorites(IEnumerable<string> source)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in source)
        {
            if (string.IsNullOrWhiteSpace(path) || !seen.Add(FavoriteKey(path)))
            {
                continue;
            }

            result.Add(SpecialPaths.ToStored(path));

            if (result.Count >= MaxFavorites)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>제목 표시줄 목록에서 즐겨찾기에 없는 것과 두 번 든 것을 걷어 냅니다.</summary>
    private void PruneTitleBar()
    {
        if (TitleBarFavorites is null)
        {
            return;
        }

        var favorites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var favorite in Favorites)
        {
            favorites.Add(FavoriteKey(favorite));
        }

        var kept = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pinned in TitleBarFavorites)
        {
            if (string.IsNullOrWhiteSpace(pinned))
            {
                continue;
            }

            var key = FavoriteKey(pinned);

            if (favorites.Contains(key) && seen.Add(key))
            {
                kept.Add(SpecialPaths.ToStored(pinned));
            }
        }

        TitleBarFavorites = kept;
    }

    /// <summary>
    /// 같은 폴더인지 견주기 위한 열쇠입니다.
    /// "::Desktop" 과 실제 바탕 화면 경로, %USERPROFILE% 처럼 풀어 써야 같은 자리인지 알 수 있는 것이 있습니다.
    /// </summary>
    private static string FavoriteKey(string path)
    {
        var trimmed = path.Trim();

        if (SpecialPaths.IsMyComputer(trimmed))
        {
            return SpecialPaths.MyComputer;
        }

        if (SpecialPaths.IsRecycleBin(trimmed))
        {
            return SpecialPaths.RecycleBin;
        }

        try
        {
            trimmed = PathHelper.Expand(trimmed);
        }
        catch (ArgumentException)
        {
        }

        return trimmed.TrimEnd('\\', '/');
    }

    /// <summary>예전 판이 적던 %USERPROFILE% 경로를 기본 폴더 표시로 바꿉니다. 다른 경로는 그대로 둡니다.</summary>
    private static string FromLegacyPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path ?? string.Empty;
        }

        var trimmed = path.Trim().TrimEnd('\\');

        return trimmed.ToUpperInvariant() switch
        {
            @"%USERPROFILE%\DESKTOP" => SpecialPaths.Desktop,
            @"%USERPROFILE%\DOWNLOADS" => SpecialPaths.Downloads,
            @"%USERPROFILE%\DOCUMENTS" => SpecialPaths.Documents,
            _ => path
        };
    }

    /// <summary>처음 실행할 때 쓰는 기본 설정입니다.</summary>
    public static AppSettings CreateDefault()
    {
        var settings = new AppSettings { Panes = CreateDefaultPanes() };
        return settings;
    }

    private static List<PaneSettings> CreateDefaultPanes() => new()
    {
        new PaneSettings { Index = 0, Role = PaneRole.Files, StartPath = SpecialPaths.MyComputer },
        new PaneSettings { Index = 1, Role = PaneRole.Files, StartPath = SpecialPaths.Documents },
        new PaneSettings { Index = 2, Role = PaneRole.Files, StartPath = SpecialPaths.Downloads },
        new PaneSettings { Index = 3, Role = PaneRole.Files, StartPath = SpecialPaths.Desktop }
    };

    /// <summary>
    /// 사람이 손댔거나 깨진 설정 파일을 읽어도 프로그램이 뜨도록 값을 다듬습니다.
    /// 패널은 항상 4개, 미리보기 패널은 항상 하나가 되도록 맞춥니다.
    /// </summary>
    /// <summary>
    /// 이미 저장된 설정 파일을 새 판으로 옮깁니다.
    /// 기본값만 고치면 파일이 이미 있는 사용자에게는 반영되지 않습니다.
    /// </summary>
    private void Migrate()
    {
        if (Version < 2)
        {
            // 미리보기 대기 시간을 200ms에서 100ms로 줄였습니다.
            if (Preview.DebounceMs == 200)
            {
                Preview.DebounceMs = 100;
            }
        }

        if (Version < 3)
        {
            // 좌상단 패널의 기본 시작 위치를 바탕화면에서 "내 PC"로 바꿨습니다.
            // 직접 고른 위치는 건드리지 않습니다.
            var first = Panes.Find(pane => pane.Index == 0);

            if (first is not null
                && string.Equals(first.StartPath, @"%USERPROFILE%\Desktop", StringComparison.OrdinalIgnoreCase))
            {
                first.StartPath = SpecialPaths.MyComputer;
            }
        }

        if (Version < 4)
        {
            // 오른쪽 아래도 탐색기가 됐습니다. 비어 있던 시작 위치를 채웁니다.
            var last = Panes.Find(pane => pane.Index == PaneCount - 1);

            if (last is not null && string.IsNullOrWhiteSpace(last.StartPath))
            {
                last.StartPath = @"%USERPROFILE%\Desktop";
            }
        }

        if (Version < 6)
        {
            // 폴더 감시가 깨어나기까지 기다리는 시간을 0.2초에서 0.6초로 늘렸습니다.
            // 다른 프로그램이 파일을 쏟아 낼 때 목록을 여러 번 갈아 끼우면 화면이 자꾸 멈춥니다.
            if (View.WatchDebounceMs == 200)
            {
                View.WatchDebounceMs = 600;
            }
        }

        if (Version < 7)
        {
            // 바탕 화면·다운로드·문서를 %USERPROFILE% 경로 대신 표시로 적습니다.
            // 경로로 적어 두면 OneDrive 가 옮겨 둔 폴더나 다른 컴퓨터에서 엉뚱한 자리를 가리킵니다.
            Favorites ??= new List<string>();

            for (var index = 0; index < Favorites.Count; index++)
            {
                Favorites[index] = FromLegacyPath(Favorites[index]);
            }

            foreach (var pane in Panes)
            {
                pane.StartPath = FromLegacyPath(pane.StartPath);
            }

            // 예전에는 1~4번이 고정이었고 제목 표시줄 단추는 없었습니다. 고정이던 넷을 띄워 둡니다.
            TitleBarFavorites ??= new List<string>(DefaultFavorites);
        }

        if (Version < 8)
        {
            // 도구 모음의 휴지통 단추를 없애고 즐겨찾기 5번 기본값으로 옮겼습니다.
            // 이미 쓰던 설정에도 휴지통이 없으면 5번 자리에 끼워 넣고 제목 띠 단추로 띄웁니다.
            Favorites ??= new List<string>();

            var hasBin = false;

            foreach (var favorite in Favorites)
            {
                if (SpecialPaths.IsRecycleBin(favorite))
                {
                    hasBin = true;
                    break;
                }
            }

            if (!hasBin && Favorites.Count < MaxFavorites)
            {
                Favorites.Insert(Math.Min(DefaultFavoriteCount - 1, Favorites.Count), SpecialPaths.RecycleBin);

                if (TitleBarFavorites is not null && !TitleBarFavorites.Contains(SpecialPaths.RecycleBin))
                {
                    TitleBarFavorites.Add(SpecialPaths.RecycleBin);
                }
            }
        }

        if (Version < 5)
        {
            // 탐색 창의 기본 너비를 4 대 6 에서 3 대 7 로 줄였습니다.
            // 직접 끌어 맞춘 너비는 건드리지 않습니다.
            if (Math.Abs(TreeSplit - 0.4) < 0.005)
            {
                TreeSplit = DefaultTreeSplit;
            }
        }

        Version = CurrentVersion;
    }

    public void Normalize()
    {
        Window ??= new WindowSettings();
        Preview ??= new PreviewSettings();
        Search ??= new SearchSettings();
        View ??= new ViewSettings();
        View.DetailColumns = ColumnSettings.Normalize(View.DetailColumns);
        Panes ??= new List<PaneSettings>();

        var defaults = CreateDefaultPanes();
        var slots = new PaneSettings?[PaneCount];

        foreach (var pane in Panes)
        {
            if (pane is null || pane.Index < 0 || pane.Index >= PaneCount)
            {
                continue;
            }

            slots[pane.Index] ??= pane;
        }

        for (var i = 0; i < PaneCount; i++)
        {
            var pane = slots[i] ?? defaults[i];
            pane.Index = i;
            pane.LastTabs ??= new List<string>();
            pane.LastTabColors ??= new List<int>();
            pane.StartPath ??= string.Empty;
            pane.IconSize = pane.IconSize <= 0 ? 96 : Math.Clamp(pane.IconSize, 32, 256);
            slots[i] = pane;
        }

        var normalized = new List<PaneSettings>(PaneCount);
        for (var i = 0; i < PaneCount; i++)
        {
            normalized.Add(slots[i]!);
        }

        Panes = normalized;

        // 보이는 칸이 모두 미리보기이면 할 수 있는 일이 없습니다. 첫 칸은 탐색기로 둡니다.
        var visible = PaneCountFor(Layout);
        var hasExplorer = false;

        for (var i = 0; i < visible && i < Panes.Count; i++)
        {
            if (Panes[i].Role == PaneRole.Files)
            {
                hasExplorer = true;
                break;
            }
        }

        if (!hasExplorer && Panes.Count > 0)
        {
            Panes[0].Role = PaneRole.Files;
        }

        Migrate();

        Preview.DebounceMs = Math.Clamp(Preview.DebounceMs, 0, 3000);

        Favorites ??= new List<string>();
        Favorites = CleanFavorites(Favorites);

        // 처음 실행이면 기본값 다섯을 제목 표시줄에 띄웁니다.
        TitleBarFavorites ??= new List<string>(DefaultFavorites);
        PruneTitleBar();

        if (Preview.MaxTextPreviewBytes <= 0)
        {
            Preview.MaxTextPreviewBytes = 2L * 1024 * 1024;
        }

        Search.MaxResults = Math.Clamp(Search.MaxResults, 100, 200_000);
        Search.DescribeChunk = Math.Clamp(Search.DescribeChunk, 200, 50_000);

        Window.Width = Math.Max(Window.Width, 800);
        Window.Height = Math.Max(Window.Height, 600);

        View.ThumbnailSize = Math.Clamp(View.ThumbnailSize, 16, 256);
        View.LargeThumbnailSize = Math.Clamp(View.LargeThumbnailSize, 32, 512);
        View.ThumbnailCacheCapacity = Math.Clamp(View.ThumbnailCacheCapacity, 100, 50_000);
        // 느린 파일 하나에 막히지 않도록 최소 네 갈래는 두고 돌립니다.
        View.ThumbnailConcurrency = Math.Clamp(View.ThumbnailConcurrency, 4, 8);
        View.WatchDebounceMs = Math.Clamp(View.WatchDebounceMs, 50, 5000);

        // 한쪽이 너무 얇아지면 쓸 수 없습니다.
        RowSplit = Math.Clamp(double.IsNaN(RowSplit) ? DefaultSplit : RowSplit, 0.15, 0.85);
        ColumnSplit = Math.Clamp(double.IsNaN(ColumnSplit) ? DefaultSplit : ColumnSplit, 0.15, 0.85);
        TreeSplit = Math.Clamp(double.IsNaN(TreeSplit) ? DefaultTreeSplit : TreeSplit, 0.15, 0.7);
    }
}
