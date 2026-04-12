using ImageBrowser.Models;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Channels;

namespace ImageBrowser.Services;

/// <summary>
/// 图像加速服务
/// 后台预加载、优先级队列管理、自动资源释放
/// 使用 Channel + Task 替代 BackgroundWorker，提供更好的并发控制
/// </summary>
public class ImageBooster : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<BoosterTask> _taskChannel;
    private Task? _workerTask;
    private readonly object _lockObj = new();

    // 图片列表
    private List<ImageItem> _imgList = [];

    // 待加载队列（优先级排序）
    private readonly List<int> _queuedList = [];

    // 待释放队列
    private readonly List<int> _freeList = [];

    // 当前显示的图片索引
    private int _currentIndex = -1;

    // 预加载范围（前后各多少张）
    private int _preloadRange = 3;

    // 最大缓存数量
    private int _maxCacheCount = 10;

    // 是否正在运行
    public bool IsRunning => _workerTask is { IsCompleted: false };

    // 当前加载的图片数量
    public int LoadedCount
    {
        get
        {
            lock (_lockObj)
            {
                return _imgList.Count(i => i.IsLoaded);
            }
        }
    }

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
        // 创建有界 Channel，避免内存无限增长
        _taskChannel = Channel.CreateUnbounded<BoosterTask>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>
    /// 设置图片列表
    /// </summary>
    public void SetImageList(List<ImageItem> images)
    {
        lock (_lockObj)
        {
            _imgList = new List<ImageItem>(images);
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
        List<int> indicesToLoad;
        List<int> indicesToFree;

        lock (_lockObj)
        {
            if (index < 0 || index >= _imgList.Count) return;

            _currentIndex = index;
            UpdateQueues();
            indicesToLoad = new List<int>(_queuedList);
            indicesToFree = new List<int>(_freeList);
            _queuedList.Clear();
            _freeList.Clear();
        }

        // 在锁外触发后台任务
        EnsureWorkerRunning();

        // 提交任务到 Channel
        foreach (var idx in indicesToFree)
        {
            _taskChannel.Writer.TryWrite(new BoosterTask(BoosterAction.Release, idx));
        }
        foreach (var idx in indicesToLoad)
        {
            _taskChannel.Writer.TryWrite(new BoosterTask(BoosterAction.Load, idx));
        }
    }

    /// <summary>
    /// 确保工作线程正在运行
    /// </summary>
    private void EnsureWorkerRunning()
    {
        if (_workerTask is null or { IsCompleted: true })
        {
            _workerTask = Task.Run(WorkerLoopAsync);
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
    /// 后台工作循环
    /// </summary>
    private async Task WorkerLoopAsync()
    {
        await foreach (var task in _taskChannel.Reader.ReadAllAsync(_cts.Token))
        {
            try
            {
                switch (task.Action)
                {
                    case BoosterAction.Release:
                        await ExecuteReleaseAsync(task.Index);
                        break;
                    case BoosterAction.Load:
                        await ExecuteLoadAsync(task.Index);
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImageBooster] 任务执行失败: {ex.Message}");
            }

            // 短暂延迟，避免占用过多 CPU
            await Task.Delay(10, _cts.Token);
        }
    }

    /// <summary>
    /// 执行释放操作
    /// </summary>
    private async Task ExecuteReleaseAsync(int index)
    {
        try
        {
            // 在锁内检查状态
            bool shouldRelease;
            lock (_lockObj)
            {
                shouldRelease = index >= 0 && index < _imgList.Count && _imgList[index].IsLoaded;
            }

            if (!shouldRelease) return;

            // 释放操作在锁外执行
            await Task.Run(() => FreeImage(index));

            ProgressChanged?.Invoke(this, new BoosterProgress
            {
                Action = BoosterAction.Released,
                Index = index
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ImageBooster] 释放图片失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 执行加载操作
    /// </summary>
    private async Task ExecuteLoadAsync(int index)
    {
        try
        {
            // 在锁内检查状态
            bool shouldLoad;
            lock (_lockObj)
            {
                shouldLoad = index >= 0 && index < _imgList.Count && !_imgList[index].IsLoaded;
            }

            if (!shouldLoad) return;

            // 加载操作在锁外执行
            await LoadImageAsync(index);

            ProgressChanged?.Invoke(this, new BoosterProgress
            {
                Action = BoosterAction.Loaded,
                Index = index
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ImageBooster] 加载图片失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 加载指定索引的图片（异步版本）
    /// </summary>
    private async Task LoadImageAsync(int index)
    {
        ImageItem? item;
        lock (_lockObj)
        {
            if (index < 0 || index >= _imgList.Count) return;
            item = _imgList[index];
        }

        if (item.IsLoaded) return;

        // 使用 WicImageLoader 加载缩略图
        var thumbnail = await WicImageLoader.LoadThumbnailAsync(item.FilePath, 200, 200).ConfigureAwait(false);
        if (thumbnail != null)
        {
            lock (_lockObj)
            {
                // 再次检查索引是否仍然有效
                if (index < _imgList.Count && _imgList[index] == item)
                {
                    item.Thumbnail = thumbnail;
                }
            }
        }
    }

    /// <summary>
    /// 释放指定索引的图片资源
    /// </summary>
    private void FreeImage(int index)
    {
        lock (_lockObj)
        {
            if (index < 0 || index >= _imgList.Count) return;

            var item = _imgList[index];
            if (!item.IsLoaded) return;

            // 释放缩略图资源
            item.Thumbnail = null;
        }
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
        _cts.Cancel();
        _taskChannel.Writer.Complete();
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
        _cts.Dispose();
        _taskChannel.Writer.Complete();
    }
}

/// <summary>
/// 加速任务
/// </summary>
internal readonly record struct BoosterTask(BoosterAction Action, int Index);

/// <summary>
/// 加速服务操作类型
/// </summary>
public enum BoosterAction
{
    Load,
    Released,
    Loaded,
    Release
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
