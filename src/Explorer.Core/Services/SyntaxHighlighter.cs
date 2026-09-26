using System;
using System.Collections.Generic;
using Explorer.Core.Models;

namespace Explorer.Core.Services;

/// <summary>
/// 코드 미리보기에 색을 입힐 구간을 찾습니다.
///
/// 제대로 된 구문 분석이 아닙니다. 주석·글월·수·미리 잡아 둔 낱말 넷만 가려냅니다.
/// 미리보기는 읽기만 하는 자리라 이 정도로 충분하고, 바깥 꾸러미를 들이지 않아도 됩니다.
/// 편집기를 만들 생각이라면 그때는 제대로 된 것을 붙여야 합니다.
/// </summary>
public static class SyntaxHighlighter
{
    /// <summary>이보다 긴 글은 칠하지 않습니다. 구간이 수만 개가 되면 그리는 데 더 오래 걸립니다.</summary>
    public const int MaxLength = 120_000;

    private enum Family
    {
        None,
        CLike,
        Script,
        Markup
    }

    /// <summary>
    /// 중괄호를 쓰는 말들의 낱말입니다.
    /// 말마다 목록을 따로 두면 정확하지만, 미리보기에서는 한 벌로 묶어도 눈에 거슬리지 않습니다.
    /// </summary>
    private static readonly HashSet<string> CLikeWords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch",
        "char", "class", "const", "continue", "decimal", "default", "delegate", "delete", "do", "double",
        "else", "enum", "event", "explicit", "export", "extends", "extern", "false", "finally", "fixed",
        "float", "fn", "for", "foreach", "from", "func", "function", "goto", "if", "impl",
        "implements", "import", "in", "instanceof", "int", "interface", "internal", "is", "let", "lock",
        "long", "match", "mod", "mut", "namespace", "new", "null", "object", "operator", "out",
        "override", "package", "params", "private", "protected", "pub", "public", "readonly", "record", "ref",
        "return", "sealed", "short", "sizeof", "static", "string", "struct", "switch", "this", "throw",
        "trait", "true", "try", "type", "typeof", "uint", "ulong", "unsafe", "use", "ushort",
        "using", "var", "virtual", "void", "while", "yield"
    };

    /// <summary>들여쓰기로 묶는 말들과 셸 쪽 낱말입니다.</summary>
    private static readonly HashSet<string> ScriptWords = new(StringComparer.Ordinal)
    {
        "and", "as", "break", "case", "class", "continue", "def", "do", "echo", "elif",
        "else", "end", "esac", "except", "exit", "export", "fi", "finally", "for", "from",
        "function", "global", "if", "import", "in", "is", "lambda", "local", "None", "not",
        "or", "pass", "raise", "return", "self", "set", "then", "True", "False", "try",
        "unset", "until", "while", "with", "yield"
    };

    private static readonly HashSet<string> CLikeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".c", ".h", ".cpp", ".hpp", ".cc", ".java", ".js", ".ts", ".jsx",
        ".tsx", ".go", ".rs", ".php", ".css", ".json", ".sql", ".kt", ".swift", ".m"
    };

    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".rb", ".sh", ".bash", ".zsh", ".ps1", ".psm1", ".yml", ".yaml", ".ini",
        ".cfg", ".conf", ".toml", ".bat", ".cmd", ".editorconfig", ".gitignore"
    };

    private static readonly HashSet<string> MarkupExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xml", ".html", ".htm", ".xaml", ".svg", ".csproj", ".props", ".targets", ".config", ".resx",
        ".xsd", ".xsl", ".plist"
    };

    /// <summary>이 확장자를 칠할 수 있는지입니다.</summary>
    public static bool Supports(string? extension) => FamilyOf(extension) != Family.None;

    /// <summary>칠할 구간을 앞에서 뒤로 차례대로 돌려줍니다. 칠할 것이 없으면 빈 목록입니다.</summary>
    public static IReadOnlyList<CodeSpan> Highlight(string? text, string? extension)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength)
        {
            return Array.Empty<CodeSpan>();
        }

        return FamilyOf(extension) switch
        {
            Family.CLike => ScanCLike(text),
            Family.Script => ScanScript(text),
            Family.Markup => ScanMarkup(text),
            _ => Array.Empty<CodeSpan>()
        };
    }

    private static Family FamilyOf(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return Family.None;
        }

        // 확장자가 없는 이름(.gitignore 같은 것)도 들어옵니다.
        var key = extension.StartsWith('.') ? extension : "." + extension;

        if (CLikeExtensions.Contains(key))
        {
            return Family.CLike;
        }

        if (ScriptExtensions.Contains(key))
        {
            return Family.Script;
        }

        return MarkupExtensions.Contains(key) ? Family.Markup : Family.None;
    }

    private static IReadOnlyList<CodeSpan> ScanCLike(string text)
    {
        var spans = new List<CodeSpan>();
        var index = 0;

        while (index < text.Length)
        {
            var letter = text[index];

            // 줄 주석입니다.
            if (letter == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                var end = LineEnd(text, index);
                spans.Add(new CodeSpan(index, end - index, CodeSpanKind.Comment));
                index = end;
                continue;
            }

            // 여러 줄 주석입니다.
            if (letter == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                var close = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
                var end = close < 0 ? text.Length : close + 2;
                spans.Add(new CodeSpan(index, end - index, CodeSpanKind.Comment));
                index = end;
                continue;
            }

            if (letter is '"' or '\'' or '`')
            {
                index = ReadQuoted(text, index, spans, escapes: true);
                continue;
            }

            if (char.IsDigit(letter) && IsWordEdge(text, index))
            {
                index = ReadNumber(text, index, spans);
                continue;
            }

            if (IsWordStart(letter))
            {
                index = ReadWord(text, index, spans, CLikeWords);
                continue;
            }

            index++;
        }

        return spans;
    }

    private static IReadOnlyList<CodeSpan> ScanScript(string text)
    {
        var spans = new List<CodeSpan>();
        var index = 0;

        while (index < text.Length)
        {
            var letter = text[index];

            // # 과 ; 둘 다 주석으로 봅니다. 파이썬·셸은 앞의 것을, 설정 파일은 뒤의 것을 씁니다.
            if (letter is '#' or ';')
            {
                var end = LineEnd(text, index);
                spans.Add(new CodeSpan(index, end - index, CodeSpanKind.Comment));
                index = end;
                continue;
            }

            // 배치 파일의 rem 입니다. 줄 첫머리에 올 때만 주석입니다.
            if ((letter is 'r' or 'R') && AtLineStart(text, index) && IsRemark(text, index))
            {
                var end = LineEnd(text, index);
                spans.Add(new CodeSpan(index, end - index, CodeSpanKind.Comment));
                index = end;
                continue;
            }

            if (letter is '"' or '\'')
            {
                index = ReadQuoted(text, index, spans, escapes: false);
                continue;
            }

            if (char.IsDigit(letter) && IsWordEdge(text, index))
            {
                index = ReadNumber(text, index, spans);
                continue;
            }

            if (IsWordStart(letter))
            {
                index = ReadWord(text, index, spans, ScriptWords);
                continue;
            }

            index++;
        }

        return spans;
    }

    private static IReadOnlyList<CodeSpan> ScanMarkup(string text)
    {
        var spans = new List<CodeSpan>();
        var index = 0;

        while (index < text.Length)
        {
            if (text[index] != '<')
            {
                index++;
                continue;
            }

            // 주석입니다.
            if (Starts(text, index, "<!--"))
            {
                var close = text.IndexOf("-->", index + 4, StringComparison.Ordinal);
                var end = close < 0 ? text.Length : close + 3;
                spans.Add(new CodeSpan(index, end - index, CodeSpanKind.Comment));
                index = end;
                continue;
            }

            var tagStart = index;
            index++;

            if (index < text.Length && (text[index] == '/' || text[index] == '?' || text[index] == '!'))
            {
                index++;
            }

            while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] is '_' or '-' or ':' or '.'))
            {
                index++;
            }

            if (index > tagStart + 1)
            {
                spans.Add(new CodeSpan(tagStart, index - tagStart, CodeSpanKind.Keyword));
            }

            // 태그 안쪽입니다. 따옴표로 묶인 값만 따로 칠합니다.
            while (index < text.Length && text[index] != '>')
            {
                if (text[index] is '"' or '\'')
                {
                    index = ReadQuoted(text, index, spans, escapes: false);
                    continue;
                }

                index++;
            }

            if (index < text.Length)
            {
                spans.Add(new CodeSpan(index, 1, CodeSpanKind.Keyword));
                index++;
            }
        }

        return spans;
    }

    /// <summary>따옴표로 묶인 자리를 한 구간으로 잡습니다. 닫는 따옴표가 없으면 줄 끝까지입니다.</summary>
    private static int ReadQuoted(string text, int start, List<CodeSpan> spans, bool escapes)
    {
        var quote = text[start];
        var index = start + 1;

        while (index < text.Length)
        {
            var letter = text[index];

            if (escapes && letter == '\\' && index + 1 < text.Length)
            {
                index += 2;
                continue;
            }

            if (letter == quote)
            {
                index++;
                break;
            }

            // 줄이 바뀌도록 닫히지 않았으면 거기서 끊습니다. 따옴표 하나 때문에 뒤가 다 물들지 않게 합니다.
            if (letter is '\n' or '\r')
            {
                break;
            }

            index++;
        }

        spans.Add(new CodeSpan(start, index - start, CodeSpanKind.Quoted));
        return index;
    }

    private static int ReadNumber(string text, int start, List<CodeSpan> spans)
    {
        var index = start;

        while (index < text.Length
               && (char.IsLetterOrDigit(text[index]) || text[index] == '.')
               && !(text[index] == '.' && index + 1 < text.Length && text[index + 1] == '.'))
        {
            index++;
        }

        spans.Add(new CodeSpan(start, index - start, CodeSpanKind.Number));
        return index;
    }

    private static int ReadWord(string text, int start, List<CodeSpan> spans, HashSet<string> words)
    {
        var index = start;

        while (index < text.Length && IsWordPart(text[index]))
        {
            index++;
        }

        if (words.Contains(text[start..index]))
        {
            spans.Add(new CodeSpan(start, index - start, CodeSpanKind.Keyword));
        }

        return index;
    }

    private static int LineEnd(string text, int start)
    {
        var index = text.IndexOf('\n', start);
        return index < 0 ? text.Length : index;
    }

    private static bool AtLineStart(string text, int index)
    {
        for (var back = index - 1; back >= 0; back--)
        {
            if (text[back] is '\n' or '\r')
            {
                return true;
            }

            if (text[back] is not (' ' or '\t'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsRemark(string text, int index) =>
        Starts(text, index, "rem ") || Starts(text, index, "REM ") || Starts(text, index, "Rem ");

    private static bool Starts(string text, int index, string what) =>
        index + what.Length <= text.Length && string.CompareOrdinal(text, index, what, 0, what.Length) == 0;

    private static bool IsWordStart(char letter) => char.IsLetter(letter) || letter is '_' or '$';

    private static bool IsWordPart(char letter) => char.IsLetterOrDigit(letter) || letter is '_' or '$';

    /// <summary>낱말 한가운데의 숫자를 수로 잘못 보지 않게 앞 글자를 살핍니다.</summary>
    private static bool IsWordEdge(string text, int index) => index == 0 || !IsWordPart(text[index - 1]);
}
