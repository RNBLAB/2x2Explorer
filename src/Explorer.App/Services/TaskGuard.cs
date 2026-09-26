using System;
using System.Threading.Tasks;
using Explorer.Core.Services;

namespace Explorer.App.Services;

/// <summary>상태 줄처럼 사람에게 한 줄 알릴 수 있는 곳입니다.</summary>
public interface IProblemSink
{
    void ReportProblem(string message);
}

/// <summary>
/// 기다리는 사람 없이 떠난 작업을 지켜봅니다.
///
/// 화면에서 부르는 일은 거의 다 <c>_ = 무엇Async()</c> 꼴입니다.
/// 그렇게 두면 그 안에서 난 예외는 아무도 받지 않아, 아무 일도 안 일어난 것처럼 보입니다.
/// 여기를 거치면 기록에 남고, 알릴 곳이 있으면 상태 줄에도 한 줄 뜹니다.
/// </summary>
public static class TaskGuard
{
    /// <param name="what">무슨 일이었는지입니다. 사람에게 그대로 보여 줍니다.</param>
    /// <param name="sink">알릴 곳입니다. 없으면 기록만 남습니다.</param>
    public static void Fire(this Task? task, string what, IProblemSink? sink = null)
    {
        if (task is null || task.IsCompletedSuccessfully)
        {
            return;
        }

        _ = Watch(task, what, sink);
    }

    private static async Task Watch(Task task, string what, IProblemSink? sink)
    {
        try
        {
            await task.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 다른 일로 넘어간 경우입니다. 실패가 아닙니다.
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteAlways($"{what} 중 예외: {ex.GetType().Name} {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            sink?.ReportProblem($"{what}에서 문제가 생겼습니다. {ex.Message}");
        }
    }
}
