using ImageBrowser.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace ImageBrowser.Services;

/// <summary>
/// 缩略图缓存服务
/// 融合 ImageGlass 的磁盘缓存 + LRU 内存缓存
/// </summary>
public class ThumbnailCache : IDisposable
{
    private readonly Dictionary<string, CacheEntry> _memoryCache = new();
    private readonly LinkedList<string> _lruList = new();
    private readonly string _cacheDirectory;
    private readonly int _maxMemoryCacheSize;
    private readonly long _maxDiskCacheSize;
    private long _currentDiskCacheSize;
    private readonly object _lock = new();
    private bool _disposed;
    
    public ThumbnailCache(int maxMemoryCacheSize = 100, long maxDiskCacheSizeBytes = 100 * 1024 * 1024)
    {
        _maxMemoryCacheSize = maxMemoryCacheSize;
        _maxDiskCacheSize = maxDiskCacheSizeBytes;
        _cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ImageBrowser",
            "ThumbnailCache");
        
        Directory.CreateDirectory(_cacheDirectory);
        CalculateDiskCacheSize();
    }
    
    /// <summary>
    /// 获取或创建缩略图
    /// </summary>
    public async Task<BitmapSource?> GetOrCreateAsync(string filePath, int width, int height)
    {
        if (_disposed) return null;
        
        var cacheKey = GetCacheKey(filePath, width, height);
        
        lock (_lock)
        {
            // 检查内存缓存
            if (_memoryCache.TryGetValue(cacheKey, out var entry))
            {
                // 移动到 LRU 链表头部（最近使用）
                _lruList.Remove(entry.Node);
                _lruList.AddFirst(entry.Node);
                return entry.Thumbnail;
            }
        }
        
        // 检查磁盘缓存
        var diskCached = await LoadFromDiskAsync(cacheKey);
        if (diskCached != null)
        {
            AddToMemoryCache(cacheKey, diskCached);
            return diskCached;
        }
        
        // 生成新缩略图
        var thumbnail = await ImageLoader.LoadThumbnailAsync(filePath, width, height);
        if (thumbnail != null)
        {
            AddToMemoryCache(cacheKey, thumbnail);
            await SaveToDiskAsync(cacheKey, thumbnail);
        }
        
        return thumbnail;
    }
    
    /// <summary>
    /// 预加载缩略图（后台）
    /// </summary>
    public async Task PreloadAsync(IEnumerable<string> filePaths, int width, int height)
    {
        foreach (var filePath in filePaths)
        {
            if (_disposed) break;
            
            try
            {
                await GetOrCreateAsync(filePath, width, height);
                await Task.Delay(10); // 避免阻塞
            }
            catch { }
        }
    }
    
    /// <summary>
    /// 清除缓存
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _memoryCache.Clear();
            _lruList.Clear();
        }
        
        try
        {
            if (Directory.Exists(_cacheDirectory))
            {
                Directory.Delete(_cacheDirectory, true);
                Directory.CreateDirectory(_cacheDirectory);
            }
            _currentDiskCacheSize = 0;
        }
        catch { }
    }
    
    private void AddToMemoryCache(string key, BitmapSource thumbnail)
    {
        lock (_lock)
        {
            // 如果缓存已满，移除最久未使用的
            while (_memoryCache.Count >= _maxMemoryCacheSize && _lruList.Count > 0)
            {
                var lastKey = _lruList.Last!.Value;
                _lruList.RemoveLast();
                _memoryCache.Remove(lastKey);
            }
            
            var node = _lruList.AddFirst(key);
            _memoryCache[key] = new CacheEntry
            {
                Thumbnail = thumbnail,
                Node = node,
                Timestamp = DateTime.Now
            };
        }
    }
    
    private async Task<BitmapSource?> LoadFromDiskAsync(string cacheKey)
    {
        try
        {
            var filePath = Path.Combine(_cacheDirectory, cacheKey + ".jpg");
            if (!File.Exists(filePath)) return null;
            
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = fs;
            bitmap.EndInit();
            bitmap.Freeze();
            
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
    
    private async Task SaveToDiskAsync(string cacheKey, BitmapSource thumbnail)
    {
        try
        {
            // 检查磁盘缓存大小
            await EnforceDiskCacheLimitAsync();
            
            var filePath = Path.Combine(_cacheDirectory, cacheKey + ".jpg");
            
            var encoder = new JpegBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(thumbnail));
            
            await using var fs = new FileStream(filePath, FileMode.Create);
            encoder.Save(fs);
            
            var fileInfo = new FileInfo(filePath);
            Interlocked.Add(ref _currentDiskCacheSize, fileInfo.Length);
        }
        catch { }
    }
    
    private async Task EnforceDiskCacheLimitAsync()
    {
        if (_currentDiskCacheSize < _maxDiskCacheSize) return;
        
        await Task.Run(() =>
        {
            try
            {
                var files = Directory.GetFiles(_cacheDirectory)
                    .Select(f => new FileInfo(f))
                    .OrderBy(f => f.LastAccessTime)
                    .ToList();
                
                while (_currentDiskCacheSize > _maxDiskCacheSize * 0.8 && files.Count > 0)
                {
                    var oldestFile = files[0];
                    files.RemoveAt(0);
                    
                    try
                    {
                        oldestFile.Delete();
                        Interlocked.Add(ref _currentDiskCacheSize, -oldestFile.Length);
                    }
                    catch { }
                }
            }
            catch { }
        });
    }
    
    private void CalculateDiskCacheSize()
    {
        try
        {
            _currentDiskCacheSize = Directory.GetFiles(_cacheDirectory)
                .Sum(f => new FileInfo(f).Length);
        }
        catch
        {
            _currentDiskCacheSize = 0;
        }
    }
    
    private static string GetCacheKey(string filePath, int width, int height)
    {
        var input = $"{filePath}|{width}x{height}|{File.GetLastWriteTimeUtc(filePath):O}";
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash);
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        lock (_lock)
        {
            _memoryCache.Clear();
            _lruList.Clear();
        }
    }
    
    private class CacheEntry
    {
        public BitmapSource Thumbnail { get; set; } = null!;
        public LinkedListNode<string> Node { get; set; } = null!;
        public DateTime Timestamp { get; set; }
    }
}
