using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Explorer.Core.Services;

namespace Explorer.App.Services;

/// <summary>
/// WPF 바인딩 오류를 debug.log 에 적습니다.
///
/// 바인딩이 풀리면 WPF 는 예외를 내지 않고 값만 비운 채 넘어갑니다. 화면이 이상해져도 흔적이 남지 않습니다.
/// 목록 아이콘이 열 너비만큼 커진 일(2026-09-27)이 그랬습니다. 크기 바인딩이 풀린 것으로 보이는데 확인할 길이 없었습니다.
///
/// 기록 설정과 상관없이 적습니다(WriteAlways). 대신 같은 문장은 한 번만, 한 실행에 최대 <see cref="Limit"/>줄까지입니다.
/// 목록 줄마다 같은 오류가 되풀이되면 로그가 금세 불어나기 때문입니다.
/// </summary>
public sealed class BindingErrorLog : TraceListener
{
    private const int Limit = 200;

    private readonly object _gate = new();
    private readonly StringBuilder _line = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private int _written;

    /// <summary>프로그램을 켤 때 한 번 부릅니다. 오류 단계만 듣습니다(경고는 흔하고 대개 해가 없습니다).</summary>
    public static void Start()
    {
        try
        {
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingErrorLog());
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            DiagnosticLog.WriteAlways($"바인딩 오류 기록을 켜지 못했습니다: {ex.Message}");
        }
    }

    // WPF 는 머리말("System.Windows.Data Error: 40 : ")을 Write 로, 본문을 WriteLine 으로 보냅니다.
    // 둘을 모아 한 줄로 적습니다.
    public override void Write(string? message)
    {
        lock (_gate)
        {
            _line.Append(message);
        }
    }

    public override void WriteLine(string? message)
    {
        string text;
        int count;

        lock (_gate)
        {
            _line.Append(message);
            text = _line.ToString().Trim();
            _line.Clear();

            if (text.Length == 0 || _written >= Limit || !_seen.Add(text))
            {
                return;
            }

            count = ++_written;
        }

        DiagnosticLog.WriteAlways(count == Limit
            ? $"[바인딩] {text} (이후 바인딩 오류는 적지 않습니다)"
            : $"[바인딩] {text}");
    }
}
