using System;
using System.Diagnostics;

namespace Explorer.Core.Services;

/// <summary>
/// 화면 갈래에서 도는 일에 시계를 답니다.
///
/// 오래 걸린 것만 기록에 남깁니다.
/// 화면이 멈춘 동안 무엇을 하고 있었는지 가리려고 두었습니다.
/// 화면 갈래에서만 쓰므로 여러 갈래를 가릴 잠금은 두지 않습니다.
/// </summary>
public static class UiWatch
{
    /// <summary>이보다 오래 걸린 일만 적습니다.</summary>
    private const double SlowMilliseconds = 50;

    private static string _doing = "쉬는 중";

    /// <summary>지금 하고 있는 일입니다. 멈춤을 알릴 때 함께 적습니다.</summary>
    public static string Doing => _doing;

    /// <summary>한 걸음을 잽니다. using 으로 감싸면 끝날 때 저절로 적습니다.</summary>
    public static Scope Step(string what) => new(what);

    public readonly struct Scope : IDisposable
    {
        private readonly string _what;
        private readonly string _previous;
        private readonly long _start;

        public Scope(string what)
        {
            _what = what;
            _previous = _doing;
            _doing = what;
            _start = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            var spent = (Stopwatch.GetTimestamp() - _start) * 1000.0 / Stopwatch.Frequency;
            _doing = _previous;

            if (spent > SlowMilliseconds)
            {
                DiagnosticLog.Write($"[느림] {_what} {spent:F0}ms");
            }
        }
    }
}
