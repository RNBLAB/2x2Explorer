using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Explorer.Core.Abstractions;
using Explorer.Core.Settings;

namespace Explorer.Core.Services;

/// <summary>
/// 설정을 JSON 파일로 읽고 씁니다.
/// 저장은 임시 파일에 쓴 뒤 바꿔치기해서, 쓰는 도중 꺼져도 기존 파일이 깨지지 않게 합니다.
/// </summary>
public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>저장에 실패했을 때 알립니다.</summary>
    public event Action<string>? SaveFailed;

    public string SettingsFilePath { get; }

    /// <param name="settingsFilePath">
    /// 설정 파일 경로입니다. 비워 두면 %APPDATA%\2x2Explorer\settings.json 을 씁니다.
    /// </param>
    public JsonSettingsService(string? settingsFilePath = null)
    {
        SettingsFilePath = string.IsNullOrWhiteSpace(settingsFilePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "2x2Explorer",
                "settings.json")
            : settingsFilePath;
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                var created = AppSettings.CreateDefault();
                Save(created);
                return created;
            }

            var json = File.ReadAllText(SettingsFilePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);

            if (loaded is null)
            {
                return AppSettings.CreateDefault();
            }

            loaded.Normalize();
            return loaded;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            BackupBrokenFile();
            return AppSettings.CreateDefault();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Normalize();

        try
        {
            var directory = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = SettingsFilePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, SerializerOptions));
            File.Move(temporaryPath, SettingsFilePath, overwrite: true);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            // 설정을 못 저장한다고 프로그램을 멈추지는 않습니다. 다만 조용히 넘기지도 않습니다.
            // 이대로 두면 탭이며 창 크기며 바꾼 것이 다음 실행에서 사라진 까닭을 알 길이 없습니다.
            DiagnosticLog.WriteAlways($"설정 저장 실패 {SettingsFilePath}: {ex.GetType().Name} {ex.Message}");
            SaveFailed?.Invoke($"설정을 저장하지 못했습니다. {ex.Message}");
        }
    }

    private void BackupBrokenFile()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                return;
            }

            var backupPath = $"{SettingsFilePath}.broken-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(SettingsFilePath, backupPath, overwrite: true);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            // 백업까지 실패하면 그냥 기본 설정으로 갑니다.
        }
    }

    private static bool IsRecoverable(Exception ex) =>
        ex is IOException
            or JsonException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException;
}
