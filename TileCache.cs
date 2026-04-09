using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageBrowser;

/// <summary>
/// Tile 缓存项
/// </summary>
public readonly record struct TileKey(
    string FilePath,
    int Level,      // 金字塔层级 0=原图, 1=1/2, 2=1/4...
    int X,          // Tile X 坐标
    int Y           // Tile Y 坐标
);

/// <summary>
/// Tile 图像缓存（LRU策略）
/// 用于超大图像（>8192px）的分块加载
/// </summary>
public sealed class TileCache
{
    // 默认最大缓存 Tile 数量
    private const int DefaultMaxTiles = 256;
    
    // 每个层级的最大缓存数量
    private const int MaxTilesPerLevel = 64;

    // 缓存存储
    private readonly ConcurrentDictionary<TileKey, BitmapSource> _cache = new();
    
    // LRU 访问顺序记录
    private readonly LinkedList<TileKey> _lruList = new();
    private readonly object _lruLock = new();

    // 最大缓存数量
    private readonly int _maxTiles;

    // 每个层级的 Tile 计数
    private readonly ConcurrentDictionary<int, int> _levelCounts = new();

    public TileCache(int maxTiles = DefaultMaxTiles)
    {
        _maxTiles = maxTiles;
    }

    /// <summary>
    /// 尝试获取缓存的 Tile
    /// </summary>
    public BitmapSource? TryGet(TileKey key)
    {
        if (_cache.TryGetValue(key, out var bitmap))
        {
            // 更新 LRU 顺序
            lock (_lruLock)
            {
                _lruList.Remove(key);
                _lruList.AddFirst(key);
            }
            return bitmap;
        }
        return null;
    }

    /// <summary>
    /// 将 Tile 放入缓存
    /// </summary>
    public void Put(TileKey key, BitmapSource bitmap)
    {
        // 如果已存在，更新值和 LRU 顺序
        if (_cache.ContainsKey(key))
        {
            _cache[key] = bitmap;
            lock (_lruLock)
            {
                _lruList.Remove(key);
                _lruList.AddFirst(key);
            }
            return;
        }

        // 检查该层级的 Tile 数量
        var levelCount = _levelCounts.AddOrUpdate(key.Level, 1, (_, count) => count + 1);
        
        // 如果该层级 Tile 过多，清理该层级的旧 Tile
        if (levelCount > MaxTilesPerLevel)
        {
            CleanupLevel(key.Level);
        }

        // 检查总缓存数量
        while (_cache.Count >= _maxTiles)
        {
            EvictLRU();
        }

        // 添加到缓存
        _cache[key] = bitmap;
        lock (_lruLock)
        {
            _lruList.AddFirst(key);
        }
    }

    /// <summary>
    /// 检查是否包含指定的 Tile
    /// </summary>
    public bool Contains(TileKey key)
    {
        return _cache.ContainsKey(key);
    }

    /// <summary>
    /// 移除指定的 Tile
    /// </summary>
    public bool Remove(TileKey key)
    {
        if (_cache.TryRemove(key, out _))
        {
            lock (_lruLock)
            {
                _lruList.Remove(key);
            }
            _levelCounts.AddOrUpdate(key.Level, 0, (_, count) => Math.Max(0, count - 1));
            return true;
        }
        return false;
    }

    /// <summary>
    /// 清空整个缓存
    /// </summary>
    public void Clear()
    {
        _cache.Clear();
        lock (_lruLock)
        {
            _lruList.Clear();
        }
        _levelCounts.Clear();
    }

    /// <summary>
    /// 清空指定文件的缓存
    /// </summary>
    public void ClearFile(string filePath)
    {
        var keysToRemove = _cache.Keys.Where(k => 
            k.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)).ToList();
        
        foreach (var key in keysToRemove)
        {
            Remove(key);
        }
    }

    /// <summary>
    /// 获取缓存统计信息
    /// </summary>
    public (int Total, int Levels) GetStats()
    {
        return (_cache.Count, _levelCounts.Count);
    }

    /// <summary>
    /// 驱逐最久未使用的 Tile
    /// </summary>
    private void EvictLRU()
    {
        lock (_lruLock)
        {
            if (_lruList.Last == null) return;
            
            var key = _lruList.Last.Value;
            _lruList.RemoveLast();
            _cache.TryRemove(key, out _);
            _levelCounts.AddOrUpdate(key.Level, 0, (_, count) => Math.Max(0, count - 1));
        }
    }

    /// <summary>
    /// 清理指定层级的旧 Tile
    /// </summary>
    private void CleanupLevel(int level)
    {
        lock (_lruLock)
        {
            var levelTiles = _lruList
                .Where(k => k.Level == level)
                .Take(_levelCounts[level] - MaxTilesPerLevel + 1)
                .ToList();

            foreach (var key in levelTiles)
            {
                _lruList.Remove(key);
                _cache.TryRemove(key, out _);
            }

            _levelCounts[level] = MaxTilesPerLevel - 1;
        }
    }
}

/// <summary>
/// Tile 信息
/// </summary>
public readonly record struct TileInfo(
    int X,
    int Y,
    int Level,
    int PixelX,     // 在原图中的像素坐标
    int PixelY,
    int Width,      // Tile 实际宽度（边缘可能不足 512）
    int Height,
    double Scale    // 当前层级的缩放比例
);

/// <summary>
/// 视口信息
/// </summary>
public readonly record struct ViewportInfo(
    double X,
    double Y,
    double Width,
    double Height,
    double Scale
);
