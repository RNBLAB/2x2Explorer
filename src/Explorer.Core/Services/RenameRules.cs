using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Explorer.Core.Models;

namespace Explorer.Core.Services;

/// <summary>
/// 일괄 이름 바꾸기에서 쓰는 이름 규칙들입니다.
///
/// 모두 글자만 다룹니다. 디스크를 건드리지 않으므로 그대로 시험할 수 있습니다.
/// 확장자는 따로 떼어 두고 본 이름만 손대는 것이 기본입니다.
/// </summary>
public static class RenameRules
{
    /// <summary>윈도우가 파일 이름에 허락하지 않는 글자입니다.</summary>
    public const string BadCharacters = "\\/:*?\"<>|";

    /// <summary>연번 자릿수의 위아래 한계입니다.</summary>
    private const int MinDigits = 1;

    private const int MaxDigits = 6;

    /// <summary>
    /// 이름을 다듬습니다.
    /// 앞뒤 공백을 없애고, 잇따른 공백을 하나로 줄이고, 밑줄과 점을 공백으로 바꾸고,
    /// 한글 자모를 한 글자로 합칩니다(NFC). 확장자는 소문자로 맞춥니다.
    /// </summary>
    public static string Normalize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return name ?? string.Empty;
        }

        // 앞뒤 공백을 먼저 털어 냅니다. 확장자 뒤에 붙은 공백까지 함께 없애려면 이 차례라야 합니다.
        var tidyName = name.Trim();
        var extension = Path.GetExtension(tidyName);
        var body = Path.GetFileNameWithoutExtension(tidyName);

        var builder = new StringBuilder(body.Length);
        var space = false;

        foreach (var letter in body)
        {
            var current = letter is '_' or '.' ? ' ' : letter;

            if (current == ' ')
            {
                space = true;
                continue;
            }

            if (space && builder.Length > 0)
            {
                builder.Append(' ');
            }

            space = false;
            builder.Append(current);
        }

        var tidy = builder.ToString() + extension.ToLowerInvariant();
        return tidy.Normalize(NormalizationForm.FormC);
    }

    /// <summary>확장자를 바꿉니다. 빈 값을 주면 확장자를 뗍니다.</summary>
    public static string WithExtension(string name, string? extension)
    {
        var body = Path.GetFileNameWithoutExtension(name);

        // 숨김 파일처럼 본 이름이 없는 경우입니다. 그때는 원래 이름을 본 이름으로 봅니다.
        if (body.Length == 0)
        {
            body = name;
        }

        var wanted = (extension ?? string.Empty).Trim().TrimStart('.');

        return wanted.Length == 0 ? body : $"{body}.{wanted}";
    }

    /// <summary>본 이름에서 찾은 말을 바꿉니다. 확장자는 건드리지 않습니다.</summary>
    public static string Replace(string name, string? find, string? replacement)
    {
        if (string.IsNullOrEmpty(find))
        {
            return name;
        }

        var extension = Path.GetExtension(name);
        var body = Path.GetFileNameWithoutExtension(name);

        if (body.Length == 0)
        {
            return name.Replace(find, replacement ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        return body.Replace(find, replacement ?? string.Empty, StringComparison.OrdinalIgnoreCase) + extension;
    }

    /// <summary>
    /// 연번을 붙입니다.
    /// 앞말을 주면 "앞말01.jpg"가 되고, 비워 두면 원래 이름 뒤에 "이름 (01).jpg"처럼 붙습니다.
    /// </summary>
    public static string Numbered(string name, string? prefix, int index, int digits)
    {
        var extension = Path.GetExtension(name);
        var number = index.ToString(CultureInfo.InvariantCulture)
            .PadLeft(Math.Clamp(digits, MinDigits, MaxDigits), '0');

        if (!string.IsNullOrWhiteSpace(prefix))
        {
            return $"{prefix.Trim()}{number}{extension}";
        }

        var body = Path.GetFileNameWithoutExtension(name);
        return $"{body} ({number}){extension}";
    }

    /// <summary>
    /// 연번을 본 이름의 앞이나 뒤에 구분자와 함께 붙입니다. 확장자는 건드리지 않습니다.
    /// 예: 앞 + "_" → "01_원래이름.pdf", 뒤 + "-" → "원래이름-01.pdf".
    /// </summary>
    public static string Numbered(string name, int index, int digits, bool atFront, string? separator)
    {
        var extension = Path.GetExtension(name);
        var body = Path.GetFileNameWithoutExtension(name);

        // 숨김 파일처럼 본 이름이 없는 경우입니다. 그때는 이름 전체를 본 이름으로 봅니다.
        if (body.Length == 0)
        {
            body = name;
            extension = string.Empty;
        }

        var number = index.ToString(CultureInfo.InvariantCulture)
            .PadLeft(Math.Clamp(digits, MinDigits, MaxDigits), '0');
        var gap = separator ?? string.Empty;

        if (body.Length == 0)
        {
            return number + extension;
        }

        return atFront ? $"{number}{gap}{body}{extension}" : $"{body}{gap}{number}{extension}";
    }

    /// <summary>개수에 맞는 연번 자릿수입니다. 열 개면 두 자리, 백 개면 세 자리입니다.</summary>
    public static int DigitsFor(int count) =>
        count switch
        {
            < 10 => 1,
            < 100 => 2,
            < 1000 => 3,
            < 10000 => 4,
            _ => 5
        };

    public static string ToLowerName(string name) => name.ToLowerInvariant();

    public static string ToUpperName(string name) => name.ToUpperInvariant();

    /// <summary>
    /// 줄마다 무슨 문제가 있는지 봅니다. 차례는 준 그대로입니다.
    /// 그 폴더에 이미 있는 이름인지까지는 보지 않습니다. 그건 디스크를 봐야 아는 일입니다.
    /// </summary>
    public static RenameProblem[] Check(IReadOnlyList<(string Folder, string OldName, string NewName)> rows)
    {
        var problems = new RenameProblem[rows.Count];
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < rows.Count; i++)
        {
            var (folder, _, newName) = rows[i];
            var name = (newName ?? string.Empty).Trim();

            if (name.Length == 0)
            {
                problems[i] |= RenameProblem.Empty;
                continue;
            }

            if (name.IndexOfAny(BadCharacters.ToCharArray()) >= 0)
            {
                problems[i] |= RenameProblem.BadCharacter;
            }

            var key = $"{folder}|{name}";

            if (seen.TryGetValue(key, out var first))
            {
                problems[i] |= RenameProblem.Duplicate;
                problems[first] |= RenameProblem.Duplicate;
                continue;
            }

            seen[key] = i;
        }

        return problems;
    }

    /// <summary>문제를 사람이 읽을 한 마디로 옮깁니다.</summary>
    public static string Describe(RenameProblem problem)
    {
        if (problem == RenameProblem.None)
        {
            return string.Empty;
        }

        var parts = new List<string>(4);

        if ((problem & RenameProblem.Empty) != 0)
        {
            parts.Add("이름이 비었습니다");
        }

        if ((problem & RenameProblem.BadCharacter) != 0)
        {
            parts.Add("쓸 수 없는 글자");
        }

        if ((problem & RenameProblem.Duplicate) != 0)
        {
            parts.Add("서로 겹칩니다");
        }

        if ((problem & RenameProblem.Exists) != 0)
        {
            parts.Add("이미 있는 이름");
        }

        return string.Join(" · ", parts);
    }
}
