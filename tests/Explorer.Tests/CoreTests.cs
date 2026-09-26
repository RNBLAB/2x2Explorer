using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Explorer.Core.Models;
using Explorer.Core.Services;
using Explorer.Core.Settings;

namespace Explorer.Tests;

/// <summary>Explorer.Core 의 규칙들이 그대로인지 봅니다. 창을 띄우지 않고 도는 것만 다룹니다.</summary>
public static class CoreTests
{
    public static void Run()
    {
        Names();
        Sizes();
        Order();
        Columns();
        Settings();
        Hangul();
        Paths();
        Code();
        Finding();
        Renaming();
        Sizing();
        History();
        Printing();
        Imaging();
        HwpPreviews();
        NamePatterns();
    }

    private static void NamePatterns()
    {
        Check.Group("이름 패턴 (정규화)");

        var a = NamePattern.Parse("260716_한빛3구역_조합설립인가_추정분담금_용역계약서(조안).hwp");
        Check.Equal("20260716", a.Date, "yyMMdd 날짜");
        Check.Equal("한빛3구역", a.Project, "○구역은 프로젝트명");
        Check.Equal("조합설립인가_추정분담금_용역계약서", a.Body, "나머지는 본문");
        Check.Equal("조안", a.Note, "괄호말은 비고");

        var b = NamePattern.Parse("R260406-00-001_가격작업(무상양수도 - 새들2구역).xlsx");
        Check.Equal("R260406-00-001", b.DocNumber, "문서번호를 먼저 뗍니다");
        Check.Equal(string.Empty, b.Date, "문서번호 안의 숫자는 날짜가 아닙니다");

        var c = NamePattern.Parse("20250113_소속-경력2025-01-001_경력증명서 김하늘.pdf");
        Check.Equal("2025-01-001", c.DocNumber, "끝 칸이 세 자리면 날짜가 아니라 문서번호");
        Check.Equal("20250113", c.Date, "yyyyMMdd 날짜");
        Check.Equal("김하늘", c.Author, "끝의 세 글자 이름은 작성자로 짐작");
        Check.True(c.AuthorGuessed, "작성자는 짐작 표시");

        var d = NamePattern.Parse("★(최종완료)한빛22차 (사업시행계획변경)-2024.09.26.xlsx");
        Check.Equal("★", d.Marker, "맨 앞 표시");
        Check.Equal("20240926", d.Date, "점으로 나눈 날짜와 확장자를 가립니다");
        Check.Equal("한빛22차", d.Project, "○차는 프로젝트명");

        var e = NamePattern.Parse("★가격작업 _ 가람동 청년주택 _ ver2.3.0.xlsx");
        Check.Equal("2.3.0", e.Version, "ver 버전");
        Check.Equal("가람동 청년주택", e.Project, "○○동 조각은 프로젝트명");

        var f = NamePattern.Parse("250808~12(화)_ 푸른마을 재건축.hwp");
        Check.Equal("~12(화)", f.DateSuffix, "기간·요일은 날짜에 딸림");

        Check.Equal("01", NamePattern.Parse("01_회의록.docx").Sequence, "앞 순번");
        Check.Equal("2", NamePattern.Parse("회의록 (2).docx").Sequence, "뒤 괄호 순번");
        Check.Equal(string.Empty, NamePattern.Parse("새봄빌라_제1차추진위원회의_책자.pdf").Author, "두 글자 낱말·문서 끝 글자는 작성자가 아님");
        Check.Equal(string.Empty, NamePattern.Parse("새봄빌라_제1차추진위원회의_책자.pdf").Project, "제1차는 차수이지 프로젝트명이 아님");
        Check.Equal(string.Empty, NamePattern.Parse("260802_투자분석.xlsx").Sequence, "여섯 자리 숫자는 순번이 아님");

        Check.True(NamePattern.TryReadDate("2026-07-16", out var typed, out _) && typed == "20260716", "고쳐 적은 날짜 칸을 다시 읽습니다");
        Check.True(!NamePattern.TryReadDate("7월 중순", out _, out _), "날짜가 아닌 글은 날짜로 읽지 않습니다");

        var template = new NameTemplate();
        Check.Equal("260716_한빛3구역_조합설립인가_추정분담금_용역계약서_(조안).hwp", NamePattern.Compose(a, template), "기본 차례로 조립");
        Check.Equal("★240926_한빛22차_(최종완료 · 사업시행계획변경).xlsx", NamePattern.Compose(d, template), "빈 요소는 구분자까지 뺍니다");

        template.Separator = "-";
        template.DateFormat = "yyyy-MM-dd";
        Check.Equal("2026-07-16-한빛3구역-조합설립인가-추정분담금-용역계약서-(조안).hwp", NamePattern.Compose(a, template), "구분자·날짜 형식을 바꿉니다(본문 조각도 따라감)");

        template = new NameTemplate { Separator = " " };
        template.Enabled.Remove(NamePart.Note);
        Check.Equal("250113 2025-01-001 소속-경력 경력증명서 김하늘.pdf", NamePattern.Compose(c, template), "띄어쓰기 구분자, 비고 끄기");
        Check.Equal("★가람동 청년주택 가격작업 v2.3.0.xlsx", NamePattern.Compose(e, template), "버전은 v 앞말");
    }

    private static void Names()
    {
        Check.Group("이름 견주기");

        var comparer = NaturalStringComparer.Instance;

        Check.Before(comparer, "파일2", "파일10", "숫자는 수로 읽습니다");
        Check.Before(comparer, "a.txt", "B.txt", "대소문자를 가리지 않습니다");
        Check.Before(comparer, "사진", "사진2", "짧은 쪽이 앞입니다");
        Check.Equal(0, comparer.Compare("같음", "같음"), "같으면 0입니다");
        Check.Before(comparer, "x007", "x8", "앞의 0은 값에 넣지 않습니다");
    }

    private static void Sizes()
    {
        Check.Group("크기 적기");

        Check.Equal("512 B", ByteSizeFormatter.Format(512), "1024보다 작으면 바이트입니다");
        Check.Equal("1.0 KB", ByteSizeFormatter.Format(1024), "1024는 1.0 KB 입니다");
        Check.Equal("1.0 MB", ByteSizeFormatter.Format(1024L * 1024), "메가도 같습니다");
        Check.Equal(string.Empty, ByteSizeFormatter.Format(-1), "음수는 빈 칸입니다");
    }

    private static void Order()
    {
        Check.Group("목록 정렬");

        var folder = Entry("나중", isDirectory: true, size: 0, written: new DateTime(2020, 1, 1));
        var small = Entry("가장먼저", isDirectory: false, size: 10, written: new DateTime(2024, 1, 1));
        var big = Entry("중간", isDirectory: false, size: 20, written: new DateTime(2022, 1, 1));

        var byName = new FileSystemEntryComparer(SortColumn.Name, SortDirection.Ascending);
        Check.Before(byName, folder, small, "폴더가 파일보다 앞입니다");

        var down = new FileSystemEntryComparer(SortColumn.Name, SortDirection.Descending);
        Check.Before(down, folder, small, "내림차순이어도 폴더가 앞입니다");

        var bySize = new FileSystemEntryComparer(SortColumn.Size, SortDirection.Ascending);
        Check.Before(bySize, small, big, "크기가 작은 쪽이 앞입니다");

        var byNew = new FileSystemEntryComparer(SortColumn.CreationTime, SortDirection.Ascending);
        Check.True(byNew.Compare(small, big) != 0, "만든 날짜로도 가립니다");
    }

    private static void Columns()
    {
        Check.Group("열 설정");

        var defaults = ColumnSettings.CreateDefaults();
        Check.Equal(ColumnSettings.Catalog.Count, defaults.Count, "기본 열은 표에 적힌 수만큼입니다");

        var messy = new List<ColumnSettings>
        {
            new() { Key = "모르는열", Width = 100, Visible = true },
            new() { Key = "Name", Width = 5, Visible = false },
            new() { Key = "Size", Width = 123, Visible = true },
            new() { Key = "Size", Width = 200, Visible = true }
        };

        var tidy = ColumnSettings.Normalize(messy);

        Check.Equal(ColumnSettings.Catalog.Count, tidy.Count, "빠진 열은 뒤에 채웁니다");
        Check.Equal("Name", tidy[0].Key, "이름 열이 맨 앞입니다");
        Check.True(tidy[0].Visible, "이름 열은 끌 수 없습니다");
        Check.Equal(300d, tidy[0].Width, "너무 좁은 너비는 기본값으로 돌립니다");
        Check.Equal(123d, Find(tidy, "Size").Width, "쓸 만한 너비는 그대로 둡니다");
        Check.True(!tidy.Exists(column => column.Key == "모르는열"), "모르는 열은 버립니다");
        Check.Equal(1, Count(tidy, "Size"), "같은 열이 두 번 들어가지 않습니다");

        var empty = ColumnSettings.Normalize(new List<ColumnSettings>());
        Check.Equal(ColumnSettings.Catalog.Count, empty.Count, "비어 있으면 기본값입니다");
    }

    private static void Settings()
    {
        Check.Group("앱 설정 정리");

        var settings = new AppSettings();
        settings.Normalize();

        Check.Equal(AppSettings.PaneCount, settings.Panes.Count, "패널은 넷입니다");
        Check.Equal(ColumnSettings.Catalog.Count, settings.View.DetailColumns.Count, "열 구성도 함께 정리됩니다");

        var wild = new AppSettings { RowSplit = 5, ColumnSplit = -2 };
        wild.Normalize();

        Check.True(wild.RowSplit is >= 0.15 and <= 0.85, "위아래 나눔 비율을 가둡니다");
        Check.True(wild.ColumnSplit is >= 0.15 and <= 0.85, "좌우 나눔 비율을 가둡니다");

        Check.Equal(4, AppSettings.PaneCountFor(LayoutMode.Quad), "네 칸은 넷입니다");
        Check.Equal(2, AppSettings.PaneCountFor(LayoutMode.TwoRows), "위아래 두 칸은 둘입니다");
        Check.Equal(3, AppSettings.PaneCountFor(LayoutMode.TopOneBottomTwo), "상1하2는 셋입니다");

        var allPreview = new AppSettings();
        allPreview.Normalize();

        foreach (var pane in allPreview.Panes)
        {
            pane.Role = PaneRole.Preview;
        }

        allPreview.Normalize();
        Check.True(allPreview.Panes.Exists(pane => pane.Role == PaneRole.Files), "탐색기 칸이 적어도 하나는 남습니다");
    }

    private static void Hangul()
    {
        Check.Group("한글 글쇠");

        Check.Equal("ㄱ", HangulKeys.FromVirtualKey(0x52, shift: false), "R 은 ㄱ 입니다");
        Check.Equal("ㄲ", HangulKeys.FromVirtualKey(0x52, shift: true), "Shift+R 은 ㄲ 입니다");
        Check.Equal(null, HangulKeys.FromVirtualKey(0x30, shift: false), "글자 글쇠가 아니면 없습니다");

        Check.Equal("ㄱㅏ", HangulKeys.Decompose("가"), "받침 없는 글자를 풉니다");
        Check.Equal("ㅅㅏㅈㅣㄴ", HangulKeys.Decompose("사진"), "받침 있는 글자를 풉니다");
        Check.Equal("abc", HangulKeys.Decompose("ABC"), "영문은 소문자로 둡니다");

        Check.True(HangulKeys.StartsWithJamo("사진", "ㅅㅏ"), "자모로 친 앞부분을 잡습니다");
        Check.True(HangulKeys.StartsWithJamo("사진", "사"), "완성된 글자로도 잡습니다");
        Check.True(!HangulKeys.StartsWithJamo("사진", "ㅈ"), "앞이 다르면 잡지 않습니다");

        Check.True(HangulKeys.HasJamo("ㅅㅏ"), "자모가 섞였는지 봅니다");
        Check.True(!HangulKeys.HasJamo("사진"), "완성된 글자는 자모가 아닙니다");

        Check.Equal("ㅅㅈ", HangulKeys.FirstSounds("사진"), "글자마다 첫소리만 남깁니다");
        Check.Equal("ㄲㅊ", HangulKeys.FirstSounds("꽃차"), "된소리도 그대로 남깁니다");
        Check.Equal("abc", HangulKeys.FirstSounds("ABC"), "한글이 아니면 소문자로 둡니다");

        Check.True(HangulKeys.StartsWithJamo("사진", "ㅅㅈ"), "첫소리만 쳐도 잡습니다");
        Check.True(HangulKeys.StartsWithJamo("사진첩", "ㅅㅈ"), "뒤가 더 길어도 잡습니다");
        Check.True(!HangulKeys.StartsWithJamo("사진", "ㅈㅅ"), "첫소리 차례가 다르면 잡지 않습니다");
        Check.True(!HangulKeys.StartsWithJamo("수박", "사"), "완성된 글자는 첫소리로 풀지 않습니다");
        Check.True(!HangulKeys.StartsWithJamo("수박", "ㅅㅏ"), "홀소리가 섞이면 첫소리로 풀지 않습니다");
        Check.True(HangulKeys.StartsWithJamo("수박", "ㅅㅂ"), "닿소리만 이어 치면 첫소리로 풉니다");
    }

    private static void Paths()
    {
        Check.Group("경로 고르기");

        Check.Equal(string.Empty, PathHelper.Expand(null), "빈 경로는 빈 칸입니다");
        Check.True(PathHelper.Expand("%USERPROFILE%").Length > 0, "환경 변수를 펼칩니다");

        var root = Path.GetTempPath();
        var missing = Path.Combine(root, "없는폴더_" + Guid.NewGuid().ToString("N"), "더없는폴더");
        var found = PathHelper.ResolveExistingDirectory(missing);

        Check.True(Directory.Exists(found), "없는 폴더는 있는 상위 폴더로 올라갑니다");
        Check.True(!found.Contains("없는폴더_", StringComparison.Ordinal), "사라진 칸은 결과에 남지 않습니다");

        var fallback = PathHelper.ResolveExistingDirectory(string.Empty);
        Check.True(Directory.Exists(fallback), "빈 경로는 기댈 곳으로 떨어집니다");
    }

    private static void Code()
    {
        Check.Group("코드 색 입히기");

        Check.True(SyntaxHighlighter.Supports(".cs"), "중괄호 쓰는 말을 압니다");
        Check.True(SyntaxHighlighter.Supports(".py"), "들여쓰기로 묶는 말도 압니다");
        Check.True(SyntaxHighlighter.Supports(".xml"), "표시 언어도 압니다");
        Check.True(!SyntaxHighlighter.Supports(".txt"), "민 글은 칠하지 않습니다");
        Check.True(!SyntaxHighlighter.Supports(null), "확장자가 없으면 칠하지 않습니다");

        var code = SyntaxHighlighter.Highlight("// 메모\nvar n = 12;", ".cs");

        Check.Equal(3, code.Count, "주석·낱말·수 셋을 가려냅니다");
        Check.Equal(CodeSpanKind.Comment, code[0].Kind, "첫째는 주석입니다");
        Check.Equal(5, code[0].Length, "주석은 줄 끝에서 멈춥니다");
        Check.Equal(CodeSpanKind.Keyword, code[1].Kind, "var 는 미리 잡아 둔 낱말입니다");
        Check.Equal(CodeSpanKind.Number, code[2].Kind, "셋째는 수입니다");
        Check.Equal(14, code[2].Start, "수가 시작하는 자리를 맞힙니다");

        var quoted = SyntaxHighlighter.Highlight("var s = \"hi\";", ".cs");

        Check.Equal(2, quoted.Count, "낱말과 글월 둘입니다");
        Check.Equal(CodeSpanKind.Quoted, quoted[1].Kind, "따옴표로 묶은 자리를 잡습니다");
        Check.Equal(4, quoted[1].Length, "따옴표까지 한 구간입니다");

        var broken = SyntaxHighlighter.Highlight("'a\nb = 1", ".py");

        Check.Equal(CodeSpanKind.Quoted, broken[0].Kind, "닫히지 않은 따옴표도 잡습니다");
        Check.Equal(2, broken[0].Length, "다만 줄을 넘지는 않습니다");

        var tag = SyntaxHighlighter.Highlight("<a href=\"x\">글</a>", ".xml");

        Check.Equal(5, tag.Count, "여는 태그·값·꺽쇠·닫는 태그·꺽쇠 다섯입니다");
        Check.Equal(CodeSpanKind.Keyword, tag[0].Kind, "태그 이름은 낱말로 칠합니다");
        Check.Equal(CodeSpanKind.Quoted, tag[1].Kind, "속성 값은 글월로 칠합니다");

        var huge = SyntaxHighlighter.Highlight(new string('a', SyntaxHighlighter.MaxLength + 1), ".cs");

        Check.Equal(0, huge.Count, "너무 긴 글은 칠하지 않습니다");
    }

    private static void Finding()
    {
        Check.Group("이름 맞추기");

        Check.True(FolderSearch.Matches("보고서.txt", "보고"), "들어 있기만 하면 맞습니다");
        Check.True(FolderSearch.Matches("보고서.TXT", "txt"), "대소문자를 가리지 않습니다");
        Check.True(!FolderSearch.Matches("보고서.txt", "결산"), "없는 말은 맞지 않습니다");

        Check.True(FolderSearch.Matches("메모.txt", "*.txt"), "별표를 알아듣습니다");
        Check.True(!FolderSearch.Matches("메모.txtx", "*.txt"), "별표를 직접 쓰면 이름 전체가 그 꼴에 맞아야 합니다");
        Check.True(FolderSearch.Matches("abc", "a?c"), "물음표는 한 글자입니다");
        Check.True(!FolderSearch.Matches("ac", "a?c"), "물음표 자리는 반드시 한 글자가 있어야 합니다");
        Check.True(FolderSearch.Matches("abcc", "a?c"), "물음표만 쓰면 부분 일치라 뒤에 더 붙어도 맞습니다");
        Check.True(FolderSearch.Matches("정부사업계획.hwp", "정?사업"), "물음표를 가운데 써도 찾습니다");
        Check.True(FolderSearch.Matches("report_2024.pdf", "report*.pdf"), "가운데 별표도 됩니다");
        Check.True(FolderSearch.Matches("a.txt", "*.TXT"), "별표를 써도 대소문자를 가리지 않습니다");
        Check.True(FolderSearch.Matches("무엇이든", "*"), "별표 하나는 다 맞습니다");
    }

    private static void Sizing()
    {
        Check.Group("폴더 크기 세기");

        var root = Path.Combine(Path.GetTempPath(), "explorer-size-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(root);
            var inner = Directory.CreateDirectory(Path.Combine(root, "안쪽"));

            File.WriteAllBytes(Path.Combine(root, "a.bin"), new byte[1000]);
            File.WriteAllBytes(Path.Combine(inner.FullName, "b.bin"), new byte[2400]);

            var total = FolderSize
                .MeasureAsync(new[] { root }, null, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            Check.Equal(3400L, total, "아래 폴더까지 모두 더합니다");

            var one = FolderSize
                .MeasureAsync(new[] { Path.Combine(root, "a.bin") }, null, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            Check.Equal(1000L, one, "파일 하나는 제 크기만 셉니다");

            var none = FolderSize
                .MeasureAsync(Array.Empty<string>(), null, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            Check.Equal(0L, none, "셀 것이 없으면 0입니다");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // 지우지 못해도 시험 결과에는 영향이 없습니다.
            }
        }
    }

    private static void History()
    {
        Check.Group("이동 이력");

        var history = new NavigationHistory();

        Check.True(!history.CanGoBack, "처음에는 뒤로 갈 데가 없습니다");

        history.Record("C:\\하나");
        history.Record("C:\\둘");
        history.Record("C:\\셋");

        Check.True(history.CanGoBack, "쌓이면 뒤로 갈 수 있습니다");
        Check.True(!history.CanGoForward, "맨 끝에서는 앞으로 갈 데가 없습니다");

        Check.Equal("C:\\둘", history.GoBack(), "한 걸음 뒤로 갑니다");
        Check.Equal("C:\\하나", history.GoBack(), "두 걸음 뒤로 갑니다");
        Check.Equal("C:\\둘", history.GoForward(), "다시 앞으로 갑니다");

        // 뒤로 간 자리에서 새 길을 가면 앞쪽 길은 지워집니다. 웹 브라우저와 같은 규칙입니다.
        history.Record("C:\\넷");
        Check.True(!history.CanGoForward, "새 길을 가면 앞쪽은 사라집니다");
        Check.Equal("C:\\넷", history.Current, "지금 자리는 새로 간 곳입니다");

        Check.Group("특별한 자리");
        Check.True(SpecialPaths.IsMyComputer(SpecialPaths.MyComputer), "내 PC 를 알아봅니다");
        Check.True(!SpecialPaths.IsMyComputer("C:\\"), "보통 폴더는 아닙니다");
        Check.Equal(SpecialPaths.MyComputerDisplayName, SpecialPaths.ToDisplay(SpecialPaths.MyComputer), "보일 이름으로 옮깁니다");
    }

    private static void Renaming()
    {
        Check.Group("이름 규칙");

        Check.Equal("사진 하나.jpg", RenameRules.Normalize("  사진_하나 .JPG "), "다듬기는 공백·밑줄·확장자를 함께 손봅니다");
        Check.Equal("가 나.txt", RenameRules.Normalize("가   나.txt"), "잇따른 공백은 하나로 줄입니다");
        Check.Equal(".gitignore", RenameRules.Normalize(".gitignore"), "본 이름이 없는 것은 그대로 둡니다");

        Check.Equal("메모.md", RenameRules.WithExtension("메모.txt", "md"), "확장자를 바꿉니다");
        Check.Equal("메모.md", RenameRules.WithExtension("메모.txt", ".md"), "점을 붙여 써도 같습니다");
        Check.Equal("메모", RenameRules.WithExtension("메모.txt", ""), "비워 두면 확장자를 뗍니다");

        Check.Equal("새 이름.txt", RenameRules.Replace("헌 이름.txt", "헌", "새"), "본 이름만 바꿉니다");
        Check.Equal("a.txt", RenameRules.Replace("a.txt", "txt", "md"), "확장자는 건드리지 않습니다");

        Check.Equal("사진03.jpg", RenameRules.Numbered("아무.jpg", "사진", 3, 2), "앞말을 주면 앞말과 번호입니다");
        Check.Equal("아무 (03).jpg", RenameRules.Numbered("아무.jpg", null, 3, 2), "앞말이 없으면 뒤에 붙습니다");
        Check.Equal("01_계약서.pdf", RenameRules.Numbered("계약서.pdf", 1, 2, true, "_"), "앞 + 밑줄");
        Check.Equal("계약서-001.pdf", RenameRules.Numbered("계약서.pdf", 1, 3, false, "-"), "뒤 + 대시");
        Check.Equal("7 사진.jpg", RenameRules.Numbered("사진.jpg", 7, 1, true, " "), "앞 + 띄어쓰기");
        Check.Equal("사진0012.jpg", RenameRules.Numbered("사진.jpg", 12, 4, false, ""), "구분자 없음");
        Check.Equal("자료_02", RenameRules.Numbered("자료", 2, 2, false, "_"), "확장자 없는 이름");
        Check.Equal("03_.gitignore", RenameRules.Numbered(".gitignore", 3, 2, true, "_"), "점으로 시작하는 이름은 통째로 본 이름");
        Check.Equal(2, RenameRules.DigitsFor(42), "마흔둘이면 두 자리입니다");
        Check.Equal(3, RenameRules.DigitsFor(100), "백이면 세 자리입니다");

        var rows = new List<(string Folder, string OldName, string NewName)>
        {
            ("C:\\가", "a.txt", "같다.txt"),
            ("C:\\가", "b.txt", "같다.txt"),
            ("C:\\가", "c.txt", ""),
            ("C:\\가", "d.txt", "못 쓰는:글자.txt"),
            ("C:\\나", "e.txt", "같다.txt")
        };

        var problems = RenameRules.Check(rows);

        Check.True((problems[0] & RenameProblem.Duplicate) != 0, "겹치는 첫째 줄을 짚습니다");
        Check.True((problems[1] & RenameProblem.Duplicate) != 0, "겹치는 둘째 줄도 짚습니다");
        Check.True((problems[2] & RenameProblem.Empty) != 0, "빈 이름을 짚습니다");
        Check.True((problems[3] & RenameProblem.BadCharacter) != 0, "쓸 수 없는 글자를 짚습니다");
        Check.Equal(RenameProblem.None, problems[4], "폴더가 다르면 겹치는 것이 아닙니다");

        Check.True(RenameRules.Describe(RenameProblem.None).Length == 0, "문제가 없으면 할 말도 없습니다");
        Check.True(RenameRules.Describe(RenameProblem.Empty).Length > 0, "문제는 말로 풀어 줍니다");
    }

    private static void Printing()
    {
        Check.Group("인쇄 목록");

        Check.Equal(PrintItemKind.Image, PrintItems.KindOf("사진.JPG"), "그림은 우리가 찍습니다");
        Check.Equal(PrintItemKind.Document, PrintItems.KindOf("계약서.pdf"), "문서는 맡깁니다");
        Check.Equal(PrintItemKind.Document, PrintItems.KindOf("보고서.hwp"), "한글 문서도 압니다");
        Check.Equal(PrintItemKind.Unsupported, PrintItems.KindOf("프로그램.exe"), "찍을 수 없는 것은 가려냅니다");
        Check.Equal(PrintItemKind.Unsupported, PrintItems.KindOf(null), "빈 것도 가려냅니다");

        var root = Path.Combine(Path.GetTempPath(), "explorer-print-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(root);
            var inner = Directory.CreateDirectory(Path.Combine(root, "아래"));

            File.WriteAllText(Path.Combine(root, "사진10.png"), "x");
            File.WriteAllText(Path.Combine(root, "사진2.png"), "x");
            File.WriteAllText(Path.Combine(root, "메모.exe"), "x");
            File.WriteAllText(Path.Combine(inner.FullName, "안쪽.png"), "x");

            var added = PrintItems.Expand(new[] { root }, Array.Empty<string>(), out var skipped);

            Check.Equal(2, added.Count, "폴더 바로 아래의 찍을 수 있는 것만 담습니다");
            Check.Equal("사진2.png", Path.GetFileName(added[0]), "숫자를 수로 읽어 2가 앞입니다");
            Check.Equal("사진10.png", Path.GetFileName(added[1]), "10이 뒤입니다");

            var again = PrintItems.Expand(new[] { root }, added, out _);
            Check.Equal(0, again.Count, "이미 담은 것은 다시 담지 않습니다");

            PrintItems.Expand(new[] { Path.Combine(root, "메모.exe") }, Array.Empty<string>(), out var one);
            Check.Equal(1, one, "찍을 수 없는 파일은 뺀 수로 셉니다");

            Check.True(skipped >= 0, "뺀 개수를 셉니다");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // 지우지 못해도 시험 결과에는 영향이 없습니다.
            }
        }

        var order = new List<string> { "a", "b", "c" };

        Check.True(PrintItems.Move(order, 0, 1), "한 칸 아래로 옮깁니다");
        Check.Equal("b", order[0], "옮긴 자리가 바뀝니다");
        Check.True(!PrintItems.Move(order, 0, -1), "맨 위에서 더 위로는 못 갑니다");
        Check.True(!PrintItems.Move(order, 2, 1), "맨 아래에서 더 아래로도 못 갑니다");
    }

    private static void Imaging()
    {
        Check.Group("그림 편집 셈");

        Check.Equal((1600, 1200), ImageMath.Resize(4000, 3000, ImageResizeMode.LongSide, 1600, true), "가로가 긴 그림은 가로를 맞춥니다");
        Check.Equal((1200, 1600), ImageMath.Resize(3000, 4000, ImageResizeMode.LongSide, 1600, true), "세로가 긴 그림은 세로를 맞춥니다");
        Check.Equal((800, 600), ImageMath.Resize(800, 600, ImageResizeMode.LongSide, 1600, true), "작은 그림은 키우지 않습니다");
        Check.Equal((1600, 1200), ImageMath.Resize(800, 600, ImageResizeMode.LongSide, 1600, false), "끄면 키웁니다");
        Check.Equal((500, 375), ImageMath.Resize(1000, 750, ImageResizeMode.Percent, 50, true), "퍼센트로 줄입니다");
        Check.Equal((1000, 333), ImageMath.Resize(3000, 1000, ImageResizeMode.Width, 1000, true), "가로 기준입니다");
        Check.Equal((1, 1), ImageMath.Resize(10000, 10, ImageResizeMode.Width, 1, true), "0 픽셀로는 줄이지 않습니다");
        Check.Equal((640, 480), ImageMath.Resize(640, 480, ImageResizeMode.Width, 0, true), "값이 없으면 그대로입니다");

        Check.Equal((500, 0, 3000, 3000), ImageMath.CenterCrop(4000, 3000, 1, 1, true), "정사각은 가운데를 자릅니다");
        Check.Equal((0, 0, 4000, 3000), ImageMath.CenterCrop(4000, 3000, 4, 3, true), "이미 4:3이면 그대로입니다");
        Check.Equal((0, 0, 3000, 4000), ImageMath.CenterCrop(3000, 4000, 4, 3, true), "세로 사진에는 3:4로 씁니다");
        Check.Equal((0, 875, 3000, 2250), ImageMath.CenterCrop(3000, 4000, 4, 3, false), "방향을 따르지 않으면 4:3 그대로입니다");
        Check.Equal((0, 375, 4000, 2250), ImageMath.CenterCrop(4000, 3000, 16, 9, true), "16:9는 위아래를 자릅니다");

        Check.True(ImageMath.TryParseRatio("16:9", out var rw, out var rh) && rw == 16 && rh == 9, "비율 글을 읽습니다");
        Check.True(!ImageMath.TryParseRatio("자유", out _, out _), "숫자가 아니면 읽지 않습니다");

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine("d", "a.jpg"), Path.Combine("d", "a (2).jpg") };
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Check.Equal(Path.Combine("d", "b.jpg"), ImageMath.UniquePath("d", "b", ".jpg", existing.Contains, taken), "비어 있으면 그 이름입니다");
        Check.Equal(Path.Combine("d", "a (3).jpg"), ImageMath.UniquePath("d", "a", ".jpg", existing.Contains, taken), "있으면 다음 번호입니다");
        Check.Equal(Path.Combine("d", "b (2).jpg"), ImageMath.UniquePath("d", "b", ".jpg", existing.Contains, taken), "이번에 쓴 이름도 피합니다");

        Check.True(ImageMath.IsEditable("사진.JPEG"), "대문자 확장자도 압니다");
        Check.True(!ImageMath.IsEditable("문서.pdf"), "그림이 아니면 빼냅니다");

        Check.True(ImageMath.CropRatios.Count >= 36, "비율 목록이 다 들어 있습니다");
        Check.True(ImageMath.CropRatios.All(r => r.Width > 0 && r.Height > 0), "비율은 모두 양수입니다");

        var flat = ImageMath.RotateToFill(4000, 3000, 0);
        Check.Equal((4000, 3000, 4000, 3000), (flat.CanvasWidth, flat.CanvasHeight, flat.KeepWidth, flat.KeepHeight), "0도는 그대로입니다");

        var square = ImageMath.RotateToFill(1000, 1000, 45);
        Check.Equal(1415, square.CanvasWidth, "정사각을 45도 돌리면 캔버스가 √2배입니다");
        Check.True(Math.Abs(square.Scale - (1 / Math.Sqrt(2))) < 1e-9, "남길 크기는 1/√2 배입니다");

        var tilt = ImageMath.RotateToFill(4000, 3000, 3);
        Check.True(tilt.KeepWidth < 4000 && Math.Abs(((double)tilt.KeepWidth / tilt.KeepHeight) - (4000.0 / 3000)) < 0.01, "기울여도 가로세로 비율을 지킵니다");

        Check.Equal(-5.71, ImageMath.StraightenAngle(0, 0, 100, 10), "오른쪽 아래로 처진 수평선은 반시계로 돌립니다");
        Check.Equal(5.71, ImageMath.StraightenAngle(100, 10, 0, 20), "거꾸로 그어도 같은 선입니다");
        Check.Equal(-5.71, ImageMath.StraightenAngle(0, 0, 10, 100) is { } v ? -v : 0, "수직에 가까운 선은 수직으로 맞춥니다");
        Check.True(ImageMath.StraightenAngle(0, 0, 2, 2) is null, "너무 짧은 선은 무시합니다");
    }

    private static ColumnSettings Find(List<ColumnSettings> columns, string key) =>
        columns.Find(column => column.Key == key) ?? new ColumnSettings();

    private static int Count(List<ColumnSettings> columns, string key)
    {
        var count = 0;

        foreach (var column in columns)
        {
            if (column.Key == key)
            {
                count++;
            }
        }

        return count;
    }

    private static FileSystemEntry Entry(string name, bool isDirectory, long size, DateTime written) => new()
    {
        FullPath = Path.Combine(Path.GetTempPath(), name),
        Name = name,
        IsDirectory = isDirectory,
        Size = size,
        LastWriteTime = written,
        CreationTime = written
    };

    /// <summary>파이썬으로 만든 작은 복합 문서입니다. olefile 로 읽어 맞는 것을 확인했습니다. gzip 후 base64 입니다.</summary>
    private const string SampleHwp =
          "H4sIAEI5t2oC/7twXvDBwo1SDxnQgB0DM8O//5wMbEhijFAMBgIMDExQ/r/////DhP+PgiEF/gLxPyTMCoxDUJyzAzEHEHMCMRcQ"
        + "cwMxDxDzQuN7FAwPEMSQD4QlDAoMrgx5QLqIoZKBFCDGwMoIM4uZSD0gdQ7QgsSNIZMhhyGVwQOIExlSgGQRifYzMSL7h2iNUPsD"
        + "gPaVMYQA7a0A+p50IECG/SxALMKAbL8nQy7Q9+lAV5AKhID2w8phUuzvEEYEAxM0TmBlwCgYOYCBkYmZhZWNnYOTi5uHl49fQFBI"
        + "WERUTFxCUkpaRlZOXkFRSVlFVU1dQ1NLW0dXT9/A0MjYxNTM3MLSytrG1s7ewdHJ2cXVzd3D08vbx9fPPyAwKDgkNCw8IjIqOiY2"
        + "Lj4hMSk5JTUtPSMzKzsnNy+/oLCouKS0rLyisqq6prauvqGxqbmlta29o7Oru6e3r3/CxEmTp0ydNn3GzFmz58ydN3/BwkWLlyxd"
        + "tnzFylWr16xdt37Dxk2bt2zdtn3Hzl279+zdt//AwUOHjxw9dvzEyVOnz5w9d/7CxUuXr1y9dv3GzVu379y9d//Bw0ePnzx99vzF"
        + "y1ev37x99/7Dx0+fv3z99v3Hz1+///z993/1GQUGlpO8wDouYsuPkyA2w4gCnQF+7rxcUlwM7HyiMspahhb2br4h0UmZBeV1rT2T"
        + "Zy1csX7b3iOnL9188PzdV0YOfjFZFW0jSwd3v9CY5KzCivq23imzF63csH3f0TOXbz188f4bE6eAuJyqjrGVo4d/WGxKdlFlQ3vf"
        + "1DmLV23csf/Y2Su3H7388J2ZS1BCXk3XxNrJMyA8LjWnuKqxo3/a3CWrN+08cPzc1TuPX338wcItJKmgrmdq4+wVGBGflltS3dQ5"
        + "Yfq8pWs27zp44vy1u09ef/rJyiMspaihb2br4h0UmZCeV1rT3DVxxvxla7fsPnTywvV7T998/sXGKyKtpGlgbufqExyVmJFfVtvS"
        + "PWnmguXrtu45fOrijfvP3n4Z9fqo10e9Pur1Ua+Pen3U66NeH/X6qNdHvT7q9VGvj3p91OujXh/1+qjXae71gRr/AwBUctXIAB4A"
        + "AA==";

    private static void HwpPreviews()
    {
        Check.Group("한글 문서 미리보기");

        using var packed = new MemoryStream(Convert.FromBase64String(SampleHwp));
        using var gzip = new System.IO.Compression.GZipStream(packed, System.IO.Compression.CompressionMode.Decompress);
        using var raw = new MemoryStream();
        gzip.CopyTo(raw);
        var file = raw.ToArray();

        var preview = HwpPreview.ReadHwp(file);
        Check.True(preview is not null, "복합 문서를 엽니다");
        Check.Equal(5000, preview?.Image?.Length ?? 0, "큰 흐름(그림)은 섹터를 따라 읽습니다");
        Check.Equal((byte)0x89, preview?.Image?[0] ?? 0, "그림의 첫 바이트입니다");
        Check.Equal((byte)(4991 * 7 % 251), preview?.Image?[4999] ?? 0, "그림의 끝 바이트까지 맞습니다");
        Check.Equal("첫 줄\n둘째 줄", preview?.Text, "작은 흐름(글자)은 미니 흐름에서 읽고 줄바꿈을 고칩니다");

        var header = CompoundFile.TryOpen(file)?.ReadStream("FileHeader");
        Check.Equal(256, header?.Length ?? 0, "다른 흐름도 이름으로 찾습니다");
        Check.Equal((byte)255, header?[255] ?? 0, "미니 섹터 여러 개를 잇습니다");
        Check.True(CompoundFile.TryOpen(file)?.ReadStream("없는흐름") is null, "없는 흐름은 null 입니다");
        Check.True(HwpPreview.ReadHwp(new byte[600]) is null, "복합 문서가 아니면 null 입니다");

        var hwpx = Path.Combine(Path.GetTempPath(), $"explorer-test-{Guid.NewGuid():N}.hwpx");

        try
        {
            using (var zip = System.IO.Compression.ZipFile.Open(hwpx, System.IO.Compression.ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("Preview/PrvText.txt").Open()))
                {
                    writer.Write("한글 hwpx\r\n두 번째");
                }

                using var image = zip.CreateEntry("Preview/PrvImage.png").Open();
                image.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
            }

            var x = HwpPreview.Read(hwpx);
            Check.Equal("한글 hwpx\n두 번째", x?.Text, "hwpx 는 ZIP 의 Preview 폴더에서 글자를 읽습니다");
            Check.Equal(4, x?.Image?.Length ?? 0, "hwpx 의 그림도 읽습니다");
        }
        finally
        {
            File.Delete(hwpx);
        }
    }
}
