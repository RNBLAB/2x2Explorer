using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;

namespace Explorer.App.Services;

/// <summary>
/// 파일을 클립보드에 담고 꺼냅니다.
/// 윈도우 탐색기와 같은 규약을 씁니다. 파일 목록은 FileDrop,
/// 복사인지 잘라내기인지는 "Preferred DropEffect"라는 이름의 4바이트 값입니다.
/// 덕분에 우리가 복사한 것을 탐색기에 붙여넣을 수 있고 그 반대도 됩니다.
/// </summary>
[SupportedOSPlatform("windows")]
public static class FileClipboard
{
    private const string PreferredDropEffect = "Preferred DropEffect";

    public static bool HasFiles
    {
        get
        {
            try
            {
                return Clipboard.ContainsFileDropList();
            }
            catch (Exception ex) when (IsClipboardFailure(ex))
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 파일 목록을 윈도우 규약에 맞는 꾸러미로 만듭니다.
    /// 클립보드에 담을 때도, 마우스로 끌 때도 같은 꾸러미를 씁니다.
    /// </summary>
    public static DataObject CreateData(IReadOnlyList<string> paths, bool cut)
    {
        var files = new StringCollection();

        foreach (var path in paths)
        {
            files.Add(path);
        }

        var data = new DataObject();
        data.SetFileDropList(files);

        var effect = cut ? DragDropEffects.Move : DragDropEffects.Copy;
        var stream = new MemoryStream(4);
        stream.Write(BitConverter.GetBytes((int)effect), 0, 4);
        data.SetData(PreferredDropEffect, stream);

        return data;
    }

    public static bool Set(IReadOnlyList<string> paths, bool cut)
    {
        if (paths.Count == 0)
        {
            return false;
        }

        try
        {
            // true를 주면 우리 프로그램이 닫혀도 클립보드 내용이 남습니다.
            Clipboard.SetDataObject(CreateData(paths, cut), copy: true);
            return true;
        }
        catch (Exception ex) when (IsClipboardFailure(ex))
        {
            return false;
        }
    }

    public static bool TryGet(out List<string> paths, out bool cut)
    {
        paths = new List<string>();
        cut = false;

        try
        {
            if (!Clipboard.ContainsFileDropList())
            {
                return false;
            }

            foreach (var path in Clipboard.GetFileDropList())
            {
                if (!string.IsNullOrEmpty(path))
                {
                    paths.Add(path);
                }
            }

            if (Clipboard.GetDataObject() is { } data
                && data.GetData(PreferredDropEffect) is MemoryStream stream
                && stream.Length >= 4)
            {
                var buffer = new byte[4];
                stream.Position = 0;
                _ = stream.Read(buffer, 0, 4);

                var effect = (DragDropEffects)BitConverter.ToInt32(buffer, 0);
                cut = (effect & DragDropEffects.Move) == DragDropEffects.Move;
            }

            return paths.Count > 0;
        }
        catch (Exception ex) when (IsClipboardFailure(ex))
        {
            return false;
        }
    }

    // 클립보드는 다른 프로그램이 잡고 있으면 잠깐 실패합니다. 그때 죽지 않게 합니다.
    private static bool IsClipboardFailure(Exception ex) =>
        ex is COMException or ExternalException or OutOfMemoryException or InvalidOperationException;
}
