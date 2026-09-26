using System.Collections.Generic;

namespace Explorer.Core.Services;

/// <summary>
/// 사람이 기대하는 순서로 이름을 비교합니다.
/// 단순 문자열 비교는 "파일10"을 "파일2"보다 앞에 두지만, 여기서는 숫자를 수로 읽어 뒤에 둡니다.
/// </summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static NaturalStringComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var i = 0;
        var j = 0;

        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var startX = i;
                var startY = j;

                while (i < x.Length && char.IsDigit(x[i]))
                {
                    i++;
                }

                while (j < y.Length && char.IsDigit(y[j]))
                {
                    j++;
                }

                // 앞의 0은 값에 영향을 주지 않으므로 떼고 자릿수부터 비교합니다.
                var digitsX = x.AsSpan(startX, i - startX).TrimStart('0');
                var digitsY = y.AsSpan(startY, j - startY).TrimStart('0');

                if (digitsX.Length != digitsY.Length)
                {
                    return digitsX.Length - digitsY.Length;
                }

                var digitCompare = digitsX.SequenceCompareTo(digitsY);
                if (digitCompare != 0)
                {
                    return digitCompare;
                }

                continue;
            }

            var charCompare = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
            if (charCompare != 0)
            {
                return charCompare;
            }

            i++;
            j++;
        }

        return (x.Length - i) - (y.Length - j);
    }
}
