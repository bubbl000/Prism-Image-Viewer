using System.Windows.Media.Imaging;

namespace ImageBrowser;

/// <summary>
/// 原图 LRU 内存缓存（最多 <see cref="MaxEntries"/> 张已解码的完整 BitmapSource）。
/// 使用 LinkedList + Dictionary 实现真正的 LRU 缓存。
/// </summary>
internal sealed class ImageCache
{
    // 根据物理内存动态设置缓存数量
    private static int MaxEntries
    {
        get
        {
            // 物理内存 > 16GB → 缓存15张
            // 物理内存 > 8GB  → 缓存10张
            // 否则 → 缓存7张
            var ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (ram > 16L * 1024 * 1024 * 1024) return 15;
            if (ram > 8L * 1024 * 1024 * 1024) return 10;
            return 7;
        }
    }

    // 缓存条目
    private class CacheEntry
    {
        public string Path { get; set; } = null!;
        public BitmapSource Bitmap { get; set; } = null!;
        public LinkedListNode<CacheEntry>? Node { get; set; }
    }

    // 使用 Dictionary 实现 O(1) 查找
    private readonly Dictionary<string, CacheEntry> _map = new();
    // 使用 LinkedList 实现 O(1) 的 LRU 移动和淘汰
    private readonly LinkedList<CacheEntry> _lruList = new();
    // 用于同步的锁对象
    private readonly object _lockObject = new();

    // ── 读取（同时将条目移到链表头，刷新 LRU 顺序） ──────────────
    public BitmapSource? TryGet(string path)
    {
        lock (_lockObject)
        {
            if (!_map.TryGetValue(path, out var entry))
                return null;

            // 移动到链表头部（最近使用）
            if (entry.Node != null && entry.Node.List != null)
            {
                _lruList.Remove(entry.Node);
                _lruList.AddFirst(entry.Node);
            }

            return entry.Bitmap;
        }
    }

    public bool Contains(string path)
    {
        lock (_lockObject)
        {
            return _map.ContainsKey(path);
        }
    }

    // ── 写入（超出上限时淘汰最久未用的条目） ─────────────────────
    public void Put(string path, BitmapSource bmp)
    {
        lock (_lockObject)
        {
            // 如果已存在，更新值并移动到头部
            if (_map.TryGetValue(path, out var existingEntry))
            {
                existingEntry.Bitmap = bmp;
                if (existingEntry.Node != null && existingEntry.Node.List != null)
                {
                    _lruList.Remove(existingEntry.Node);
                    _lruList.AddFirst(existingEntry.Node);
                }
                return;
            }

            // 检查是否需要淘汰
            while (_map.Count >= MaxEntries && _lruList.Count > 0)
            {
                // 移除链表尾部（最久未使用）
                var lruEntry = _lruList.Last;
                if (lruEntry != null)
                {
                    _lruList.RemoveLast();
                    _map.Remove(lruEntry.Value.Path);
                }
            }

            // 添加新条目到链表头部
            var newEntry = new CacheEntry
            {
                Path = path,
                Bitmap = bmp
            };
            var node = _lruList.AddFirst(newEntry);
            newEntry.Node = node;
            _map[path] = newEntry;
        }
    }

    // ── 删除单条（文件被改名/删除时调用） ────────────────────────
    public void Remove(string path)
    {
        lock (_lockObject)
        {
            if (_map.TryGetValue(path, out var entry))
            {
                if (entry.Node != null && entry.Node.List != null)
                {
                    _lruList.Remove(entry.Node);
                }
                _map.Remove(path);
            }
        }
    }

    // ── 清空（切换文件夹时调用） ─────────────────────────────────
    public void Clear()
    {
        lock (_lockObject)
        {
            _map.Clear();
            _lruList.Clear();
        }
    }

    /// <summary>
    /// 获取当前缓存数量
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lockObject)
            {
                return _map.Count;
            }
        }
    }
}
