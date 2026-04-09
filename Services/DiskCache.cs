using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ImageBrowser.Services;

/// <summary>
/// 磁盘缓存服务
/// 缩略图持久化到磁盘，LRU淘汰策略，100MB默认限制
/// 参考 ImageGlass 的 DiskCache 实现
/// </summary>
public class DiskCache : IDisposable
{
    private readonly string _cacheDir;
    private readonly long _maxCacheSize;
    private readonly object _lockObject = new();
    private readonly Timer _cleanupTimer;

    // 默认缓存目录
    private static readonly string DefaultCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ImageBrowser", "Cache", "Thumbnails");

    // 默认缓存大小限制 100MB
    private const long DefaultMaxCacheSize = 100 * 1024 * 1024;

    // 缓存文件扩展名
    private const string CacheFileExt = ".thumb";

    /// <summary>
    /// 当前缓存大小（字节）
    /// </summary>
    public long CurrentCacheSize { get; private set; }

    /// <summary>
    /// 缓存命中率统计
    /// </summary>
    public long HitCount { get; private set; }
    public long MissCount { get; private set; }
    public double HitRate => HitCount + MissCount > 0 ? (double)HitCount / (HitCount + MissCount) : 0;

    public DiskCache(string? cacheDir = null, long? maxCacheSize = null)
    {
        _cacheDir = cacheDir ?? DefaultCacheDir;
        _maxCacheSize = maxCacheSize ?? DefaultMaxCacheSize;

        // 确保缓存目录存在
        Directory.CreateDirectory(_cacheDir);

        // 初始化时计算当前缓存大小
        UpdateCacheSize();

        // 启动定时清理（每30分钟检查一次）
        _cleanupTimer = new Timer(CleanupCallback, null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
    }

    /// <summary>
    /// 读取缓存
    /// </summary>
    public Stream? Read(string id)
    {
        lock (_lockObject)
        {
            var key = MakeKey(id);
            var filePath = Path.Combine(_cacheDir, key + CacheFileExt);

            if (!File.Exists(filePath))
            {
                MissCount++;
                return null;
            }

            try
            {
                // 更新文件访问时间（用于LRU）
                File.SetLastAccessTime(filePath, DateTime.Now);

                HitCount++;
                return File.OpenRead(filePath);
            }
            catch
            {
                MissCount++;
                return null;
            }
        }
    }

    /// <summary>
    /// 异步读取缓存
    /// </summary>
    public async Task<Stream?> ReadAsync(string id)
    {
        return await Task.Run(() => Read(id));
    }

    /// <summary>
    /// 写入缓存
    /// </summary>
    public void Write(string id, Stream data)
    {
        lock (_lockObject)
        {
            var key = MakeKey(id);
            var filePath = Path.Combine(_cacheDir, key + CacheFileExt);

            try
            {
                // 检查是否需要清理
                EnsureCacheSpace(data.Length);

                // 写入文件
                using var fileStream = File.Create(filePath);
                data.Position = 0;
                data.CopyTo(fileStream);

                // 更新缓存大小
                UpdateCacheSize();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DiskCache 写入失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 异步写入缓存
    /// </summary>
    public async Task WriteAsync(string id, Stream data)
    {
        await Task.Run(() => Write(id, data));
    }

    /// <summary>
    /// 写入缓存（字节数组版本）
    /// </summary>
    public void Write(string id, byte[] data)
    {
        using var ms = new MemoryStream(data);
        Write(id, ms);
    }

    /// <summary>
    /// 检查缓存是否存在
    /// </summary>
    public bool Exists(string id)
    {
        lock (_lockObject)
        {
            var key = MakeKey(id);
            var filePath = Path.Combine(_cacheDir, key + CacheFileExt);
            return File.Exists(filePath);
        }
    }

    /// <summary>
    /// 删除缓存
    /// </summary>
    public void Remove(string id)
    {
        lock (_lockObject)
        {
            var key = MakeKey(id);
            var filePath = Path.Combine(_cacheDir, key + CacheFileExt);

            if (File.Exists(filePath))
            {
                try
                {
                    File.Delete(filePath);
                    UpdateCacheSize();
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// 清空缓存
    /// </summary>
    public void Clear()
    {
        lock (_lockObject)
        {
            try
            {
                foreach (var file in Directory.GetFiles(_cacheDir, "*" + CacheFileExt))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch { }
                }
                CurrentCacheSize = 0;
            }
            catch { }
        }
    }

    /// <summary>
    /// 生成缓存键（MD5哈希）
    /// </summary>
    private static string MakeKey(string id)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(id));
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// 确保有足够的缓存空间
    /// </summary>
    private void EnsureCacheSpace(long requiredSpace)
    {
        if (CurrentCacheSize + requiredSpace <= _maxCacheSize)
            return;

        // 需要清理空间
        var files = Directory.GetFiles(_cacheDir, "*" + CacheFileExt)
            .Select(f => new FileInfo(f))
            .OrderBy(f => f.LastAccessTime) // LRU: 按最后访问时间排序
            .ToList();

        var spaceToFree = requiredSpace - (_maxCacheSize - CurrentCacheSize);
        var freedSpace = 0L;

        foreach (var file in files)
        {
            try
            {
                freedSpace += file.Length;
                File.Delete(file.FullName);

                if (freedSpace >= spaceToFree)
                    break;
            }
            catch { }
        }

        UpdateCacheSize();
    }

    /// <summary>
    /// 更新缓存大小统计
    /// </summary>
    private void UpdateCacheSize()
    {
        try
        {
            CurrentCacheSize = Directory.GetFiles(_cacheDir, "*" + CacheFileExt)
                .Sum(f => new FileInfo(f).Length);
        }
        catch
        {
            CurrentCacheSize = 0;
        }
    }

    /// <summary>
    /// 定时清理回调
    /// </summary>
    private void CleanupCallback(object? state)
    {
        lock (_lockObject)
        {
            // 如果缓存超过限制，清理最旧的文件
            if (CurrentCacheSize > _maxCacheSize)
            {
                EnsureCacheSpace(0);
            }

            // 清理超过30天的缓存文件
            try
            {
                var cutoffDate = DateTime.Now.AddDays(-30);
                foreach (var file in Directory.GetFiles(_cacheDir, "*" + CacheFileExt))
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        if (fileInfo.LastAccessTime < cutoffDate)
                        {
                            File.Delete(file);
                        }
                    }
                    catch { }
                }
                UpdateCacheSize();
            }
            catch { }
        }
    }

    /// <summary>
    /// 获取缓存统计信息
    /// </summary>
    public CacheStats GetStats()
    {
        lock (_lockObject)
        {
            var files = Directory.GetFiles(_cacheDir, "*" + CacheFileExt);
            return new CacheStats
            {
                TotalFiles = files.Length,
                TotalSize = CurrentCacheSize,
                MaxSize = _maxCacheSize,
                HitCount = HitCount,
                MissCount = MissCount,
                HitRate = HitRate
            };
        }
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
    }
}

/// <summary>
/// 缓存统计信息
/// </summary>
public class CacheStats
{
    public int TotalFiles { get; set; }
    public long TotalSize { get; set; }
    public long MaxSize { get; set; }
    public long HitCount { get; set; }
    public long MissCount { get; set; }
    public double HitRate { get; set; }

    public string TotalSizeFormatted => FormatBytes(TotalSize);
    public string MaxSizeFormatted => FormatBytes(MaxSize);

    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }
}
