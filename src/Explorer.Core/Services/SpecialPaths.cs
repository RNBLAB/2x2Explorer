using System;

namespace Explorer.Core.Services;

/// <summary>
/// 파일 시스템 경로가 아닌 특수한 위치입니다.
/// "내 PC"는 디스크 위의 폴더가 아니라 드라이브를 모아 보여 주는 자리이므로,
/// 경로 대신 이 표시를 쓰고 열거할 때만 드라이브 목록으로 바꿉니다.
/// </summary>
public static class SpecialPaths
{
    /// <summary>설정 파일과 내부에서 쓰는 표시입니다.</summary>
    public const string MyComputer = "::MyComputer";

    /// <summary>화면에 보여 주는 이름입니다. 주소 표시줄에 이대로 쳐도 열립니다.</summary>
    public const string MyComputerDisplayName = "내 PC";

    /// <summary>휴지통을 가리키는 표시입니다. 이것도 디스크 위의 폴더가 아닙니다.</summary>
    public const string RecycleBin = "::RecycleBin";

    /// <summary>화면에 보여 주는 이름입니다.</summary>
    public const string RecycleBinDisplayName = "휴지통";

    public static bool IsRecycleBin(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var trimmed = path.Trim();

        return string.Equals(trimmed, RecycleBin, StringComparison.OrdinalIgnoreCase)
               || string.Equals(trimmed, RecycleBinDisplayName, StringComparison.Ordinal)
               || string.Equals(trimmed, "Recycle Bin", StringComparison.OrdinalIgnoreCase);
    }

    // ── 윈도우 기본 폴더 ──
    //
    // 바탕 화면·다운로드·문서는 컴퓨터마다 실제 경로가 다릅니다.
    // 사용자 이름이 다르고, OneDrive 가 바탕 화면과 문서를 가져가 경로가 바뀌기도 합니다.
    // 그래서 설정에는 경로 대신 이 표시를 적고, 쓸 때마다 그 컴퓨터에서 실제 경로를 물어봅니다.
    // 어느 컴퓨터에 깔든 제 폴더를 찾아갑니다.

    public const string Desktop = "::Desktop";

    public const string Downloads = "::Downloads";

    public const string Documents = "::Documents";

    public const string DesktopDisplayName = "바탕 화면";

    public const string DownloadsDisplayName = "다운로드";

    public const string DocumentsDisplayName = "문서";

    /// <summary>
    /// 다운로드 폴더를 찾는 길입니다. 앱이 시작할 때 셸에게 묻는 길을 꽂아 줍니다.
    /// 다운로드는 .NET 이 따로 알려 주지 않아 셸에게 물어야 합니다. 여기(Core)에서는 셸을 부를 수 없습니다.
    /// 꽂혀 있지 않으면 사용자 폴더 아래 Downloads 로 봅니다.
    /// </summary>
    public static Func<string?>? DownloadsLocator { get; set; }

    /// <summary>바탕 화면·다운로드·문서 표시이거나 그 이름인지입니다.</summary>
    public static bool IsKnownFolder(string? path) => KnownFolderToken(path) is not null;

    /// <summary>표시나 화면 이름을 표시로 맞춥니다. 기본 폴더가 아니면 null 입니다.</summary>
    public static string? KnownFolderToken(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim();

        if (string.Equals(trimmed, Desktop, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, DesktopDisplayName, StringComparison.Ordinal))
        {
            return Desktop;
        }

        if (string.Equals(trimmed, Downloads, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, DownloadsDisplayName, StringComparison.Ordinal))
        {
            return Downloads;
        }

        if (string.Equals(trimmed, Documents, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, DocumentsDisplayName, StringComparison.Ordinal))
        {
            return Documents;
        }

        return null;
    }

    /// <summary>이 컴퓨터에서 그 기본 폴더의 실제 경로입니다. 기본 폴더가 아니면 null 입니다.</summary>
    public static string? ResolveKnownFolder(string? path)
    {
        var located = KnownFolderToken(path) switch
        {
            Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Downloads => LocateDownloads(),
            _ => null
        };

        return string.IsNullOrEmpty(located) ? null : located;
    }

    private static string? LocateDownloads()
    {
        try
        {
            if (DownloadsLocator?.Invoke() is { Length: > 0 } found)
            {
                return found;
            }
        }
        catch (Exception)
        {
            // 셸이 답하지 않았습니다. 아래 대안으로 갑니다.
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length == 0 ? null : System.IO.Path.Combine(home, "Downloads");
    }

    /// <summary>
    /// 화면에 보인 글자를 설정에 적을 값으로 바꿉니다.
    /// "내 PC", "바탕 화면"처럼 이름으로 적힌 것은 표시로 바꾸고, 보통 경로는 그대로 둡니다.
    /// </summary>
    public static string ToStored(string text)
    {
        var trimmed = text.Trim();

        if (IsMyComputer(trimmed))
        {
            return MyComputer;
        }

        // "휴지통"이라고 적어도 표시로 바꿔 둡니다.
        if (IsRecycleBin(trimmed))
        {
            return RecycleBin;
        }

        return KnownFolderToken(trimmed) ?? trimmed;
    }

    /// <summary>디스크 위의 폴더가 아닌 자리인지입니다.</summary>
    public static bool IsVirtual(string? path) => IsMyComputer(path) || IsRecycleBin(path);

    public static bool IsMyComputer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var trimmed = path.Trim();

        return string.Equals(trimmed, MyComputer, StringComparison.OrdinalIgnoreCase)
               || string.Equals(trimmed, MyComputerDisplayName, StringComparison.Ordinal)
               || string.Equals(trimmed, "This PC", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>화면에 보여 줄 이름으로 바꿉니다. 보통 경로는 그대로 돌려줍니다.</summary>
    public static string ToDisplay(string? path)
    {
        if (IsMyComputer(path))
        {
            return MyComputerDisplayName;
        }

        if (IsRecycleBin(path))
        {
            return RecycleBinDisplayName;
        }

        return KnownFolderToken(path) switch
        {
            Desktop => DesktopDisplayName,
            Downloads => DownloadsDisplayName,
            Documents => DocumentsDisplayName,
            _ => path ?? string.Empty
        };
    }
}
