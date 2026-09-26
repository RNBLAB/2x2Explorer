using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Explorer.Core.Services;

/// <summary>
/// 무슨 일이 있었는지 파일에 적어 둡니다.
/// 화면에 아무 반응이 없을 때 어디까지 갔는지 보려고 쓰는 최소한의 장치입니다.
/// 실패해도 조용히 넘어갑니다. 로그 때문에 프로그램이 멈추면 안 됩니다.
/// </summary>
public static class DiagnosticLog
{
    private static readonly object Gate = new();

    static DiagnosticLog()
    {
        // 실행 파일이 있는 폴더에 남깁니다. 프로젝트 폴더 안이라 찾기 쉽습니다.
        FilePath = Path.Combine(AppContext.BaseDirectory, "debug.log");
    }

    /// <summary>로그 파일의 전체 경로입니다.</summary>
    public static string FilePath { get; }

    /// <summary>꺼 두면 아무것도 적지 않습니다.</summary>
    public static bool Enabled { get; set; } = true;

    public static void Write(string message)
    {
        if (!Enabled)
        {
            return;
        }

        Append(message);
    }

    /// <summary>
    /// 기록을 꺼 두었어도 적습니다.
    /// 예외처럼 나중에 꼭 되짚어 봐야 하는 것만 이 길로 보냅니다.
    /// </summary>
    public static void WriteAlways(string message) => Append(message);

    private static void Append(string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var line = string.Format(
                CultureInfo.InvariantCulture,
                "{0:HH:mm:ss.fff}  {1}{2}",
                DateTime.Now,
                message,
                Environment.NewLine);

            lock (Gate)
            {
                File.AppendAllText(FilePath, line, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    /// <summary>지난 실행의 기록입니다. 켤 때마다 한 판씩 밀려 이 이름으로 남습니다.</summary>
    public static string PreviousFilePath => Path.ChangeExtension(FilePath, ".prev.log");

    /// <summary>
    /// 프로그램을 켤 때 한 번 불러 새 기록을 시작합니다.
    /// 지난 기록은 지우지 않고 debug.prev.log 로 옮깁니다. 버그를 본 뒤 다시 켜면
    /// 그 실행의 기록이 통째로 사라져 원인을 되짚을 수 없었습니다(2026-09-27).
    /// </summary>
    public static void Reset()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Move(FilePath, PreviousFilePath, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 옮기지 못하면(다른 프로그램이 열어 둔 경우 등) 예전처럼 지우고 새로 시작합니다.
            try
            {
                File.Delete(FilePath);
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
