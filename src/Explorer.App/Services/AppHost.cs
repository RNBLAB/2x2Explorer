using System;
using System.Runtime.Versioning;
using Explorer.Core.Abstractions;
using Explorer.Core.Services;
using Explorer.Core.Settings;

namespace Explorer.App.Services;

/// <summary>
/// 앱 수명 동안 살아 있는 서비스와 설정을 담습니다.
/// 썸네일 캐시는 패널 네 개가 함께 씁니다. 패널마다 따로 두면 같은 파일을 네 번 만듭니다.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AppHost : IDisposable
{
    private AppHost(ISettingsService settingsService, AppSettings settings)
    {
        SettingsService = settingsService;
        Settings = settings;
        // 드라이브 이름은 셸에게 묻습니다. Core 혼자서는 "새 볼륨 (X:)" 같은 이름밖에 못 만듭니다.
        Explorer.Core.Services.DirectoryEnumerator.ShellDisplayName =
            Explorer.Shell.Interop.ShellNames.GetDisplayName;
        DirectoryEnumerator = new DirectoryEnumerator();
        Thumbnails = new ThumbnailCache(
            settings.View.ThumbnailCacheCapacity,
            settings.View.ThumbnailConcurrency);
        Search = new EverythingSearchService();
        Undo = new UndoHistory();
    }

    /// <summary>
    /// 즐겨찾기 목록이 바뀌면 발생합니다. 네 칸의 탐색 창이 이 신호를 듣고 맨 위 줄을 다시 세웁니다.
    /// </summary>
    public event EventHandler? FavoritesChanged;

    /// <summary>즐겨찾기를 고쳤다고 알립니다.</summary>
    public void NotifyFavoritesChanged() => FavoritesChanged?.Invoke(this, EventArgs.Empty);

    public ISettingsService SettingsService { get; }

    /// <summary>실행 중에 값이 바뀌는 설정 인스턴스입니다. 종료할 때 그대로 저장합니다.</summary>
    public AppSettings Settings { get; }

    public IDirectoryEnumerator DirectoryEnumerator { get; }

    public ThumbnailCache Thumbnails { get; }

    /// <summary>Everything 검색입니다. 네 패널이 함께 씁니다.</summary>
    public EverythingSearchService Search { get; }

    /// <summary>되돌리기 기록입니다. 어느 칸에서 한 일이든 한 줄로 쌓입니다.</summary>
    public UndoHistory Undo { get; }

    public static AppHost Create()
    {
        var settingsService = new JsonSettingsService();
        var settings = settingsService.Load();

        // 평소에는 기록을 남기지 않습니다. 설정에서 켤 때만 적습니다.
        DiagnosticLog.Enabled = settings.View.WriteDiagnosticLog;

        return new AppHost(settingsService, settings);
    }

    public void Dispose() => Thumbnails.Dispose();
}
