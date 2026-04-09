using ImageBrowser.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows.Media.Imaging;

namespace ImageBrowser.Services;

/// <summary>
/// 图像加速服务
/// 后台预加载、优先级队列管理、自动资源释放
/// </summary>
public class ImageBooster : IDisposable
{
    private readonly BackgroundWorker _worker = new();
    private readonly object _lockObj = new();
    
    // 图片列表
    private List<ImageItem> _imgList { get; } = [];
    
    // 待加载队列（优先级排序）
    private List<int> _queuedList { get; } = [];
    
    // 待释放队列
    private List<int> _freeList { get; } = [];
    
    // 当前显示的图片索引
    private int _currentIndex = -1;
    
    // 预加载范围（前后各多少张）
    private int _preloadRange = 3;
    
    // 最大缓存数量
    private int _maxCacheCount = 10;
    
    // 是否正在运行
    public bool IsRunning => _worker.IsBusy;
    
    // 当前加载的图片数量
    public int LoadedCount => _imgList.Count(i => i.IsLoaded);
    
    // 队列中的任务数量
    public int QueueCount 
    { 
        get 
        { 
            lock (_lockObj) 
            { 
                return _queuedList.Count; 
            } 
        } 
    }

    public ImageBooster()
    {
        _worker.WorkerSupportsCancellation = true;
        _worker.WorkerReportsProgress = true;
        _worker.DoWork += Worker_DoWork;
        _worker.ProgressChanged += Worker_ProgressChanged;
        _worker.RunWorkerCompleted += Worker_RunWorkerCompleted;
    }

    /// <summary>
    /// 设置图片列表
    /// </summary>
    public void SetImageList(List<ImageItem> images)
    {
        lock (_lockObj)
        {
            _imgList.Clear();
            _imgList.AddRange(images);
            _queuedList.Clear();
            _freeList.Clear();
            _currentIndex = -1;
        }
    }

    /// <summary>
    /// 设置当前显示的图片索引
    /// </summary>
    public void SetCurrentIndex(int index)
    {
        if (index < 0 || index >= _imgList.Count) return;
        
        lock (_lockObj)
        {
            _currentIndex = index;
            UpdateQueues();
        }
        
        // 触发后台加载
        if (!_worker.IsBusy)
        {
            _worker.RunWorkerAsync();
        }
    }

    /// <summary>
    /// 更新加载和释放队列
    /// </summary>
    private void UpdateQueues()
    {
        _queuedList.Clear();
        _freeList.Clear();
        
        if (_currentIndex < 0 || _imgList.Count == 0) return;
        
        // 计算需要预加载的索引
        var preloadIndices = new List<int>();
        
        // 当前图片优先级最高
        preloadIndices.Add(_currentIndex);
        
        // 前后预加载
        for (int i = 1; i <= _preloadRange; i++)
        {
            // 前面的图片
            int prevIndex = _currentIndex - i;
            if (prevIndex >= 0)
            {
                preloadIndices.Add(prevIndex);
            }
            
            // 后面的图片
            int nextIndex = _currentIndex + i;
            if (nextIndex < _imgList.Count)
            {
                preloadIndices.Add(nextIndex);
            }
        }
        
        // 添加到加载队列（未加载的）
        foreach (var idx in preloadIndices)
        {
            if (!_imgList[idx].IsLoaded && !_queuedList.Contains(idx))
            {
                _queuedList.Add(idx);
            }
        }
        
        // 确定需要释放的图片
        var loadedIndices = _imgList
            .Select((item, idx) => new { Item = item, Index = idx })
            .Where(x => x.Item.IsLoaded)
            .Select(x => x.Index)
            .Where(idx => !preloadIndices.Contains(idx))
            .ToList();
        
        // 如果超过最大缓存数，释放最远的图片
        if (loadedIndices.Count > _maxCacheCount)
        {
            var sortedByDistance = loadedIndices
                .Select(idx => new { Index = idx, Distance = Math.Abs(idx - _currentIndex) })
                .OrderByDescending(x => x.Distance)
                .Take(loadedIndices.Count - _maxCacheCount)
                .Select(x => x.Index);
            
            _freeList.AddRange(sortedByDistance);
        }
    }

    /// <summary>
    /// 后台工作线程
    /// </summary>
    private void Worker_DoWork(object? sender, DoWorkEventArgs e)
    {
        while (!_worker.CancellationPending)
        {
            int? indexToLoad = null;
            int? indexToFree = null;
            
            lock (_lockObj)
            {
                // 优先释放资源
                if (_freeList.Count > 0)
                {
                    indexToFree = _freeList[0];
                    _freeList.RemoveAt(0);
                }
                // 然后加载图片
                else if (_queuedList.Count > 0)
                {
                    indexToLoad = _queuedList[0];
                    _queuedList.RemoveAt(0);
                }
            }
            
            // 执行释放
            if (indexToFree.HasValue)
            {
                try
                {
                    FreeImage(indexToFree.Value);
                    _worker.ReportProgress(0, new BoosterProgress 
                    { 
                        Action = BoosterAction.Released, 
                        Index = indexToFree.Value 
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"释放图片失败: {ex.Message}");
                }
            }
            
            // 执行加载
            if (indexToLoad.HasValue)
            {
                try
                {
                    LoadImage(indexToLoad.Value);
                    _worker.ReportProgress(0, new BoosterProgress 
                    { 
                        Action = BoosterAction.Loaded, 
                        Index = indexToLoad.Value 
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"加载图片失败: {ex.Message}");
                }
            }
            
            // 如果没有任务，退出循环
            if (!indexToLoad.HasValue && !indexToFree.HasValue)
            {
                break;
            }
            
            // 短暂休眠，避免占用过多 CPU
            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// 加载指定索引的图片（异步版本，避免 .Result 死锁）
    /// </summary>
    private async Task LoadImageAsync(int index)
    {
        if (index < 0 || index >= _imgList.Count) return;
        
        var item = _imgList[index];
        if (item.IsLoaded) return;
        
        // 使用 WicImageLoader 加载缩略图（使用 await 避免死锁）
        var thumbnail = await WicImageLoader.LoadThumbnailAsync(item.FilePath, 200, 200).ConfigureAwait(false);
        if (thumbnail != null)
        {
            item.Thumbnail = thumbnail;
        }
    }

    /// <summary>
    /// 加载指定索引的图片（同步包装，用于兼容旧代码）
    /// </summary>
    private void LoadImage(int index)
    {
        // 使用 GetAwaiter().GetResult() 比 .Result 更安全
        // 但仍然建议尽可能使用 LoadImageAsync
        LoadImageAsync(index).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 释放指定索引的图片资源
    /// </summary>
    private void FreeImage(int index)
    {
        if (index < 0 || index >= _imgList.Count) return;
        
        var item = _imgList[index];
        if (!item.IsLoaded) return;
        
        // 释放缩略图资源
        item.Thumbnail = null;
    }

    /// <summary>
    /// 进度报告
    /// </summary>
    private void Worker_ProgressChanged(object? sender, ProgressChangedEventArgs e)
    {
        if (e.UserState is BoosterProgress progress)
        {
            ProgressChanged?.Invoke(this, progress);
        }
    }

    /// <summary>
    /// 工作完成
    /// </summary>
    private void Worker_RunWorkerCompleted(object? sender, RunWorkerCompletedEventArgs e)
    {
        WorkCompleted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 进度变化事件
    /// </summary>
    public event EventHandler<BoosterProgress>? ProgressChanged;

    /// <summary>
    /// 工作完成事件
    /// </summary>
    public event EventHandler? WorkCompleted;

    /// <summary>
    /// 停止后台工作
    /// </summary>
    public void Stop()
    {
        if (_worker.IsBusy)
        {
            _worker.CancelAsync();
        }
    }

    /// <summary>
    /// 清空所有缓存
    /// </summary>
    public void ClearCache()
    {
        lock (_lockObj)
        {
            foreach (var item in _imgList.Where(i => i.IsLoaded))
            {
                item.Thumbnail = null;
            }
            _queuedList.Clear();
            _freeList.Clear();
        }
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        Stop();
        ClearCache();
        _worker.Dispose();
    }
}

/// <summary>
/// 加速服务操作类型
/// </summary>
public enum BoosterAction
{
    Loaded,
    Released
}

/// <summary>
/// 加速服务进度信息
/// </summary>
public class BoosterProgress
{
    public BoosterAction Action { get; set; }
    public int Index { get; set; }
    public string? FileName { get; set; }
}
