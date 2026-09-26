using System;
using System.IO;
using System.Threading;

namespace Explorer.Core.Services;

/// <summary>
/// 폴더 하나를 감시하고, 변경이 잠잠해진 뒤에 한 번만 알립니다.
/// 대량 복사 중에는 초당 수백 건이 들어오므로 그대로 새로고침하면 목록이 깜빡입니다.
/// </summary>
public sealed class DebouncedDirectoryWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _timer;
    private readonly int _debounceMs;
    private bool _disposed;

    public DebouncedDirectoryWatcher(int debounceMs = 200)
    {
        _debounceMs = Math.Clamp(debounceMs, 50, 5000);

        _watcher = new FileSystemWatcher
        {
            IncludeSubdirectories = false,
            // 기본 버퍼(8KB)는 큰 폴더에서 쉽게 넘칩니다.
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName
                           | NotifyFilters.DirectoryName
                           | NotifyFilters.LastWrite
                           | NotifyFilters.Size
        };

        _watcher.Created += OnFileSystemEvent;
        _watcher.Deleted += OnFileSystemEvent;
        _watcher.Changed += OnFileSystemEvent;
        _watcher.Renamed += OnFileSystemEvent;
        _watcher.Error += OnWatcherError;

        _timer = new Timer(OnTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>변경이 잠잠해졌을 때 한 번 발생합니다. 스레드 풀에서 올라오므로 UI로 옮겨야 합니다.</summary>
    public event EventHandler? Changed;

    /// <summary>감시 대상 폴더를 바꿉니다. 열 수 없는 경로면 조용히 감시를 끕니다.</summary>
    public void Watch(string path)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _watcher.EnableRaisingEvents = false;

            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                return;
            }

            _watcher.Path = path;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or IOException)
        {
            // 네트워크 드라이브나 제거된 디스크입니다. 감시 없이 동작합니다.
        }
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e) => Restart();

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // 버퍼가 넘치면 개별 이벤트를 잃습니다. 전체 새로고침 한 번으로 맞춥니다.
        Restart();
    }

    private void Restart()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _timer.Change(_debounceMs, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnTimerElapsed(object? state)
    {
        if (_disposed)
        {
            return;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _timer.Dispose();
    }
}
