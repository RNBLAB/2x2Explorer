using System;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace Explorer.Shell.Theme;

/// <summary>윈도우에 설정된 색 테마를 읽습니다.</summary>
[SupportedOSPlatform("windows")]
public static class SystemThemeReader
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>
    /// 앱에 적용할 테마가 어두운 쪽인지 알려 줍니다.
    /// 윈도우는 앱 테마와 작업 표시줄 테마를 따로 두므로 앱 쪽 값을 봅니다.
    /// 값을 읽지 못하면 어두운 테마로 봅니다.
    /// </summary>
    public static bool IsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);

            if (key?.GetValue("AppsUseLightTheme") is int useLight)
            {
                return useLight == 0;
            }
        }
        catch (Exception ex) when (ex is SecurityException
                                       or UnauthorizedAccessException
                                       or System.IO.IOException)
        {
            // 정책으로 막힌 환경입니다. 기본값으로 갑니다.
        }

        return true;
    }
}
