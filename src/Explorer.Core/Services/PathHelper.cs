using System;
using System.IO;

namespace Explorer.Core.Services;

/// <summary>
/// 설정에 적힌 경로를 실제로 열 수 있는 경로로 바꿔 줍니다.
/// </summary>
public static class PathHelper
{
    /// <summary>사용자 프로필 폴더입니다. 어떤 경로도 못 쓸 때의 마지막 대안입니다.</summary>
    public static string DefaultFallback =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>%USERPROFILE% 같은 환경 변수와 기본 폴더 표시를 실제 경로로 바꿉니다.</summary>
    public static string Expand(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return string.Empty;
        }

        // 바탕 화면·다운로드·문서 표시는 이 컴퓨터의 실제 경로로 바꿉니다.
        if (SpecialPaths.ResolveKnownFolder(rawPath) is { } known)
        {
            return known;
        }

        return Environment.ExpandEnvironmentVariables(rawPath.Trim());
    }

    /// <summary>
    /// 경로를 펼친 뒤 실제로 존재하는 폴더를 찾아 돌려줍니다.
    /// 폴더가 사라졌으면 존재하는 상위 폴더까지 거슬러 올라가고,
    /// 그래도 없으면 <paramref name="fallback"/>을 씁니다.
    /// 시작 위치를 이동식 디스크나 네트워크 드라이브로 잡았을 때 이 경로를 탑니다.
    /// </summary>
    public static string ResolveExistingDirectory(string? rawPath, string? fallback = null)
    {
        if (SpecialPaths.IsMyComputer(rawPath))
        {
            return SpecialPaths.MyComputer;
        }

        if (SpecialPaths.IsRecycleBin(rawPath))
        {
            return SpecialPaths.RecycleBin;
        }

        var safeFallback = string.IsNullOrWhiteSpace(fallback) ? DefaultFallback : fallback;
        var expanded = Expand(rawPath);

        if (expanded.Length == 0)
        {
            return safeFallback;
        }

        try
        {
            var current = Path.GetFullPath(expanded);

            while (!string.IsNullOrEmpty(current))
            {
                if (Directory.Exists(current))
                {
                    return current;
                }

                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                current = parent;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            // 경로 문자열 자체가 잘못된 경우입니다.
        }

        return safeFallback;
    }
}
