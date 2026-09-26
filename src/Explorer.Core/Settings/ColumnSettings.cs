using System;
using System.Collections.Generic;

namespace Explorer.Core.Settings;

/// <summary>
/// 자세히 보기의 열 하나입니다. 보일지 여부와 너비를 기억합니다.
/// 순서는 목록에 담긴 순서를 그대로 씁니다.
/// </summary>
public sealed class ColumnSettings
{
    /// <summary>열을 가리키는 이름입니다. 화면 쪽의 열 표와 이 이름으로 맞춥니다.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>열 너비(픽셀)입니다.</summary>
    public double Width { get; set; }

    /// <summary>이 열을 보일지 여부입니다.</summary>
    public bool Visible { get; set; } = true;

    /// <summary>알고 있는 열과 그 기본값입니다. 차례가 곧 기본 순서입니다.</summary>
    public static IReadOnlyList<(string Key, double Width, bool Visible)> Catalog { get; } = new[]
    {
        ("Name", 300d, true),
        ("Size", 90d, true),
        ("Modified", 130d, true),
        ("Type", 90d, true),
        ("Created", 130d, false),
        ("Extension", 70d, false),
        ("Folder", 220d, false)
    };

    /// <summary>기본 열 구성입니다.</summary>
    public static List<ColumnSettings> CreateDefaults()
    {
        var result = new List<ColumnSettings>(Catalog.Count);

        foreach (var (key, width, visible) in Catalog)
        {
            result.Add(new ColumnSettings { Key = key, Width = width, Visible = visible });
        }

        return result;
    }

    /// <summary>
    /// 설정 파일에서 읽은 열 구성을 손봅니다.
    /// 모르는 열은 버리고, 빠진 열은 기본값으로 뒤에 붙이고, 이름 열은 반드시 보이게 둡니다.
    /// </summary>
    public static List<ColumnSettings> Normalize(List<ColumnSettings>? columns)
    {
        if (columns is null || columns.Count == 0)
        {
            return CreateDefaults();
        }

        var result = new List<ColumnSettings>(Catalog.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var column in columns)
        {
            if (column is null || string.IsNullOrWhiteSpace(column.Key))
            {
                continue;
            }

            var known = Find(column.Key);

            if (known is null || !seen.Add(known.Value.Key))
            {
                continue;
            }

            result.Add(new ColumnSettings
            {
                Key = known.Value.Key,
                Width = column.Width is >= 40 and <= 1200 ? column.Width : known.Value.Width,
                Visible = column.Visible
            });
        }

        // 설정 파일이 만들어진 뒤에 늘어난 열은 기본 상태로 뒤에 붙입니다.
        foreach (var (key, width, visible) in Catalog)
        {
            if (seen.Add(key))
            {
                result.Add(new ColumnSettings { Key = key, Width = width, Visible = visible });
            }
        }

        // 이름 열은 끌 수 없습니다. 꺼져 있으면 맨 앞으로 되돌립니다.
        var name = result.Find(item => string.Equals(item.Key, "Name", StringComparison.OrdinalIgnoreCase));

        if (name is null)
        {
            name = new ColumnSettings { Key = "Name", Width = 300, Visible = true };
            result.Insert(0, name);
        }

        name.Visible = true;
        return result;
    }

    private static (string Key, double Width, bool Visible)? Find(string key)
    {
        foreach (var entry in Catalog)
        {
            if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }
}
