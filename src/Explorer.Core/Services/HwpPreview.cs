using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Explorer.Core.Services;

/// <summary>한글 문서 안에 담긴 미리보기입니다. 첫 쪽 그림과 앞부분 글자입니다. 없는 것은 null 입니다.</summary>
public sealed record HwpPreviewData(byte[]? Image, string? Text);

/// <summary>
/// 한글 문서(.hwp, .hwpx)가 스스로 담아 두는 미리보기를 꺼냅니다. 한컴오피스가 없어도 됩니다.
///
/// - hwp(5.0) 는 여러 흐름을 한 파일에 담는 복합 문서(Compound File) 형식입니다.
///   그 안의 "PrvImage" 흐름에 첫 쪽 그림(BMP·GIF·PNG)이, "PrvText" 흐름에 앞부분 글자(UTF-16)가 들어 있습니다.
/// - hwpx 는 ZIP 입니다. "Preview/PrvImage.png" 와 "Preview/PrvText.txt" 에 같은 것이 들어 있습니다.
///
/// 복합 문서 읽기는 윈도우 부품(StgOpenStorage) 대신 직접 합니다. 한글에서 열어 둔 파일도
/// 공유 읽기로 통째로 읽은 뒤 메모리에서 풀기 때문에 잠겨 있어도 읽히고, 시험에서도 돌릴 수 있습니다.
/// </summary>
public static class HwpPreview
{
    /// <summary>미리보기로 읽을 파일의 최대 크기입니다. 넘으면 앞부분만 읽을 수 없는 형식이라 건너뜁니다.</summary>
    private const long MaxFileBytes = 256L * 1024 * 1024;

    public static bool IsHwp(string? path) =>
        string.Equals(Path.GetExtension(path), ".hwp", StringComparison.OrdinalIgnoreCase);

    public static bool IsHwpx(string? path) =>
        string.Equals(Path.GetExtension(path), ".hwpx", StringComparison.OrdinalIgnoreCase);

    /// <summary>그 파일의 미리보기를 읽습니다. 한글 문서가 아니거나 읽지 못하면 null 입니다.</summary>
    public static HwpPreviewData? Read(string path)
    {
        try
        {
            if (IsHwpx(path))
            {
                return ReadHwpx(path);
            }

            if (!IsHwp(path))
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            if (stream.Length > MaxFileBytes)
            {
                return null;
            }

            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return ReadHwp(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            DiagnosticLog.Write($"한글 미리보기를 읽지 못했습니다 {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>hwp(복합 문서) 바이트에서 미리보기를 꺼냅니다.</summary>
    public static HwpPreviewData? ReadHwp(byte[] file)
    {
        var compound = CompoundFile.TryOpen(file);

        if (compound is null)
        {
            return null;
        }

        var image = compound.ReadStream("PrvImage");
        var textBytes = compound.ReadStream("PrvText");
        var text = textBytes is { Length: > 0 } ? CleanText(Encoding.Unicode.GetString(textBytes)) : null;

        return image is null && text is null ? null : new HwpPreviewData(image, text);
    }

    private static HwpPreviewData? ReadHwpx(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        byte[]? image = null;
        string? text = null;

        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');

            if (image is null && name.StartsWith("Preview/PrvImage", StringComparison.OrdinalIgnoreCase))
            {
                image = ReadEntry(entry);
            }
            else if (text is null && name.StartsWith("Preview/PrvText", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = ReadEntry(entry);
                text = bytes is null ? null : CleanText(Encoding.UTF8.GetString(bytes));
            }
        }

        return image is null && text is null ? null : new HwpPreviewData(image, text);
    }

    private static byte[]? ReadEntry(ZipArchiveEntry entry)
    {
        if (entry.Length <= 0 || entry.Length > 64L * 1024 * 1024)
        {
            return null;
        }

        using var input = entry.Open();
        using var buffer = new MemoryStream((int)entry.Length);
        input.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>끝의 널 글자와 BOM 을 떼고, 한글이 쓰는 줄바꿈 표시를 보통 줄바꿈으로 바꿉니다.</summary>
    private static string? CleanText(string text)
    {
        var cleaned = text.Replace("﻿", string.Empty).TrimEnd('\0', ' ', '\r', '\n');
        cleaned = cleaned.Replace("\r\n", "\n").Replace('\r', '\n');

        return cleaned.Length == 0 ? null : cleaned;
    }
}

/// <summary>
/// 복합 문서(Microsoft Compound File Binary, 옛 오피스·hwp 가 쓰는 틀)를 읽기만 하는 아주 작은 해석기입니다.
/// 이름으로 흐름 하나를 찾아 그 바이트를 돌려주는 일만 합니다. 쓰기는 하지 않습니다.
/// </summary>
public sealed class CompoundFile
{
    private static readonly byte[] Signature = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FreeSector = 0xFFFFFFFF;
    private const int DirectoryEntrySize = 128;

    private readonly byte[] _file;
    private readonly int _sectorSize;
    private readonly int _miniSectorSize;
    private readonly uint _miniCutoff;
    private readonly uint[] _fat;
    private readonly uint[] _miniFat;
    private readonly byte[] _directory;
    private readonly byte[] _miniStream;

    private CompoundFile(byte[] file, int sectorSize, int miniSectorSize, uint miniCutoff, uint[] fat, uint[] miniFat, byte[] directory, byte[] miniStream)
    {
        _file = file;
        _sectorSize = sectorSize;
        _miniSectorSize = miniSectorSize;
        _miniCutoff = miniCutoff;
        _fat = fat;
        _miniFat = miniFat;
        _directory = directory;
        _miniStream = miniStream;
    }

    /// <summary>복합 문서면 읽을 준비를 하고, 아니거나 깨졌으면 null 입니다.</summary>
    public static CompoundFile? TryOpen(byte[] file)
    {
        if (file.Length < 512 || !file.AsSpan(0, 8).SequenceEqual(Signature))
        {
            return null;
        }

        try
        {
            var sectorShift = BitConverter.ToUInt16(file, 0x1E);
            var miniShift = BitConverter.ToUInt16(file, 0x20);

            if (sectorShift is not (9 or 12) || miniShift != 6)
            {
                return null;
            }

            var sectorSize = 1 << sectorShift;
            var fatSectors = BitConverter.ToUInt32(file, 0x2C);
            var firstDirectory = BitConverter.ToUInt32(file, 0x30);
            var miniCutoff = BitConverter.ToUInt32(file, 0x38);
            var firstMiniFat = BitConverter.ToUInt32(file, 0x3C);
            var firstDifat = BitConverter.ToUInt32(file, 0x44);
            var difatSectors = BitConverter.ToUInt32(file, 0x48);

            // FAT 가 놓인 섹터 목록(DIFAT)입니다. 머리에 109 개, 모자라면 이어지는 DIFAT 섹터에 있습니다.
            var fatSectorList = new List<uint>();

            for (var i = 0; i < 109 && fatSectorList.Count < fatSectors; i++)
            {
                fatSectorList.Add(BitConverter.ToUInt32(file, 0x4C + (i * 4)));
            }

            var difat = firstDifat;
            var perSector = (sectorSize / 4) - 1;

            for (var guard = 0; guard < difatSectors && difat != EndOfChain && difat != FreeSector && fatSectorList.Count < fatSectors; guard++)
            {
                var offset = SectorOffset(difat, sectorSize);

                for (var i = 0; i < perSector && fatSectorList.Count < fatSectors; i++)
                {
                    fatSectorList.Add(BitConverter.ToUInt32(file, offset + (i * 4)));
                }

                difat = BitConverter.ToUInt32(file, offset + (perSector * 4));
            }

            var entriesPerSector = sectorSize / 4;
            var fat = new uint[fatSectorList.Count * entriesPerSector];

            for (var s = 0; s < fatSectorList.Count; s++)
            {
                var offset = SectorOffset(fatSectorList[s], sectorSize);

                for (var i = 0; i < entriesPerSector; i++)
                {
                    fat[(s * entriesPerSector) + i] = BitConverter.ToUInt32(file, offset + (i * 4));
                }
            }

            var directory = ReadChain(file, fat, firstDirectory, sectorSize, long.MaxValue);

            if (directory.Length < DirectoryEntrySize)
            {
                return null;
            }

            var miniFatBytes = firstMiniFat == EndOfChain ? Array.Empty<byte>() : ReadChain(file, fat, firstMiniFat, sectorSize, long.MaxValue);
            var miniFat = new uint[miniFatBytes.Length / 4];

            for (var i = 0; i < miniFat.Length; i++)
            {
                miniFat[i] = BitConverter.ToUInt32(miniFatBytes, i * 4);
            }

            // 뿌리 항목(0번)이 작은 흐름들을 모아 둔 "미니 흐름"을 가리킵니다.
            var rootStart = BitConverter.ToUInt32(directory, 0x74);
            var rootSize = (long)BitConverter.ToUInt64(directory, 0x78);
            var miniStream = rootStart == EndOfChain || rootSize == 0
                ? Array.Empty<byte>()
                : ReadChain(file, fat, rootStart, sectorSize, rootSize);

            return new CompoundFile(file, sectorSize, 1 << miniShift, miniCutoff, fat, miniFat, directory, miniStream);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>그 이름의 흐름을 읽습니다. 없으면 null 입니다. 이름은 대소문자를 가리지 않습니다.</summary>
    public byte[]? ReadStream(string name)
    {
        for (var offset = 0; offset + DirectoryEntrySize <= _directory.Length; offset += DirectoryEntrySize)
        {
            var type = _directory[offset + 0x42];

            if (type != 2)
            {
                continue;
            }

            var nameLength = BitConverter.ToUInt16(_directory, offset + 0x40);

            if (nameLength < 2 || nameLength > 64)
            {
                continue;
            }

            var entryName = Encoding.Unicode.GetString(_directory, offset, nameLength - 2);

            if (!string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var start = BitConverter.ToUInt32(_directory, offset + 0x74);
            var size = (long)(BitConverter.ToUInt64(_directory, offset + 0x78) & 0xFFFFFFFF);

            if (size == 0)
            {
                return Array.Empty<byte>();
            }

            try
            {
                return size < _miniCutoff
                    ? ReadMiniChain(start, size)
                    : ReadChain(_file, _fat, start, _sectorSize, size);
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidDataException)
            {
                return null;
            }
        }

        return null;
    }

    private byte[] ReadMiniChain(uint start, long size)
    {
        var result = new byte[size];
        var written = 0L;
        var sector = start;

        for (var guard = 0; written < size && sector != EndOfChain; guard++)
        {
            if (guard > _miniFat.Length || sector >= _miniFat.Length)
            {
                throw new InvalidDataException("미니 흐름 사슬이 깨졌습니다.");
            }

            var offset = (long)sector * _miniSectorSize;
            var count = (int)Math.Min(_miniSectorSize, size - written);
            Array.Copy(_miniStream, offset, result, written, count);
            written += count;
            sector = _miniFat[sector];
        }

        return result;
    }

    /// <summary>FAT 를 따라 섹터를 이어 읽습니다. 크기를 넘거나 사슬이 돌면 멈춥니다.</summary>
    private static byte[] ReadChain(byte[] file, uint[] fat, uint start, int sectorSize, long size)
    {
        using var buffer = new MemoryStream();
        var sector = start;

        for (var guard = 0; sector != EndOfChain && buffer.Length < size; guard++)
        {
            if (guard > fat.Length || sector >= fat.Length)
            {
                throw new InvalidDataException("섹터 사슬이 깨졌습니다.");
            }

            var offset = SectorOffset(sector, sectorSize);
            var count = (int)Math.Min(sectorSize, Math.Min(size - buffer.Length, file.Length - offset));

            if (count <= 0)
            {
                break;
            }

            buffer.Write(file, offset, count);
            sector = fat[sector];
        }

        return buffer.ToArray();
    }

    private static int SectorOffset(uint sector, int sectorSize) => checked((int)((sector + 1L) * sectorSize));
}
