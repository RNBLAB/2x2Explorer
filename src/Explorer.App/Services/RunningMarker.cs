using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;

namespace Explorer.App.Services;

/// <summary>
/// 프로그램이 켜져 있는 동안 이름 있는 뮤텍스를 쥐고 있습니다.
///
/// 설치 마법사(installer/2x2Explorer.iss 의 AppMutex)가 이 이름을 보고
/// "2x2 Explorer 가 실행 중입니다"라고 알린 뒤 닫을 때까지 기다립니다.
/// 새 판을 깔 때 옛 판의 제거 프로그램을 먼저 돌리는데, 프로그램이 켜져 있으면
/// 파일이 잠겨 제거가 반쯤에서 멈추기 때문입니다.
///
/// 이름을 바꾸면 .iss 의 AppMutex 도 같이 바꿔야 합니다.
/// 로컬 이름과 Global\ 이름을 모두 겁니다. 다른 사용자 세션에서 켠 것도 알아보게 하려는 것입니다.
/// 여러 개를 켜도 괜찮습니다. 뮤텍스를 소유하지 않고 열어 두기만 하므로 서로 막지 않습니다.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class RunningMarker
{
    /// <summary>installer/2x2Explorer.iss 의 AppMutex 와 같아야 합니다.</summary>
    public const string Name = "2x2Explorer.Running";

    private static Mutex? _local;
    private static Mutex? _global;

    public static void Hold()
    {
        _local ??= TryCreate(Name);
        _global ??= TryCreate(@"Global\" + Name);
    }

    public static void Release()
    {
        _local?.Dispose();
        _global?.Dispose();
        _local = null;
        _global = null;
    }

    private static Mutex? TryCreate(string name)
    {
        try
        {
            return new Mutex(initiallyOwned: false, name);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
                                       or IOException
                                       or WaitHandleCannotBeOpenedException)
        {
            // 표식을 못 걸어도 프로그램은 돌아야 합니다. 설치 마법사가 알아보지 못할 뿐입니다.
            return null;
        }
    }
}
