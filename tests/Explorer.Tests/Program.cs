using System;
using System.Text;

namespace Explorer.Tests;

internal static class Program
{
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("Explorer.Core 시험");

        CoreTests.Run();

        return Check.Report();
    }
}
