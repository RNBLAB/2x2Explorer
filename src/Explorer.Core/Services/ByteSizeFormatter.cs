using System.Globalization;

namespace Explorer.Core.Services;

/// <summary>바이트 수를 목록에 넣을 짧은 문자열로 바꿉니다.</summary>
public static class ByteSizeFormatter
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    public static string Format(long bytes)
    {
        if (bytes < 0)
        {
            return string.Empty;
        }

        if (bytes < 1024)
        {
            return bytes.ToString("N0", CultureInfo.CurrentCulture) + " B";
        }

        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // 1024 미만 구간에서는 소수점 한 자리까지만 보여 줍니다.
        var format = value >= 100 ? "N0" : "N1";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + Units[unit];
    }
}
