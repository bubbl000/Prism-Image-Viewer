using System.Collections.Concurrent;
using System.Windows;

namespace ImageBrowser.Services;

/// <summary>
/// 文件删除队列处理器
/// 后台批量处理删除事件，减少界面刷新次数
/// 参考 ImageGlass 的删除队列处理实现
/// </summary>
public class FileDeleteQueueProcessor : IDisposable
{
    private readonly ConcurrentQueue<DeleteQueueItem> _deleteQueue = new();
    private readonly Thread _processingThread;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lockObject = new();

    // 处理间隔（毫秒）
    private const int ProcessIntervalMs = 500;

    /// <summary>
    /// 批量删除处理完成事件
    /// </summary>
    public event EventHandler<DeleteProcessedEventArgs>? OnBatchProcessed;

    /// <summary>
    /// 单个文件删除事件
    /// </summary>
    public event EventHandler<DeleteQueueItem>? OnFileDeleted;

    /// <summary>
    /// 队列中的文件数量
    /// </summary>
    public int QueueCount => _deleteQueue.Count;

    /// <summary>
    /// 是否正在运行
    /// </summary>
    public bool IsRunning { get; private set; }

    public FileDeleteQueueProcessor()
    {
        _processingThread = new Thread(ProcessQueueLoop)
        {
            IsBackground = true,
            Name = "FileDeleteQueueProcessor"
        };
    }

    /// <summary>
    /// 启动处理器
    /// </summary>
    public void Start()
    {
        if (IsRunning) return;

        IsRunning = true;
        _processingThread.Start();
    }

    /// <summary>
    /// 停止处理器
    /// </summary>
    public void Stop()
    {
        IsRunning = false;
        _cts.Cancel();
    }

    /// <summary>
    /// 添加文件到删除队列
    /// </summary>
    public void Enqueue(string filePath, int? currentIndex = null)
    {
        var item = new DeleteQueueItem
        {
            FilePath = filePath,
            CurrentIndex = currentIndex,
            EnqueueTime = DateTime.Now
        };

        _deleteQueue.Enqueue(item);
    }

    /// <summary>
    /// 批量添加文件到删除队列
    /// </summary>
    public void EnqueueRange(IEnumerable<string> filePaths, int? currentIndex = null)
    {
        foreach (var filePath in filePaths)
        {
            Enqueue(filePath, currentIndex);
        }
    }

    /// <summary>
    /// 清空队列
    /// </summary>
    public void Clear()
    {
        while (_deleteQueue.TryDequeue(out _)) { }
    }

    /// <summary>
    /// 队列处理循环
    /// </summary>
    private void ProcessQueueLoop()
    {
        while (IsRunning && !_cts.Token.IsCancellationRequested)
        {
            try
            {
                // 等待一段时间，收集批量删除事件
                Thread.Sleep(ProcessIntervalMs);

                // 检查是否有待处理的事件
                if (_deleteQueue.IsEmpty) continue;

                // 收集所有待处理的删除事件
                var batch = new List<DeleteQueueItem>();
                while (_deleteQueue.TryDequeue(out var item))
                {
                    batch.Add(item);
                }

                if (batch.Count > 0)
                {
                    ProcessBatch(batch);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"删除队列处理错误: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 批量处理删除事件
    /// </summary>
    private void ProcessBatch(List<DeleteQueueItem> batch)
    {
        // 去重（同一文件可能被多次添加）
        var uniqueFiles = batch
            .GroupBy(x => x.FilePath.ToLowerInvariant())
            .Select(g => g.First())
            .ToList();

        // 触发单个删除事件
        foreach (var item in uniqueFiles)
        {
            try
            {
                OnFileDeleted?.Invoke(this, item);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"处理删除事件失败: {item.FilePath}, 错误: {ex.Message}");
            }
        }

        // 触发批量处理完成事件
        var args = new DeleteProcessedEventArgs
        {
            DeletedFiles = uniqueFiles.Select(x => x.FilePath).ToList(),
            ProcessedCount = uniqueFiles.Count,
            ProcessTime = DateTime.Now
        };

        // 在 UI 线程上触发事件
        Application.Current?.Dispatcher.Invoke(() =>
        {
            OnBatchProcessed?.Invoke(this, args);
        });
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}

/// <summary>
/// 删除队列项
/// </summary>
public class DeleteQueueItem
{
    /// <summary>
    /// 文件路径
    /// </summary>
    public string FilePath { get; set; } = "";

    /// <summary>
    /// 当前显示的图片索引（用于导航）
    /// </summary>
    public int? CurrentIndex { get; set; }

    /// <summary>
    /// 入队时间
    /// </summary>
    public DateTime EnqueueTime { get; set; }
}

/// <summary>
/// 删除处理完成事件参数
/// </summary>
public class DeleteProcessedEventArgs : EventArgs
{
    /// <summary>
    /// 已删除的文件路径列表
    /// </summary>
    public List<string> DeletedFiles { get; set; } = new();

    /// <summary>
    /// 处理的文件数量
    /// </summary>
    public int ProcessedCount { get; set; }

    /// <summary>
    /// 处理时间
    /// </summary>
    public DateTime ProcessTime { get; set; }
}
