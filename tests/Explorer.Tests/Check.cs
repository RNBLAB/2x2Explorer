using System;
using System.Collections.Generic;

namespace Explorer.Tests;

/// <summary>
/// 아주 작은 검사 도구입니다.
/// 틀린 것을 세어 두었다가 마지막에 한 번에 알려 줍니다.
/// </summary>
public static class Check
{
    private static readonly List<string> Failures = new();

    private static string _current = string.Empty;

    public static int FailureCount => Failures.Count;

    public static void Group(string name)
    {
        _current = name;
        Console.WriteLine();
        Console.WriteLine("── " + name);
    }

    public static void True(bool value, string what) => Equal(true, value, what);

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (EqualityComparer<T>.Default.Equals(expected, actual))
        {
            Console.WriteLine("  통과  " + what);
            return;
        }

        Failures.Add($"{_current} / {what}: 바란 값 [{expected}], 나온 값 [{actual}]");
        Console.WriteLine($"  실패  {what}  → 바란 값 [{expected}], 나온 값 [{actual}]");
    }

    /// <summary>비교자가 앞뒤 차례를 어떻게 매기는지 봅니다.</summary>
    public static void Before<T>(IComparer<T> comparer, T first, T second, string what)
    {
        if (comparer.Compare(first, second) < 0)
        {
            Console.WriteLine("  통과  " + what);
            return;
        }

        Failures.Add($"{_current} / {what}: [{first}]이 [{second}]보다 앞이어야 합니다.");
        Console.WriteLine($"  실패  {what}");
    }

    public static int Report()
    {
        Console.WriteLine();

        if (Failures.Count == 0)
        {
            Console.WriteLine("모두 통과했습니다.");
            return 0;
        }

        Console.WriteLine($"{Failures.Count}개가 틀렸습니다.");

        foreach (var failure in Failures)
        {
            Console.WriteLine("  " + failure);
        }

        return 1;
    }
}
