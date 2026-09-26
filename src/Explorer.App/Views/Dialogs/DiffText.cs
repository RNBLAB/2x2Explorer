using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Explorer.App.Views.Dialogs;

/// <summary>
/// 원래 이름과 새 이름을 견주어 새 이름에서 바뀐 부분만 강조해 보이는 글줄입니다.
/// 앞뒤로 같은 부분을 떼고 가운데를 강조하는 단순한 방식입니다. 이름 바꾸기 미리 보기에는 이것으로 충분합니다.
/// </summary>
public sealed class DiffText : TextBlock
{
    public static readonly DependencyProperty OriginalProperty =
        DependencyProperty.Register(nameof(Original), typeof(string), typeof(DiffText),
            new PropertyMetadata(string.Empty, (d, _) => ((DiffText)d).Rebuild()));

    public static readonly DependencyProperty ResultProperty =
        DependencyProperty.Register(nameof(Result), typeof(string), typeof(DiffText),
            new PropertyMetadata(string.Empty, (d, _) => ((DiffText)d).Rebuild()));

    public string Original
    {
        get => (string)GetValue(OriginalProperty);
        set => SetValue(OriginalProperty, value);
    }

    public string Result
    {
        get => (string)GetValue(ResultProperty);
        set => SetValue(ResultProperty, value);
    }

    private void Rebuild()
    {
        Inlines.Clear();

        var before = Original ?? string.Empty;
        var after = Result ?? string.Empty;

        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            // 바뀌지 않는 이름은 흐리게 보입니다.
            var same = new Run(after);
            same.SetResourceReference(TextElement.ForegroundProperty, "Text.Muted");
            Inlines.Add(same);
            return;
        }

        var head = 0;

        while (head < before.Length && head < after.Length && before[head] == after[head])
        {
            head++;
        }

        var tail = 0;

        while (tail < before.Length - head && tail < after.Length - head
               && before[before.Length - 1 - tail] == after[after.Length - 1 - tail])
        {
            tail++;
        }

        if (head > 0)
        {
            Inlines.Add(new Run(after[..head]));
        }

        var middle = after.Substring(head, after.Length - head - tail);

        if (middle.Length > 0)
        {
            var run = new Run(middle) { FontWeight = FontWeights.SemiBold };
            run.SetResourceReference(TextElement.ForegroundProperty, "Accent");
            Inlines.Add(run);
        }

        if (tail > 0)
        {
            Inlines.Add(new Run(after[^tail..]));
        }
    }
}
