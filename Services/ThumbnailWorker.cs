using ImageBrowser.Utils;
using System;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ImageBrowser.Services;

/// <summary>
/// 缩略图生成工作器
/// 使用 QueuedWorker 实现多线程缩略图生成
/// </summary>
public class ThumbnailWorker : IDisposable
{
    private readonly QueuedWorker _worker;
    private readonly ThumbnailCache _cache;
    private readonly Dispatcher _dispatcher;

    /// <summary>
    /// 缩略图生成完成事件
    /// </summary>
    public event EventHandler<ThumbnailCompletedEventArgs>? ThumbnailCompleted;

    /// <summary>
    /// 是否正在运行
    /// </summary>
    public bool IsRunning => _worker.IsStarted;

    /// <summary>
    /// 队列中的任务数
    /// </summary>
    public int QueueCount => _worker.QueueCount;

    public ThumbnailWorker(ThumbnailCache cache, int threadCount = 4)
    {
        _cache = cache;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _worker = new QueuedWorker(threadCount)
        {
            ProcessingMode = ProcessingMode.Priority,
            PriorityQueues = 3,  // 高、中、低三级优先级
            ThreadName = "ThumbnailWorker"
        };

        _worker.DoWork += Worker_DoWork;
        _worker.RunWorkerCompleted += Worker_RunWorkerCompleted;
    }

    /// <summary>
    /// 请求生成缩略图
    /// </summary>
    /// <param name="filePath">图片路径</param>
    /// <param name="priority">优先级 (0=高, 1=中, 2=低)</param>
    public void RequestThumbnail(string filePath, int priority = 1)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        if (!File.Exists(filePath)) return;

        _worker.RunWorkerAsync(filePath, Math.Clamp(priority, 0, 2));
    }

    /// <summary>
    /// 批量请求缩略图
    /// </summary>
    public void RequestThumbnails(IEnumerable<string> filePaths, int basePriority = 1)
    {
        int index = 0;
        foreach (var filePath in filePaths)
        {
            // 前面的图片使用更高优先级
            int priority = Math.Clamp(basePriority + (index / 10), 0, 2);
            RequestThumbnail(filePath, priority);
            index++;
        }
    }

    /// <summary>
    /// 取消指定图片的缩略图生成
    /// </summary>
    public void CancelThumbnail(string filePath)
    {
        _worker.CancelAsync(filePath);
    }

    /// <summary>
    /// 取消所有待处理的缩略图生成
    /// </summary>
    public void CancelAll()
    {
        _worker.CancelAsync();
    }

    /// <summary>
    /// 暂停
    /// </summary>
    public void Pause()
    {
        _worker.Pause();
    }

    /// <summary>
    /// 恢复
    /// </summary>
    public void Resume()
    {
        _worker.Resume();
    }

    private void Worker_DoWork(object? sender, QueuedWorkerDoWorkEventArgs e)
    {
        if (e.Argument is not string filePath) return;

        try
        {
            // 生成缩略图 (200x200)
            var thumbnail = GenerateThumbnail(filePath);
            
            if (thumbnail != null)
            {
                e.Result = new ThumbnailResult 
                { 
                    FilePath = filePath, 
                    Thumbnail = thumbnail 
                };
            }
            else
            {
                e.Cancel = true;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"生成缩略图失败: {filePath}, 错误: {ex.Message}");
            e.Cancel = true;
        }
    }

    private void Worker_RunWorkerCompleted(object? sender, QueuedWorkerCompletedEventArgs e)
    {
        if (e.Error != null || e.Cancelled) return;

        var result = e.Result as ThumbnailResult;
        if (result == null) return;

        // 在UI线程触发事件
        _dispatcher.Invoke(() =>
        {
            ThumbnailCompleted?.Invoke(this, new ThumbnailCompletedEventArgs(
                result.FilePath, result.Thumbnail, e.Priority));
        });
    }

    private BitmapImage? GenerateThumbnail(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return null;

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 200; // 缩略图宽度
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _worker.Dispose();
    }

    private class ThumbnailResult
    {
        public string FilePath { get; set; } = "";
        public BitmapImage Thumbnail { get; set; } = null!;
    }
}

/// <summary>
/// 缩略图完成事件参数
/// </summary>
public class ThumbnailCompletedEventArgs : EventArgs
{
    public string FilePath { get; }
    public BitmapImage Thumbnail { get; }
    public int Priority { get; }

    public ThumbnailCompletedEventArgs(string filePath, BitmapImage thumbnail, int priority)
    {
        FilePath = filePath;
        Thumbnail = thumbnail;
        Priority = priority;
    }
}
