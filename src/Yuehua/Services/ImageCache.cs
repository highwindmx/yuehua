using System.Collections.Generic;
using System.Threading.Tasks;

namespace Yuehua.Services;

/// <summary>LRU 图像缓存：保留最近若干张解码结果，切换/预载邻图时不重复解码。</summary>
public class ImageCache
{
    private readonly Dictionary<string, LoadedImage> _dict = new();
    private readonly LinkedList<string> _lru = new();
    private readonly int _maxCount;
    private readonly object _lock = new();

    public ImageCache(int maxCount = 8) => _maxCount = maxCount;

    public LoadedImage GetOrLoad(string path)
    {
        lock (_lock)
        {
            if (_dict.TryGetValue(path, out var v))
            {
                Touch(path);
                return v;
            }
        }

        var loaded = ImageLoader.Load(path);
        lock (_lock)
        {
            _dict[path] = loaded;
            _lru.AddFirst(path);
            while (_dict.Count > _maxCount)
            {
                var last = _lru.Last;
                if (last == null) break;
                _lru.RemoveLast();
                if (_dict.TryGetValue(last.Value, out var old))
                {
                    _dict.Remove(last.Value);
                    try { old.Full.Dispose(); old.Display.Dispose(); } catch { }
                }
            }
        }
        return loaded;
    }

    public LoadedImage? TryGet(string path)
    {
        lock (_lock)
            return _dict.TryGetValue(path, out var v) ? v : null;
    }

    /// <summary>后台预载若干路径（通常是当前图的前后邻图），提升翻页流畅度。</summary>
    public void Prefetch(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            Task.Run(() =>
            {
                try { GetOrLoad(p); } catch { /* 预载失败忽略 */ }
            });
        }
    }

    private void Touch(string path)
    {
        var node = _lru.Find(path);
        if (node != null)
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
        }
    }
}
