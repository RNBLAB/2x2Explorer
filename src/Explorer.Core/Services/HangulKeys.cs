using System;
using System.Text;

namespace Explorer.Core.Services;

/// <summary>
/// 두벌식 글쇠를 한글 자모로 바꾸고, 한글을 자모로 풀어 견줍니다.
///
/// 목록에서 글자를 쳐서 항목을 찾을 때 씁니다.
/// 목록에는 입력기가 붙지 않아 완성된 한글이 오지 않습니다.
/// 그래서 눌린 글쇠를 직접 자모로 바꾸고, 파일 이름도 자모로 풀어 앞부분을 견줍니다.
/// "ㅅ"이나 "ㅅㅏ"까지 친 것으로도 "사진"이 잡히고,
/// "ㅅㅈ"처럼 첫소리만 이어 쳐도 "사진"이 잡힙니다.
/// </summary>
public static class HangulKeys
{
    private const char SyllableFirst = '가';
    private const char SyllableLast = '힣';

    private const char JamoFirst = 'ㄱ';
    private const char JamoLast = 'ㅣ';

    /// <summary>홀소리 자모가 시작하는 자리입니다. 여기부터 <see cref="JamoLast"/>까지가 홀소리입니다.</summary>
    private const char VowelFirst = 'ㅏ';

    /// <summary>첫소리 열아홉 자입니다.</summary>
    private static readonly string[] Leads =
    {
        "ㄱ", "ㄲ", "ㄴ", "ㄷ", "ㄸ", "ㄹ", "ㅁ", "ㅂ", "ㅃ", "ㅅ",
        "ㅆ", "ㅇ", "ㅈ", "ㅉ", "ㅊ", "ㅋ", "ㅌ", "ㅍ", "ㅎ"
    };

    /// <summary>가운뎃소리 스물한 자입니다. 겹모음은 친 순서대로 풀어 둡니다.</summary>
    private static readonly string[] Vowels =
    {
        "ㅏ", "ㅐ", "ㅑ", "ㅒ", "ㅓ", "ㅔ", "ㅕ", "ㅖ", "ㅗ", "ㅗㅏ",
        "ㅗㅐ", "ㅗㅣ", "ㅛ", "ㅜ", "ㅜㅓ", "ㅜㅔ", "ㅜㅣ", "ㅠ", "ㅡ", "ㅡㅣ",
        "ㅣ"
    };

    /// <summary>끝소리 스물여덟 자입니다. 첫 자리는 받침이 없는 경우입니다.</summary>
    private static readonly string[] Tails =
    {
        "", "ㄱ", "ㄲ", "ㄱㅅ", "ㄴ", "ㄴㅈ", "ㄴㅎ", "ㄷ", "ㄹ", "ㄹㄱ",
        "ㄹㅁ", "ㄹㅂ", "ㄹㅅ", "ㄹㅌ", "ㄹㅍ", "ㄹㅎ", "ㅁ", "ㅂ", "ㅂㅅ", "ㅅ",
        "ㅆ", "ㅇ", "ㅈ", "ㅊ", "ㅋ", "ㅌ", "ㅍ", "ㅎ"
    };

    /// <summary>글쇠 번호(A=0x41 … Z=0x5A)에 맞춘 두벌식 자모입니다.</summary>
    private static readonly string[] Plain =
    {
        "ㅁ", "ㅠ", "ㅊ", "ㅇ", "ㄷ", "ㄹ", "ㅎ", "ㅗ", "ㅑ", "ㅓ",
        "ㅏ", "ㅣ", "ㅡ", "ㅜ", "ㅐ", "ㅔ", "ㅂ", "ㄱ", "ㄴ", "ㅅ",
        "ㅕ", "ㅍ", "ㅈ", "ㅌ", "ㅛ", "ㅋ"
    };

    /// <summary>같은 자리에 Shift를 더했을 때입니다. 빈 칸은 Shift를 눌러도 같습니다.</summary>
    private static readonly string[] Shifted =
    {
        "", "", "", "", "ㄸ", "", "", "", "", "",
        "", "", "", "", "ㅒ", "ㅖ", "ㅃ", "ㄲ", "", "ㅆ",
        "", "", "ㅉ", "", "", ""
    };

    /// <summary>자모 한 글자인지 봅니다. ㄱ부터 ㅣ까지입니다.</summary>
    public static bool IsJamo(char letter) => letter >= JamoFirst && letter <= JamoLast;

    /// <summary>글월에 자모가 섞여 있는지 봅니다.</summary>
    public static bool HasJamo(string text)
    {
        foreach (var letter in text)
        {
            if (IsJamo(letter))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 눌린 글쇠를 두벌식 자모로 바꿉니다. 한글 글쇠가 아니면 null입니다.
    /// </summary>
    /// <param name="virtualKey">윈도우가 알려 주는 글쇠 번호입니다. A는 0x41입니다.</param>
    /// <param name="shift">Shift를 함께 눌렀는지 여부입니다.</param>
    public static string? FromVirtualKey(int virtualKey, bool shift)
    {
        if (virtualKey < 0x41 || virtualKey > 0x5A)
        {
            return null;
        }

        var index = virtualKey - 0x41;

        if (shift && Shifted[index].Length > 0)
        {
            return Shifted[index];
        }

        return Plain[index];
    }

    /// <summary>
    /// 한글을 자모로 풀어 늘어놓습니다. 영문은 소문자로 바꿉니다.
    /// "사진"은 "ㅅㅏㅈㅣㄴ"이 되고, "가"는 "ㄱㅏ"가 됩니다.
    /// </summary>
    public static string Decompose(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length * 3);

        foreach (var letter in text)
        {
            if (letter >= SyllableFirst && letter <= SyllableLast)
            {
                var offset = letter - SyllableFirst;

                builder.Append(Leads[offset / (21 * 28)]);
                builder.Append(Vowels[offset / 28 % 21]);
                builder.Append(Tails[offset % 28]);
                continue;
            }

            builder.Append(char.ToLowerInvariant(letter));
        }

        return builder.ToString();
    }

    /// <summary>
    /// 이름이 친 자모로 시작하는지 봅니다.
    /// 친 쪽도 이름 쪽도 자모로 풀어 견주므로 "ㅅㅏ"와 "사"가 같은 것으로 잡힙니다.
    /// 닿소리만 이어 쳤으면 글자마다의 첫소리와도 견줍니다. "ㅅㅈ"으로 "사진"을 찾는 길입니다.
    /// </summary>
    public static bool StartsWithJamo(string name, string typed)
    {
        if (Decompose(name).StartsWith(Decompose(typed), StringComparison.Ordinal))
        {
            return true;
        }

        return IsLeadsOnly(typed)
               && FirstSounds(name).StartsWith(FirstSounds(typed), StringComparison.Ordinal);
    }

    /// <summary>
    /// 글자마다 첫소리만 남깁니다. "사진"은 "ㅅㅈ"이 됩니다.
    /// 한글이 아닌 글자는 소문자로 바꿔 그대로 둡니다.
    /// </summary>
    public static string FirstSounds(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);

        foreach (var letter in text)
        {
            if (letter >= SyllableFirst && letter <= SyllableLast)
            {
                builder.Append(Leads[(letter - SyllableFirst) / (21 * 28)]);
                continue;
            }

            builder.Append(char.ToLowerInvariant(letter));
        }

        return builder.ToString();
    }

    /// <summary>
    /// 첫소리만 이어 친 것인지 봅니다.
    ///
    /// 닿소리 자모만 있어야 합니다. 홀소리나 완성된 글자가 섞이면 첫소리 찾기가 아닙니다.
    /// 이 가름이 없으면 "사"를 친 사람에게 "수박"이 잡힙니다.
    /// </summary>
    private static bool IsLeadsOnly(string typed)
    {
        if (string.IsNullOrEmpty(typed))
        {
            return false;
        }

        var found = false;

        foreach (var letter in typed)
        {
            if (letter >= SyllableFirst && letter <= SyllableLast)
            {
                return false;
            }

            if (letter >= VowelFirst && letter <= JamoLast)
            {
                return false;
            }

            if (letter >= JamoFirst && letter < VowelFirst)
            {
                found = true;
            }
        }

        return found;
    }
}
