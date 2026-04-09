using System.Collections.Concurrent;
using System.Windows.Media.Imaging;

namespace ImageBrowser;

/// <summary>
/// 原图 LRU 内存缓存（最多 <see cref="MaxEntries"/> 张已解码的完整 BitmapSource）。
/// 使用 ConcurrentDictionary 保证线程安全。
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

    // 使用 ConcurrentDictionary 保证线程安全
    private readonly ConcurrentDictionary<string, BitmapSource> _map = new();
    // 使用 ConcurrentQueue 记录访问顺序（简化 LRU 实现）
    private readonly ConcurrentQueue<string> _accessQueue = new();
    // 用于同步的锁对象
    private readonly object _lockObject = new();

    // ── 读取（同时将条目移到链表头，刷新 LRU 顺序） ──────────────
    public BitmapSource? TryGet(string path)
    {
        if (!_map.TryGetValue(path, out var bmp)) return null;
        
        // 记录访问（使用锁保护队列操作）
        lock (_lockObject)
        {
            // 重新入队表示最近访问
            _accessQueue.Enqueue(path);
        }
        
        return bmp;
    }

    public bool Contains(string path) => _map.ContainsKey(path);

    // ── 写入（超出上限时淘汰最久未用的条目） ─────────────────────
    public void Put(string path, BitmapSource bmp)
    {
        // 如果已存在，更新值
        if (_map.ContainsKey(path))
        {
            _map[path] = bmp;
            lock (_lockObject)
            {
                _accessQueue.Enqueue(path);
            }
            return;
        }

        // 检查是否需要淘汰
        lock (_lockObject)
        {
            while (_map.Count >= MaxEntries && _accessQueue.TryDequeue(out var lruPath))
            {
                // 只有当路径确实在字典中且没有其他引用时才移除
                if (_map.TryRemove(lruPath, out _))
                {
                    break;
                }
            }
        }

        // 添加新条目
        _map[path] = bmp;
        lock (_lockObject)
        {
            _accessQueue.Enqueue(path);
        }
    }

    // ── 删除单条（文件被改名/删除时调用） ────────────────────────
    public void Remove(string path)
    {
        _map.TryRemove(path, out _);
        // 注意：队列中的残留会在下次淘汰时清理
    }

    // ── 清空（切换文件夹时调用） ─────────────────────────────────
    public void Clear()
    {
        _map.Clear();
        lock (_lockObject)
        {
            while (_accessQueue.TryDequeue(out _)) { }
        }
    }
}
