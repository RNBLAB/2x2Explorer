using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Explorer.Core.Services;

namespace Explorer.Shell.Operations;

[ComImport]
[Guid("000214F9-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellLinkW
{
    void GetPath([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int length, IntPtr findData, uint flags);

    void GetIDList(out IntPtr idList);

    void SetIDList(IntPtr idList);

    void GetDescription([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int length);

    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

    void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder folder, int length);

    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string folder);

    void GetArguments([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder arguments, int length);

    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);

    void GetHotkey(out short hotkey);

    void SetHotkey(short hotkey);

    void GetShowCmd(out int show);

    void SetShowCmd(int show);

    void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder iconPath, int length, out int index);

    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int index);

    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);

    void Resolve(IntPtr window, uint flags);

    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
}

[ComImport]
[Guid("0000010b-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPersistFile
{
    void GetClassID(out Guid classId);

    [PreserveSig]
    int IsDirty();

    void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);

    void Save([MarshalAs(UnmanagedType.LPWStr)] string? fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);

    void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);

    void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
}

/// <summary>
/// 바로가기(.lnk)를 만들고 읽습니다.
/// 윈도우에서 Ctrl+Shift나 Alt를 누른 채 끌어다 놓을 때 생기는 그것입니다.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ShellShortcut
{
    private static readonly Guid ShellLinkClassId = new("00021401-0000-0000-C000-000000000046");

    /// <summary>주어진 것들의 바로가기를 그 폴더에 만듭니다. 만든 개수를 돌려줍니다.</summary>
    public static int CreateAll(System.Collections.Generic.IReadOnlyList<string> targets, string folder)
    {
        if (targets.Count == 0 || string.IsNullOrWhiteSpace(folder))
        {
            return 0;
        }

        var made = 0;

        foreach (var target in targets)
        {
            if (Create(target, folder))
            {
                made++;
            }
        }

        return made;
    }

    private static bool Create(string target, string folder)
    {
        object? link = null;

        try
        {
            var type = Type.GetTypeFromCLSID(ShellLinkClassId);

            if (type is null || Activator.CreateInstance(type) is not IShellLinkW shortcut)
            {
                return false;
            }

            link = shortcut;
            shortcut.SetPath(target);

            var workingFolder = Path.GetDirectoryName(target);

            if (!string.IsNullOrEmpty(workingFolder))
            {
                shortcut.SetWorkingDirectory(workingFolder);
            }

            if (shortcut is not IPersistFile file)
            {
                return false;
            }

            file.Save(FindAvailableName(target, folder), remember: false);
            return true;
        }
        catch (Exception ex) when (ex is COMException
                                       or InvalidCastException
                                       or NotSupportedException
                                       or UnauthorizedAccessException
                                       or IOException
                                       or ArgumentException)
        {
            DiagnosticLog.Write($"바로가기를 만들지 못했습니다 {target}: {ex.GetType().Name} {ex.Message}");
            return false;
        }
        finally
        {
            if (link is not null)
            {
                Marshal.ReleaseComObject(link);
            }
        }
    }

    /// <summary>
    /// 바로가기가 가리키는 곳을 읽습니다. 읽지 못하면 null 입니다.
    ///
    /// 바로가기를 찾아 고치는 Resolve 는 부르지 않습니다.
    /// 대상이 옮겨졌으면 디스크를 뒤지거나 창을 띄우는데, 그동안 화면이 멈춥니다.
    /// 적혀 있는 경로만 꺼냅니다.
    /// </summary>
    public static string? ReadTarget(string linkPath)
    {
        object? link = null;

        try
        {
            var type = Type.GetTypeFromCLSID(ShellLinkClassId);

            if (type is null || Activator.CreateInstance(type) is not IShellLinkW shortcut)
            {
                return null;
            }

            link = shortcut;

            if (shortcut is not IPersistFile file)
            {
                return null;
            }

            // 0 은 읽기 전용으로 연다는 뜻입니다.
            file.Load(linkPath, 0);

            var buffer = new System.Text.StringBuilder(1024);
            shortcut.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);

            var target = buffer.ToString();

            return string.IsNullOrWhiteSpace(target)
                ? null
                : Environment.ExpandEnvironmentVariables(target);
        }
        catch (Exception ex) when (ex is COMException
                                       or InvalidCastException
                                       or NotSupportedException
                                       or UnauthorizedAccessException
                                       or IOException
                                       or ArgumentException)
        {
            DiagnosticLog.Write($"바로가기를 읽지 못했습니다 {linkPath}: {ex.GetType().Name} {ex.Message}");
            return null;
        }
        finally
        {
            if (link is not null)
            {
                Marshal.ReleaseComObject(link);
            }
        }
    }

    /// <summary>이미 있는 이름을 피해 "이름 - 바로 가기", "이름 - 바로 가기 (2)" 순으로 고릅니다.</summary>
    private static string FindAvailableName(string target, string folder)
    {
        var name = Path.GetFileNameWithoutExtension(target);

        if (string.IsNullOrEmpty(name))
        {
            name = Path.GetFileName(target.TrimEnd(Path.DirectorySeparatorChar));
        }

        var candidate = Path.Combine(folder, $"{name} - 바로 가기.lnk");
        var index = 2;

        while (File.Exists(candidate) && index < 1000)
        {
            candidate = Path.Combine(folder, $"{name} - 바로 가기 ({index}).lnk");
            index++;
        }

        return candidate;
    }
}
