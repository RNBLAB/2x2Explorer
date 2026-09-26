using System;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using Explorer.Shell.Interop;

namespace Explorer.Shell.Text;

/// <summary>텍스트 미리보기를 위해 파일 앞부분을 읽고 인코딩을 가려냅니다.</summary>
[SupportedOSPlatform("windows")]
public static class TextPreviewReader
{
    /// <summary>한국어 윈도우의 기본 코드 페이지입니다.</summary>
    private const uint CodePageKorean = 949;

    /// <summary>
    /// 파일 앞부분을 읽어 문자열로 돌려줍니다.
    /// BOM → UTF-8 → CP949 순으로 판별하고, 이진 파일로 보이면 null을 돌려줍니다.
    /// </summary>
    /// <param name="path">읽을 파일입니다.</param>
    /// <param name="maxBytes">읽을 최대 바이트 수입니다.</param>
    /// <param name="encodingName">판별한 인코딩 이름이 들어갑니다.</param>
    /// <param name="truncated">파일이 잘려서 읽혔는지 여부입니다.</param>
    public static string? TryRead(string path, long maxBytes, out string encodingName, out bool truncated)
    {
        encodingName = string.Empty;
        truncated = false;

        byte[] bytes;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = (int)Math.Min(stream.Length, maxBytes);
            truncated = stream.Length > length;

            bytes = new byte[length];
            var read = stream.Read(bytes, 0, length);

            if (read < length)
            {
                Array.Resize(ref bytes, read);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        if (LooksBinary(bytes))
        {
            return null;
        }

        // 1) BOM이 있으면 그대로 믿습니다.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encodingName = "UTF-8 (BOM)";
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            encodingName = "UTF-16 LE";
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            encodingName = "UTF-16 BE";
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        // 잘린 파일은 마지막 글자가 중간에서 끊겼을 수 있습니다.
        // 그대로 두면 UTF-8 판별이 실패해 엉뚱하게 CP949로 읽습니다.
        var usable = truncated ? TrimIncompleteUtf8Tail(bytes) : bytes.Length;

        // 2) 엄격한 UTF-8로 읽어 봅니다. 성공하면 UTF-8입니다.
        try
        {
            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, 0, usable);
            encodingName = "UTF-8";
            return text;
        }
        catch (DecoderFallbackException)
        {
        }

        // 3) 남은 후보는 한국어 윈도우의 기본 코드 페이지입니다.
        var ansi = DecodeWithCodePage(bytes, usable, CodePageKorean);
        if (ansi is not null)
        {
            encodingName = "CP949 (EUC-KR)";
            return ansi;
        }

        encodingName = "Latin-1";
        return Encoding.Latin1.GetString(bytes, 0, usable);
    }

    /// <summary>NUL 바이트가 섞여 있으면 텍스트가 아니라고 봅니다.</summary>
    private static bool LooksBinary(byte[] bytes)
    {
        var limit = Math.Min(bytes.Length, 8000);

        for (var i = 0; i < limit; i++)
        {
            if (bytes[i] == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>끝에 걸친 불완전한 UTF-8 바이트 열을 떼어 낸 길이를 돌려줍니다.</summary>
    private static int TrimIncompleteUtf8Tail(byte[] bytes)
    {
        var end = bytes.Length;

        // 이어지는 바이트(10xxxxxx)는 최대 3개까지만 거슬러 올라가면 됩니다.
        for (var back = 0; back < 4 && end - 1 - back >= 0; back++)
        {
            var b = bytes[end - 1 - back];

            if ((b & 0x80) == 0)
            {
                return end;
            }

            if ((b & 0xC0) == 0xC0)
            {
                var needed = (b & 0xF0) == 0xF0 ? 4 : (b & 0xE0) == 0xE0 ? 3 : 2;
                return back + 1 >= needed ? end : end - 1 - back;
            }
        }

        return end;
    }

    private static string? DecodeWithCodePage(byte[] bytes, int length, uint codePage)
    {
        if (length == 0)
        {
            return string.Empty;
        }

        var source = bytes;

        if (length != bytes.Length)
        {
            source = new byte[length];
            Array.Copy(bytes, source, length);
        }

        var needed = NativeMethods.MultiByteToWideChar(codePage, 0, source, source.Length, null, 0);

        if (needed <= 0)
        {
            return null;
        }

        var buffer = new char[needed];
        var written = NativeMethods.MultiByteToWideChar(codePage, 0, source, source.Length, buffer, needed);

        return written <= 0 ? null : new string(buffer, 0, written);
    }
}
