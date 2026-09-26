using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using Explorer.Shell.Thumbnails;

namespace Explorer.App.Services;

/// <summary>
/// 썸네일을 메모리에 들고 있다가 다시 쓰고, 넘치면 오래 안 쓴 것부터 버립니다(LRU).
/// 같은 폴더를 오갈 때마다 셸을 다시 부르지 않게 하는 것이 목적입니다.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ThumbnailCache : IDisposable
{
    private readonly ShellThumbnailService _service;
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<CacheItem>> _lookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<CacheItem> _order = new();

    public ThumbnailCache(int capacity, int concurrency)
    {
        _capacity = Math.Max(capacity, 100);
        _service = new ShellThumbnailService(concurrency);
    }

    /// <param name="iconOnly">true이면 미리보기 그림을 만들지 않고 파일 종류 아이콘만 받습니다.</param>
    public async Task<ImageSource?> GetAsync(
        string path,
        int size,
        bool iconOnly,
        CancellationToken cancellationToken)
    {
        var key = $"{(iconOnly ? "i" : "t")}{size}|{path}";

        if (TryTake(key, out var cached))
        {
            return cached;
        }

        var image = await _service.GetAsync(path, size, iconOnly, cancellationToken).ConfigureAwait(true);

        if (image is not null)
        {
            Store(key, image);
        }

        return image;
    }

    private bool TryTake(string key, out ImageSource? image)
    {
        lock (_gate)
        {
            if (_lookup.TryGetValue(key, out var node))
            {
                // 최근에 쓴 것으로 옮깁니다.
                _order.Remove(node);
                _order.AddFirst(node);
                image = node.Value.Image;
                return true;
            }
        }

        image = null;
        return false;
    }

    private void Store(string key, ImageSource image)
    {
        lock (_gate)
        {
            if (_lookup.ContainsKey(key))
            {
                return;
            }

            var node = _order.AddFirst(new CacheItem(key, image));
            _lookup[key] = node;

            while (_lookup.Count > _capacity && _order.Last is { } last)
            {
                _order.RemoveLast();
                _lookup.Remove(last.Value.Key);
            }
        }
    }

    public void Dispose() => _service.Dispose();

    private sealed record CacheItem(string Key, ImageSource Image);
}
