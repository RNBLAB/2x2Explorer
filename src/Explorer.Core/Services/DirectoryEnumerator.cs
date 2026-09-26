using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Explorer.Core.Abstractions;
using Explorer.Core.Models;

namespace Explorer.Core.Services;

/// <summary>
/// 폴더 한 단계를 백그라운드에서 읽습니다.
/// 파일이 1만 개인 폴더에서도 UI 스레드는 건드리지 않습니다.
/// </summary>
public sealed class DirectoryEnumerator : IDirectoryEnumerator
{
    /// <summary>
    /// 드라이브 이름을 셸에게 물어보는 길입니다. 앱이 시작할 때 꽂아 줍니다.
    /// 여기(Core)에서는 셸을 부를 수 없는데, 볼륨 레이블만 읽으면 탐색기와 이름이 달라집니다.
    /// 꽂혀 있지 않으면 레이블로 이름을 짜 맞춥니다.
    /// </summary>
    public static Func<string, string?>? ShellDisplayName { get; set; }

    public Task<IReadOnlyList<FileSystemEntry>> EnumerateAsync(
        string path,
        bool includeHidden,
        CancellationToken cancellationToken)
    {
        return Task.Run(() => Enumerate(path, includeHidden, cancellationToken), cancellationToken);
    }

    private static IReadOnlyList<FileSystemEntry> Enumerate(
        string path,
        bool includeHidden,
        CancellationToken cancellationToken)
    {
        if (SpecialPaths.IsMyComputer(path))
        {
            return EnumerateDrives();
        }

        var results = new List<FileSystemEntry>();

        var options = new EnumerationOptions
        {
            // 권한 없는 항목 하나 때문에 폴더 전체가 실패하지 않게 합니다.
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            // 탐색기처럼 숨김 속성만 봅니다. 숨김+시스템 항목(desktop.ini 등)도 여기서 함께 빠집니다.
            // 시스템 속성만 있는 항목은 보여 줍니다. MYBOX 가상 드라이브(N:)의 폴더가 모두 이 경우라
            // 예전 규칙(숨김 | 시스템)에서는 N:\ 가 통째로 비어 보였습니다. (2026-09-26 Get-ChildItem -Force 로 확인)
            AttributesToSkip = includeHidden
                ? default
                : FileAttributes.Hidden
        };

        try
        {
            var directory = new DirectoryInfo(path);

            foreach (var info in directory.EnumerateFileSystemInfos("*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(FileSystemEntry.FromInfo(info));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            // 폴더 자체를 못 연 경우입니다. 빈 폴더와 구별되도록 까닭을 실어 던집니다.
            // 예전에는 빈 목록을 돌려줘서 "비어 있음"과 "읽지 못함"이 화면에서 같아 보였습니다.
            DiagnosticLog.WriteAlways($"폴더 읽기 실패 {path} — {Describe(ex)}");
            throw new DirectoryReadException(path, Describe(ex), ex);
        }

        return results;
    }

    private static bool IsReadFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    /// <summary>로그와 상태 줄에 남길 한 줄입니다. 형식·HRESULT·메시지를 함께 적어야 원인을 가릴 수 있습니다.</summary>
    private static string Describe(Exception ex) =>
        $"{ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}";

    /// <summary>"내 PC"의 내용입니다. 드라이브를 폴더처럼 늘어놓습니다.</summary>
    private static IReadOnlyList<FileSystemEntry> EnumerateDrives()
    {
        var results = new List<FileSystemEntry>();

        DriveInfo[] drives;

        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<FileSystemEntry>();
        }

        foreach (var drive in drives)
        {
            try
            {
                var letter = drive.Name.TrimEnd(Path.DirectorySeparatorChar);
                var root = drive.RootDirectory.FullName;

                // 셸이 아는 이름이 있으면 그대로 씁니다. 탐색기에 나오는 그 이름입니다.
                var name = ShellDisplayName?.Invoke(root);

                if (string.IsNullOrWhiteSpace(name))
                {
                    name = $"{DescribeDrive(drive)} ({letter})";
                }

                results.Add(new FileSystemEntry
                {
                    FullPath = root,
                    Name = name,
                    IsDirectory = true,
                    Size = 0,
                    LastWriteTime = DateTime.MinValue,
                    Attributes = FileAttributes.Directory
                });
            }
            catch (Exception ex) when (ex is IOException
                                           or UnauthorizedAccessException
                                           or ArgumentException)
            {
                // 준비되지 않은 드라이브 하나 때문에 목록 전체를 버리지는 않습니다.
            }
        }

        return results;
    }

    private static string DescribeDrive(DriveInfo drive)
    {
        try
        {
            if (drive.IsReady && !string.IsNullOrWhiteSpace(drive.VolumeLabel))
            {
                return drive.VolumeLabel;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return drive.DriveType switch
        {
            DriveType.Fixed => "로컬 디스크",
            DriveType.Removable => "이동식 디스크",
            DriveType.Network => "네트워크 드라이브",
            DriveType.CDRom => "CD 드라이브",
            DriveType.Ram => "램 디스크",
            _ => "드라이브"
        };
    }
}
