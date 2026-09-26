using System;
using Explorer.Core.Settings;

namespace Explorer.Core.Abstractions;

/// <summary>
/// 설정을 읽고 쓰는 창구입니다.
/// </summary>
public interface ISettingsService
{
    /// <summary>실제로 사용 중인 설정 파일의 전체 경로입니다.</summary>
    string SettingsFilePath { get; }

    /// <summary>설정을 읽습니다. 파일이 없거나 깨져 있으면 기본 설정을 돌려줍니다.</summary>
    AppSettings Load();

    /// <summary>
    /// 저장에 실패했을 때 알립니다. 사람에게 그대로 보여 줄 한 줄이 실려 옵니다.
    /// 저장은 창을 닫을 때를 비롯해 여러 곳에서 일어나므로, 실패도 한곳에서 듣습니다.
    /// </summary>
    event Action<string>? SaveFailed;

    /// <summary>설정을 저장합니다. 쓰기에 실패해도 예외를 던지지 않습니다.</summary>
    void Save(AppSettings settings);
}
