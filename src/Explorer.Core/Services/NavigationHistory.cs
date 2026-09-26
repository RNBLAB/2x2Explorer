using System;
using System.Collections.Generic;

namespace Explorer.Core.Services;

/// <summary>
/// 한 탭의 뒤로/앞으로 이력입니다.
/// 브라우저와 같은 규칙으로, 뒤로 간 상태에서 새 곳으로 이동하면 앞쪽 이력은 버립니다.
/// </summary>
public sealed class NavigationHistory
{
    private const int MaxEntries = 100;

    private readonly List<string> _entries = new();
    private int _index = -1;

    public bool CanGoBack => _index > 0;

    public bool CanGoForward => _index >= 0 && _index < _entries.Count - 1;

    public string? Current => _index >= 0 && _index < _entries.Count ? _entries[_index] : null;

    public void Record(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        if (string.Equals(Current, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // 앞쪽 이력을 버립니다.
        if (_index < _entries.Count - 1)
        {
            _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);
        }

        _entries.Add(path);
        _index = _entries.Count - 1;

        if (_entries.Count > MaxEntries)
        {
            var overflow = _entries.Count - MaxEntries;
            _entries.RemoveRange(0, overflow);
            _index -= overflow;
        }
    }

    public string? GoBack()
    {
        if (!CanGoBack)
        {
            return null;
        }

        _index--;
        return _entries[_index];
    }

    public string? GoForward()
    {
        if (!CanGoForward)
        {
            return null;
        }

        _index++;
        return _entries[_index];
    }
}
