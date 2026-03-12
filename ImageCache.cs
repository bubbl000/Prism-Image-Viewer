using System.Windows.Media.Imaging;

namespace ImageViewer;

/// <summary>
/// 原图 LRU 内存缓存（最多 <see cref="MaxEntries"/> 张已解码的完整 BitmapSource）。
/// 仅在 UI 线程访问，无锁。
/// </summary>
internal sealed class ImageCache
{
    private const int MaxEntries = 7;

    private readonly LinkedList<string>              _order = new();
    private readonly Dictionary<string, BitmapSource> _map   = new(MaxEntries + 1);

    // ── 读取（同时将条目移到链表头，刷新 LRU 顺序） ──────────────
    public BitmapSource? TryGet(string path)
    {
        if (!_map.TryGetValue(path, out var bmp)) return null;
        _order.Remove(path);
        _order.AddFirst(path);
        return bmp;
    }

    public bool Contains(string path) => _map.ContainsKey(path);

    // ── 写入（超出上限时淘汰最久未用的条目） ─────────────────────
    public void Put(string path, BitmapSource bmp)
    {
        if (_map.ContainsKey(path))
        {
            _map[path] = bmp;
            _order.Remove(path);
            _order.AddFirst(path);
            return;
        }

        while (_map.Count >= MaxEntries && _order.Last != null)
        {
            string lru = _order.Last.Value;
            _order.RemoveLast();
            _map.Remove(lru);
        }

        _map[path] = bmp;
        _order.AddFirst(path);
    }

    // ── 删除单条（文件被改名/删除时调用） ────────────────────────
    public void Remove(string path)
    {
        _order.Remove(path);
        _map.Remove(path);
    }

    // ── 清空（切换文件夹时调用） ─────────────────────────────────
    public void Clear()
    {
        _map.Clear();
        _order.Clear();
    }
}
