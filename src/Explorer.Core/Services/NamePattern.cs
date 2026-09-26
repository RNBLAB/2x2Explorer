using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Explorer.Core.Services;

/// <summary>이름을 이루는 요소입니다. 목표 패턴에서 이 요소들을 골라 차례대로 잇습니다.</summary>
public enum NamePart
{
    Sequence,
    Date,
    DocNumber,
    Project,
    Body,
    Version,
    Author,
    Note
}

/// <summary>이름 하나를 요소로 나눈 결과입니다. 창에서 칸마다 고칠 수 있게 모두 글자로 둡니다.</summary>
public sealed class NameParts
{
    /// <summary>원래 이름(확장자 포함)입니다.</summary>
    public string Original { get; set; } = string.Empty;

    /// <summary>확장자입니다. 점을 포함합니다(".pdf"). 폴더나 확장자 없는 이름이면 빈 칸입니다.</summary>
    public string Extension { get; set; } = string.Empty;

    /// <summary>★ 같은 맨 앞 표시입니다.</summary>
    public string Marker { get; set; } = string.Empty;

    public string Sequence { get; set; } = string.Empty;

    /// <summary>날짜입니다. 읽은 날짜는 yyyyMMdd 로 담아 두고, 조립할 때 고른 형식으로 바꿉니다.</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>날짜에 딸린 꼬리입니다. 기간("~12")과 요일("(화)")입니다.</summary>
    public string DateSuffix { get; set; } = string.Empty;

    public string DocNumber { get; set; } = string.Empty;

    public string Project { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    /// <summary>버전 숫자입니다("2.3.0"). 앞말(v·ver)은 떼고 담습니다.</summary>
    public string Version { get; set; } = string.Empty;

    public string Author { get; set; } = string.Empty;

    /// <summary>비고입니다. 괄호말이 여럿이면 " · " 로 잇습니다.</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>프로젝트명을 어구 규칙으로 짐작했는지입니다(확신 낮음).</summary>
    public bool ProjectGuessed { get; set; }

    /// <summary>작성자를 이름꼴로 짐작했는지입니다(확신 낮음).</summary>
    public bool AuthorGuessed { get; set; }

    public string Get(NamePart part) => part switch
    {
        NamePart.Sequence => Sequence,
        NamePart.Date => Date,
        NamePart.DocNumber => DocNumber,
        NamePart.Project => Project,
        NamePart.Body => Body,
        NamePart.Version => Version,
        NamePart.Author => Author,
        NamePart.Note => Note,
        _ => string.Empty
    };
}

/// <summary>목표 패턴입니다. 요소 차례, 켜고 끈 것, 구분자와 요소별 형식입니다.</summary>
public sealed class NameTemplate
{
    /// <summary>
    /// 기본 차례입니다(사용자 지정 2026-09-26): 순번-날짜-프로젝트명-본문-버전-작성자-비고.
    /// 문서번호는 사용자 차례에 없던 요소라 날짜 바로 뒤에 둡니다. 끄면 이름에서 번호가 사라지므로 기본은 켭니다.
    /// </summary>
    public static readonly NamePart[] DefaultOrder =
    {
        NamePart.Sequence, NamePart.Date, NamePart.DocNumber, NamePart.Project,
        NamePart.Body, NamePart.Version, NamePart.Author, NamePart.Note
    };

    public List<NamePart> Order { get; set; } = new(DefaultOrder);

    public HashSet<NamePart> Enabled { get; set; } = new(DefaultOrder);

    /// <summary>요소 사이 구분자입니다. 기본 밑줄, 띄어쓰기·대시를 고를 수 있습니다.</summary>
    public string Separator { get; set; } = "_";

    /// <summary>날짜 형식입니다(.NET 날짜 형식 문자열). 기본 yyMMdd.</summary>
    public string DateFormat { get; set; } = "yyMMdd";

    /// <summary>버전 앞말입니다. "v" → v2.1, "ver" → ver2.1.</summary>
    public string VersionPrefix { get; set; } = "v";

    /// <summary>순번 자릿수입니다. 0 이면 원래 적힌 대로 둡니다.</summary>
    public int SequenceDigits { get; set; }

    /// <summary>★ 같은 맨 앞 표시를 남길지입니다. 목록 맨 위로 올리려고 붙인 것이라 기본은 남깁니다.</summary>
    public bool KeepMarker { get; set; } = true;

    /// <summary>비고를 괄호로 감쌀지입니다.</summary>
    public bool NoteInParentheses { get; set; } = true;

    public bool LowerExtension { get; set; } = true;
}

/// <summary>
/// 이름을 요소로 나누고(분석), 목표 패턴으로 다시 잇습니다(조립).
///
/// 판별 순위(사용자 지정 2026-09-26): 숫자 덩어리가 여러 요소로 읽힐 수 있으면 순번 → 버전 → 날짜 차례로 먼저 맞는 쪽이 가져갑니다.
/// 문서번호(하이픈으로 이은 숫자 묶음 셋 이상)는 날짜와 다른 요소라, 그 안의 숫자가 날짜로 잡히지 않도록 가장 먼저 떼어 냅니다.
/// 프로젝트명·작성자는 형식이 없어 어구 규칙으로 짐작만 하고 짐작한 표시를 남깁니다. 창에서 사람이 고칩니다.
/// 날짜가 없는 이름은 날짜를 비워 둡니다(파일 날짜로 채우지 않음, 사용자 지정).
/// </summary>
public static class NamePattern
{
    private const string Seps = @"[\s_\-.]";

    private static readonly Regex DocNumberRegex = new(
        @"(?<![A-Za-z0-9])([A-Za-z]{0,3}\d{2,8}(?:-\d{1,6}){2,})(?![\d-])", RegexOptions.Compiled);

    private static readonly Regex IsoDateOnly = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);

    private static readonly Regex LeadingSequence = new(
        @"^\s*(?:\((\d{1,4})\)|(\d{1,4}))(?=[\s_\-.)]|$)[\s_\-.)]*", RegexOptions.Compiled);

    private static readonly Regex TrailingSequence = new(
        @"[\s_\-]+(?:\((\d{1,4})\)|(\d{1,4}))\s*$", RegexOptions.Compiled);

    private static readonly Regex VersionRegex = new(
        @"(?<![A-Za-z])(?:v|ver|rev|version)\.?\s?(\d+(?:\.\d+)*)(?![\d])", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DateSeparated = new(
        @"(?<!\d)(\d{4}|\d{2})([.\-/])(\d{1,2})\2(\d{1,2})(?!\d)", RegexOptions.Compiled);

    private static readonly Regex DateCompact = new(@"(?<!\d)(\d{8}|\d{6})(?!\d)", RegexOptions.Compiled);

    private static readonly Regex DateTail = new(
        @"^(?:~\s?\d{1,8})?(?:\s?\([월화수목금토일]\))?", RegexOptions.Compiled);

    private static readonly Regex Brackets = new(@"\(([^()]*)\)|\[([^\[\]]*)\]|【([^【】]*)】", RegexOptions.Compiled);

    /// <summary>작성자 이름꼴: 세 글자(한글·한자)입니다. 두 글자는 "수정"·"책자" 같은 낱말과 가를 수 없어 받지 않습니다.</summary>
    private static readonly Regex AuthorLike = new(@"^[가-힣一-龥]{3}$", RegexOptions.Compiled);

    /// <summary>문서 이름에 흔한 끝 글자입니다(견적서·회의록·요약본·일람표·설명회·조안 …). 이 글자로 끝나면 작성자로 보지 않습니다.</summary>
    private const string DocumentEndings = "서록본표회안집도문건철부장지";

    private static readonly Regex ProjectWord = new(
        @"(\d+구역|(?<!제)\d+차|재건축|재개발|정비사업|단지|타운|아파트)", RegexOptions.Compiled);

    private static readonly HashSet<string> DongStopWords = new(StringComparer.Ordinal)
    {
        "자동", "이동", "활동", "변동", "공동", "행동", "운동", "연동", "작동", "가동", "협동", "출동", "노동", "진동", "유동", "파동", "감동"
    };

    private const string MarkerChars = "★☆※●◆■▶";

    /// <summary>이름 여럿을 한꺼번에 나눕니다. 절반 이상에서 같은 프로젝트명이 나오면 그 이름들의 짐작 표시를 거둡니다.</summary>
    public static List<NameParts> Analyze(IReadOnlyList<string> names, IReadOnlyList<bool>? isFolder = null)
    {
        var result = new List<NameParts>(names.Count);

        for (var i = 0; i < names.Count; i++)
        {
            result.Add(Parse(names[i], isFolder is not null && i < isFolder.Count && isFolder[i]));
        }

        if (result.Count >= 3)
        {
            foreach (var group in result.Where(p => p.Project.Length > 0).GroupBy(p => p.Project, StringComparer.Ordinal))
            {
                if (group.Count() * 2 >= result.Count)
                {
                    foreach (var parts in group)
                    {
                        parts.ProjectGuessed = false;
                    }
                }
            }
        }

        return result;
    }

    /// <summary>이름 하나를 요소로 나눕니다.</summary>
    public static NameParts Parse(string name, bool isFolder = false)
    {
        var parts = new NameParts { Original = name };
        var text = (name ?? string.Empty).Trim();

        if (!isFolder)
        {
            var extension = Path.GetExtension(text);

            // 확장자처럼 보여도 길거나 띄어쓰기가 있으면 이름의 일부입니다("2024.09.26" 의 ".26" 은 확장자가 아님).
            if (extension.Length is > 1 and <= 6 && !extension.Contains(' ') && !extension.Skip(1).All(char.IsDigit))
            {
                parts.Extension = extension;
                text = text[..^extension.Length];
            }
        }

        text = text.Normalize(NormalizationForm.FormC);

        // 맨 앞 표시(★ 등)
        var marker = new StringBuilder();

        while (text.Length > 0 && (MarkerChars.Contains(text[0]) || text[0] == ' '))
        {
            if (text[0] != ' ')
            {
                marker.Append(text[0]);
            }

            text = text[1..];
        }

        parts.Marker = marker.ToString();

        // 0. 문서번호 — 그 안의 숫자가 날짜·순번으로 잡히지 않게 가장 먼저 뗍니다. 완전한 yyyy-MM-dd 는 날짜로 남깁니다.
        foreach (Match match in DocNumberRegex.Matches(text))
        {
            if (IsoDateOnly.IsMatch(match.Value) && TryDate(match.Value[..4], match.Value[5..7], match.Value[8..10], out _))
            {
                continue;
            }

            parts.DocNumber = match.Value;

            // 같은 번호가 두 번 적힌 이름이 있습니다("251205-00-018_251205-00-018 …"). 모두 뗍니다.
            text = text.Replace(match.Value, " _ ", StringComparison.Ordinal);
            break;
        }

        // 1. 순번 — 이름 맨 앞이나 맨 뒤의 1~4자리 숫자(괄호 포함)
        var lead = LeadingSequence.Match(text);

        if (lead.Success)
        {
            parts.Sequence = lead.Groups[1].Success ? lead.Groups[1].Value : lead.Groups[2].Value;
            text = text[lead.Length..];
        }
        else
        {
            var tail = TrailingSequence.Match(text);

            if (tail.Success)
            {
                parts.Sequence = tail.Groups[1].Success ? tail.Groups[1].Value : tail.Groups[2].Value;
                text = Cut(text, tail.Index, tail.Length);
            }
        }

        // 2. 버전
        var version = VersionRegex.Match(text);

        if (version.Success)
        {
            parts.Version = version.Groups[1].Value;
            text = Cut(text, version.Index, version.Length);
        }

        // 3. 날짜(기간·요일 꼬리 포함). 달력에 있는 날짜만 받습니다. 첫 날짜 하나만 뗍니다.
        if (TakeDate(ref text, out var date, out var suffix))
        {
            parts.Date = date;
            parts.DateSuffix = suffix;
        }

        // 괄호말 → 비고
        var notes = new List<string>();

        text = Brackets.Replace(text, match =>
        {
            var inner = (match.Groups[1].Success ? match.Groups[1].Value
                : match.Groups[2].Success ? match.Groups[2].Value
                : match.Groups[3].Value).Trim();

            if (inner.Length > 0)
            {
                notes.Add(inner);
            }

            return " _ ";
        });

        parts.Note = string.Join(" · ", notes);

        // 남은 글을 밑줄 단위 조각으로 나눕니다.
        var segments = SplitSegments(text);

        // 작성자 — 맨 끝 조각(또는 여러 낱말로 된 맨 끝 조각의 마지막 낱말)이 세 글자 이름꼴이면 짐작합니다.
        if (segments.Count >= 2 && IsAuthorLike(segments[^1]))
        {
            parts.Author = segments[^1];
            parts.AuthorGuessed = true;
            segments.RemoveAt(segments.Count - 1);
        }
        else if (segments.Count >= 1 && segments[^1].LastIndexOf(' ') is var space and > 0
            && IsAuthorLike(segments[^1][(space + 1)..]))
        {
            parts.Author = segments[^1][(space + 1)..];
            parts.AuthorGuessed = true;
            segments[^1] = segments[^1][..space];
        }

        // 프로젝트명 — 구역·차·재건축·단지·타운·○○동 같은 어구가 든 조각
        for (var i = 0; i < segments.Count; i++)
        {
            var words = segments[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var hit = Array.FindIndex(words, IsProjectWord);

            if (hit < 0)
            {
                continue;
            }

            if (segments.Count > 1)
            {
                // 조각이 여럿이면 그 조각 전체를 프로젝트명으로 봅니다("가람동 청년주택").
                parts.Project = segments[i];
                segments.RemoveAt(i);
            }
            else
            {
                // 조각이 하나뿐이면 앞에서부터 그 어구까지를 프로젝트명, 나머지를 본문으로 봅니다("한빛 전면 제1구역 | 미니차트").
                parts.Project = string.Join(' ', words.Take(hit + 1));
                segments[i] = string.Join(' ', words.Skip(hit + 1));
            }

            parts.ProjectGuessed = true;
            break;
        }

        parts.Body = string.Join('_', segments.Where(segment => segment.Length > 0));
        return parts;
    }

    private static bool IsAuthorLike(string word) =>
        AuthorLike.IsMatch(word) && !DocumentEndings.Contains(word[^1]);

    private static bool IsProjectWord(string word)
    {
        if (ProjectWord.IsMatch(word))
        {
            return true;
        }

        // ○○동(석관동·사당동). 두 글자 낱말(자동·이동 …)과 흔한 낱말은 뺍니다.
        return word.Length >= 3 && word.EndsWith('동') && !DongStopWords.Contains(word[^2..])
            && word.All(letter => letter is >= '가' and <= '힣' || char.IsDigit(letter));
    }

    private static List<string> SplitSegments(string text)
    {
        var list = new List<string>();

        foreach (var piece in text.Split('_'))
        {
            // 조각 가장자리의 구분자 찌꺼기와 겹친 공백을 정리합니다.
            var tidy = Regex.Replace(piece, @"\s+", " ").Trim(' ', '-', '.', '~', ',');

            if (tidy.Length > 0)
            {
                list.Add(tidy);
            }
        }

        return list;
    }

    private static string Cut(string text, int index, int length) =>
        text[..index] + " _ " + text[(index + length)..];

    /// <summary>
    /// 사람이 고쳐 적은 날짜 칸을 읽습니다. "2026-07-16", "260716~18(화)" 처럼 적으면 날짜(yyyyMMdd)와 꼬리로 나눕니다.
    /// 날짜로 읽히지 않으면 false 입니다(그때 창은 적힌 글자를 그대로 씁니다).
    /// </summary>
    public static bool TryReadDate(string text, out string date, out string suffix)
    {
        var work = text ?? string.Empty;
        return TakeDate(ref work, out date, out suffix) && work.Trim(' ', '_').Length == 0;
    }

    /// <summary>날짜(yyyyMMdd)와 꼬리를 창에 보일 글자로 만듭니다.</summary>
    public static string ShowDate(string date, string suffix) =>
        DateTime.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + suffix
            : date + suffix;

    private static bool TakeDate(ref string text, out string date, out string suffix)
    {
        date = string.Empty;
        suffix = string.Empty;

        foreach (var regex in new[] { DateSeparated, DateCompact })
        {
            foreach (Match match in regex.Matches(text))
            {
                string year, month, day;

                if (regex == DateSeparated)
                {
                    (year, month, day) = (match.Groups[1].Value, match.Groups[3].Value, match.Groups[4].Value);
                }
                else if (match.Value.Length == 8)
                {
                    (year, month, day) = (match.Value[..4], match.Value[4..6], match.Value[6..]);
                }
                else
                {
                    (year, month, day) = (match.Value[..2], match.Value[2..4], match.Value[4..]);
                }

                if (!TryDate(year, month, day, out var value))
                {
                    continue;
                }

                var end = match.Index + match.Length;
                var tail = DateTail.Match(text[end..]);
                date = value.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                suffix = tail.Value.Replace(" ", string.Empty, StringComparison.Ordinal);
                text = Cut(text, match.Index, match.Length + tail.Length);
                return true;
            }
        }

        return false;
    }

    private static bool TryDate(string year, string month, string day, out DateTime value)
    {
        value = default;

        if (!int.TryParse(year, out var y) || !int.TryParse(month, out var m) || !int.TryParse(day, out var d))
        {
            return false;
        }

        if (year.Length == 2)
        {
            y += 2000;
        }

        if (y is < 1980 or > 2099 || m is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, m))
        {
            return false;
        }

        value = new DateTime(y, m, d);
        return true;
    }

    /// <summary>나눈 요소를 목표 패턴대로 잇습니다. 빈 요소는 구분자까지 빼서 구분자가 겹치지 않게 합니다.</summary>
    public static string Compose(NameParts parts, NameTemplate template)
    {
        var pieces = new List<string>();

        foreach (var part in template.Order)
        {
            if (!template.Enabled.Contains(part))
            {
                continue;
            }

            var value = Format(parts, part, template).Trim();

            if (value.Length > 0)
            {
                pieces.Add(value);
            }
        }

        var body = string.Join(template.Separator, pieces);

        if (template.KeepMarker && parts.Marker.Length > 0)
        {
            body = parts.Marker + body;
        }

        // 윈도우가 받지 않는 글자는 뺍니다.
        foreach (var bad in RenameRules.BadCharacters)
        {
            body = body.Replace(bad.ToString(), string.Empty, StringComparison.Ordinal);
        }

        body = Regex.Replace(body, @"\s+", " ").Trim();

        if (body.Length == 0)
        {
            return parts.Original;
        }

        var extension = template.LowerExtension ? parts.Extension.ToLowerInvariant() : parts.Extension;
        return body + extension;
    }

    private static string Format(NameParts parts, NamePart part, NameTemplate template)
    {
        switch (part)
        {
            case NamePart.Date:
                if (parts.Date.Length == 0)
                {
                    return string.Empty;
                }

                if (DateTime.TryParseExact(parts.Date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    return date.ToString(template.DateFormat, CultureInfo.InvariantCulture) + parts.DateSuffix;
                }

                // 사람이 칸을 고쳐 yyyyMMdd 가 아니게 되었으면 적힌 그대로 씁니다.
                return parts.Date + parts.DateSuffix;

            case NamePart.Sequence:
                if (template.SequenceDigits > 0 && int.TryParse(parts.Sequence, out var number))
                {
                    return number.ToString(CultureInfo.InvariantCulture).PadLeft(template.SequenceDigits, '0');
                }

                return parts.Sequence;

            case NamePart.Version:
                return parts.Version.Length == 0 ? string.Empty : template.VersionPrefix + parts.Version;

            case NamePart.Note:
                return parts.Note.Length == 0 ? string.Empty
                    : template.NoteInParentheses ? $"({parts.Note})" : parts.Note;

            case NamePart.Body:
                // 본문 안의 조각 사이(원래 밑줄)도 고른 구분자로 맞춥니다.
                return parts.Body.Replace("_", template.Separator, StringComparison.Ordinal);

            default:
                return parts.Get(part);
        }
    }

    /// <summary>요소를 사람이 읽을 이름으로 옮깁니다.</summary>
    public static string Label(NamePart part) => part switch
    {
        NamePart.Sequence => "순번",
        NamePart.Date => "날짜",
        NamePart.DocNumber => "문서번호",
        NamePart.Project => "프로젝트명",
        NamePart.Body => "본문(파일명)",
        NamePart.Version => "버전",
        NamePart.Author => "작성자",
        NamePart.Note => "비고",
        _ => part.ToString()
    };
}
