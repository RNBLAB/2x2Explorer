using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Security;
using Explorer.Core.Services;
using Microsoft.Win32;

namespace Explorer.App.Services;

/// <summary>
/// 검색 결과의 경로가 네트워크에 있는지, 클라우드에 있는지 가립니다.
/// 검색 한 번마다 새로 만들어 씁니다. 그사이 드라이브가 붙거나 빠질 수 있기 때문입니다.
///
/// 클라우드는 두 가지로 알아봅니다.
///   1. 윈도우에 등록된 동기화 폴더(OneDrive·구글 드라이브·드롭박스 등이 등록하는 SyncRootManager)
///   2. 볼륨 레이블이 설정의 CloudDriveLabels 에 있는 드라이브(기본값 "MYBOX")
/// MYBOX 는 동기화 폴더로 등록하지 않고, 드라이브 종류도 "이동식"으로 잡힙니다.
/// 그래서 드라이브 종류로는 USB 메모리와 구별할 수 없어 레이블로 알아봅니다. (2026-09-26 사용자 PC에서 확인)
///
/// 네트워크이면서 클라우드인 경로는 클라우드로만 봅니다. 두 단추가 서로 겹치지 않게 하려는 것입니다.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SearchLocations
{
    private const string SyncRootKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager";

    /// <summary>
    /// 드라이브 레이블을 담아 둡니다. 네트워크 드라이브는 레이블 한 번 읽는 데도 시간이 걸립니다.
    /// 빈 레이블은 담지 않습니다. MYBOX 가 아직 준비되지 않았을 때 읽은 빈 값이 굳어 버리면 안 되기 때문입니다.
    /// </summary>
    private static readonly ConcurrentDictionary<char, string> Labels = new();

    private readonly HashSet<char> _network = new();
    private readonly HashSet<char> _cloud = new();
    private readonly List<string> _cloudRoots = new();

    private SearchLocations()
    {
    }

    /// <summary>지금 붙어 있는 드라이브와 등록된 동기화 폴더를 읽습니다. 디스크를 만지므로 UI 갈래에서 부르지 않습니다.</summary>
    public static SearchLocations Read(IEnumerable<string>? cloudLabels)
    {
        var result = new SearchLocations();
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (cloudLabels is not null)
        {
            foreach (var label in cloudLabels)
            {
                if (!string.IsNullOrWhiteSpace(label))
                {
                    labels.Add(label.Trim());
                }
            }
        }

        DriveInfo[] drives;

        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            drives = Array.Empty<DriveInfo>();
        }

        foreach (var drive in drives)
        {
            if (drive.Name.Length == 0)
            {
                continue;
            }

            var letter = char.ToUpperInvariant(drive.Name[0]);

            if (labels.Count > 0 && labels.Contains(LabelOf(drive, letter)))
            {
                _ = result._cloud.Add(letter);
            }
            else if (drive.DriveType == DriveType.Network)
            {
                _ = result._network.Add(letter);
            }
        }

        result.ReadSyncRoots();
        return result;
    }

    /// <summary>클라우드에 있는 경로인지입니다.</summary>
    public bool IsCloud(string path)
    {
        if (HasLetter(path, _cloud))
        {
            return true;
        }

        foreach (var root in _cloudRoots)
        {
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>네트워크에 있는 경로인지입니다. 클라우드인지는 따지지 않으므로 IsCloud 를 먼저 봅니다.</summary>
    public bool IsNetwork(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) || HasLetter(path, _network);

    private static bool HasLetter(string path, HashSet<char> letters) =>
        path.Length > 1
        && path[1] == ':'
        && letters.Contains(char.ToUpperInvariant(path[0]));

    private static string LabelOf(DriveInfo drive, char letter)
    {
        if (Labels.TryGetValue(letter, out var known))
        {
            return known;
        }

        try
        {
            if (drive.IsReady && !string.IsNullOrWhiteSpace(drive.VolumeLabel))
            {
                return Labels[letter] = drive.VolumeLabel.Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
        }

        return string.Empty;
    }

    /// <summary>
    /// 동기화 프로그램이 등록한 폴더를 읽습니다.
    /// SyncRootManager\(등록 이름)\UserSyncRoots 아래에 사용자 SID 이름으로 폴더 경로가 적혀 있습니다.
    /// 등록된 것이 없으면 아무것도 더하지 않습니다.
    /// </summary>
    private void ReadSyncRoots()
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(SyncRootKey);

            if (root is null)
            {
                return;
            }

            foreach (var name in root.GetSubKeyNames())
            {
                using var users = root.OpenSubKey(name + @"\UserSyncRoots");

                if (users is null)
                {
                    continue;
                }

                foreach (var valueName in users.GetValueNames())
                {
                    if (users.GetValue(valueName) is string folder && folder.Length > 2)
                    {
                        _cloudRoots.Add(folder.TrimEnd('\\') + "\\");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            DiagnosticLog.Write($"동기화 폴더 목록을 읽지 못했습니다: {ex.Message}");
        }
    }
}
