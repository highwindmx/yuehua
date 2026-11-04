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
        var stamp = ImageLoader.StampOf(path);
        lock (_lock)
        {
            if (_dict.TryGetValue(path, out var v))
            {
                if (v.FileStamp == stamp) { Touch(path); return v; }

                // 同名文件被覆盖重写（例如「另存为」导出到当前路径、或外部工具替换了图片）：
                // 旧解码结果必须失效，否则主界面会一直显示旧内容，而缩略图条因直读磁盘显示新内容。
                // 这里不主动 Dispose 旧对象：_current 可能仍引用它的 Display，提前释放会让画布崩溃，
                // 交给 GC / 终结器回收即可。
                _dict.Remove(path);
                var node = _lru.Find(path);
                if (node != null) _lru.Remove(node);
            }
        }

        var loaded = ImageLoader.Load(path);
        loaded.FileStamp = stamp;
        lock (_lock)
        {
            // 并发预取可能已写入同路径，去掉重复 LRU 节点
            if (_dict.ContainsKey(path))
            {
                var node = _lru.Find(path);
                if (node != null) _lru.Remove(node);
            }
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

    /// <summary>尝试取缓存；文件已变化时视为未命中，避免调用方拿到过期位图。</summary>
    public LoadedImage? TryGet(string path)
    {
        var stamp = ImageLoader.StampOf(path);
        lock (_lock)
            return _dict.TryGetValue(path, out var v) && v.FileStamp == stamp ? v : null;
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
