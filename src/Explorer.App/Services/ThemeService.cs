using System;
using System.Runtime.Versioning;
using System.Windows;
using Explorer.Core.Models;
using Explorer.Shell.Theme;
using Microsoft.Win32;

namespace Explorer.App.Services;

/// <summary>
/// 색 테마를 갈아 끼웁니다.
/// 모든 색은 DynamicResource로 참조하므로, 앱 리소스의 첫 사전만 바꾸면 화면 전체가 따라옵니다.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ThemeService : IDisposable
{
    private static readonly Uri DarkTheme =
        new("pack://application:,,,/Resources/Themes/Dark.xaml", UriKind.Absolute);

    private static readonly Uri LightTheme =
        new("pack://application:,,,/Resources/Themes/Light.xaml", UriKind.Absolute);

    private readonly Application _application;
    private AppTheme _configured = AppTheme.System;
    private bool _listening;
    private bool _disposed;

    public ThemeService(Application application)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
    }

    /// <summary>지금 실제로 적용된 것이 어두운 테마인지 여부입니다.</summary>
    public bool IsDark { get; private set; }

    public void Apply(AppTheme theme)
    {
        _configured = theme;

        if (theme == AppTheme.System && !_listening)
        {
            // 사용자가 윈도우 설정에서 테마를 바꾸면 바로 따라갑니다.
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _listening = true;
        }

        ApplyEffective();
    }

    private void ApplyEffective()
    {
        IsDark = _configured switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => SystemThemeReader.IsDarkMode()
        };

        var dictionary = new ResourceDictionary { Source = IsDark ? DarkTheme : LightTheme };
        var merged = _application.Resources.MergedDictionaries;

        // 첫 자리가 팔레트입니다. 뒤에 오는 Common.xaml은 이 키들을 참조만 합니다.
        if (merged.Count == 0)
        {
            merged.Add(dictionary);
        }
        else
        {
            merged[0] = dictionary;
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General)
        {
            return;
        }

        // 이 신호는 UI 스레드가 아닌 곳에서 올 수 있습니다.
        _application.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_disposed)
            {
                ApplyEffective();
            }
        }));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_listening)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _listening = false;
        }
    }
}
