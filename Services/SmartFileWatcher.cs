using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Windows.Threading;

namespace ImageBrowser.Services;

/// <summary>
/// 增强型文件监视器
/// 批量处理文件变化事件，防抖，自动 Invoke 到 UI 线程
/// 参考 ImageGlass 的 FileSystemWatcherEx 实现
/// </summary>
public class SmartFileWatcher : IDisposable
{
    private FileSystemWatcher? _watcher;
    private readonly ConcurrentQueue<FileSystemEventArgs> _eventQueue = new();
    private readonly Timer _processTimer;
    private readonly object _lockObject = new();

    // 防抖间隔（毫秒）
    private const int DebounceIntervalMs = 500;

    /// <summary>
    /// 文件创建事件（批量）
    /// </summary>
    public event EventHandler<FileChangedBatchEventArgs>? OnCreated;

    /// <summary>
    /// 文件删除事件（批量）
    /// </summary>
    public event EventHandler<FileChangedBatchEventArgs>? OnDeleted;

    /// <summary>
    /// 文件重命名事件（批量）
    /// </summary>
    public event EventHandler<FileRenamedBatchEventArgs>? OnRenamed;

    /// <summary>
    /// 文件修改事件（批量）
    /// </summary>
    public event EventHandler<FileChangedBatchEventArgs>? OnChanged;

    /// <summary>
    /// 是否正在监视
    /// </summary>
    public bool IsWatching => _watcher?.EnableRaisingEvents ?? false;

    /// <summary>
    /// 监视的目录路径
    /// </summary>
    public string? WatchPath { get; private set; }

    /// <summary>
    /// 同步对象（用于自动 Invoke 到 UI 线程）
    /// </summary>
    public ISynchronizeInvoke? SynchronizingObject { get; set; }

    /// <summary>
    /// Dispatcher（WPF 中优先使用）
    /// </summary>
    public Dispatcher? Dispatcher { get; set; }

    /// <summary>
    /// 是否包含子目录
    /// </summary>
    public bool IncludeSubdirectories
    {
        get => _watcher?.IncludeSubdirectories ?? false;
        set
        {
            if (_watcher != null)
                _watcher.IncludeSubdirectories = value;
        }
    }

    /// <summary>
    /// 过滤的文件类型
    /// </summary>
    public string Filter
    {
        get => _watcher?.Filter ?? "*.*";
        set
        {
            if (_watcher != null)
                _watcher.Filter = value;
        }
    }

    public SmartFileWatcher()
    {
        // 创建定时器，定期批量处理事件
        _processTimer = new Timer(ProcessEventsCallback, null, DebounceIntervalMs, DebounceIntervalMs);
    }

    /// <summary>
    /// 开始监视目录
    /// </summary>
    public void StartWatching(string path)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"目录不存在: {path}");

        StopWatching();

        WatchPath = path;

        _watcher = new FileSystemWatcher(path)
        {
            NotifyFilter = NotifyFilters.FileName |
                          NotifyFilters.LastWrite |
                          NotifyFilters.CreationTime |
                          NotifyFilters.Size,
            IncludeSubdirectories = false,
            Filter = "*.*",
            EnableRaisingEvents = false
        };

        // 设置同步对象（如果提供了）
        if (SynchronizingObject != null)
            _watcher.SynchronizingObject = SynchronizingObject;

        // 订阅事件
        _watcher.Created += OnFileCreated;
        _watcher.Deleted += OnFileDeleted;
        _watcher.Renamed += OnFileRenamed;
        _watcher.Changed += OnFileChanged;
        _watcher.Error += OnWatcherError;

        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>
    /// 停止监视
    /// </summary>
    public void StopWatching()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnFileCreated;
            _watcher.Deleted -= OnFileDeleted;
            _watcher.Renamed -= OnFileRenamed;
            _watcher.Changed -= OnFileChanged;
            _watcher.Error -= OnWatcherError;
            _watcher.Dispose();
            _watcher = null;
        }

        // 清空队列
        while (_eventQueue.TryDequeue(out _)) { }

        WatchPath = null;
    }

    /// <summary>
    /// 暂停监视（不清空队列）
    /// </summary>
    public void Pause()
    {
        if (_watcher != null)
            _watcher.EnableRaisingEvents = false;
    }

    /// <summary>
    /// 恢复监视
    /// </summary>
    public void Resume()
    {
        if (_watcher != null)
            _watcher.EnableRaisingEvents = true;
    }

    #region 事件处理

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        _eventQueue.Enqueue(e);
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        _eventQueue.Enqueue(e);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        _eventQueue.Enqueue(e);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        _eventQueue.Enqueue(e);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // 发生错误时尝试重新启动监视器
        System.Diagnostics.Debug.WriteLine($"文件监视器错误: {e.GetException().Message}");

        try
        {
            if (WatchPath != null)
            {
                StopWatching();
                StartWatching(WatchPath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"重新启动监视器失败: {ex.Message}");
        }
    }

    #endregion

    /// <summary>
    /// 批量处理事件
    /// </summary>
    private void ProcessEventsCallback(object? state)
    {
        if (_eventQueue.IsEmpty) return;

        lock (_lockObject)
        {
            // 收集所有事件
            var createdFiles = new List<string>();
            var deletedFiles = new List<string>();
            var changedFiles = new List<string>();
            var renamedFiles = new List<(string oldPath, string newPath)>();

            while (_eventQueue.TryDequeue(out var evt))
            {
                switch (evt.ChangeType)
                {
                    case WatcherChangeTypes.Created:
                        if (!createdFiles.Contains(evt.FullPath))
                            createdFiles.Add(evt.FullPath);
                        break;

                    case WatcherChangeTypes.Deleted:
                        if (!deletedFiles.Contains(evt.FullPath))
                            deletedFiles.Add(evt.FullPath);
                        break;

                    case WatcherChangeTypes.Changed:
                        if (!changedFiles.Contains(evt.FullPath))
                            changedFiles.Add(evt.FullPath);
                        break;

                    case WatcherChangeTypes.Renamed:
                        if (evt is RenamedEventArgs renamedArgs)
                        {
                            renamedFiles.Add((renamedArgs.OldFullPath, renamedArgs.FullPath));
                        }
                        break;
                }
            }

            // 触发批量事件
            if (createdFiles.Count > 0)
            {
                RaiseEvent(OnCreated, new FileChangedBatchEventArgs
                {
                    FilePaths = createdFiles,
                    ChangeType = WatcherChangeTypes.Created
                });
            }

            if (deletedFiles.Count > 0)
            {
                RaiseEvent(OnDeleted, new FileChangedBatchEventArgs
                {
                    FilePaths = deletedFiles,
                    ChangeType = WatcherChangeTypes.Deleted
                });
            }

            if (changedFiles.Count > 0)
            {
                RaiseEvent(OnChanged, new FileChangedBatchEventArgs
                {
                    FilePaths = changedFiles,
                    ChangeType = WatcherChangeTypes.Changed
                });
            }

            if (renamedFiles.Count > 0)
            {
                RaiseEvent(OnRenamed, new FileRenamedBatchEventArgs
                {
                    RenamedFiles = renamedFiles
                });
            }
        }
    }

    /// <summary>
    /// 安全地触发事件（自动 Invoke 到 UI 线程）
    /// </summary>
    private void RaiseEvent<T>(EventHandler<T>? handler, T args) where T : EventArgs
    {
        if (handler == null) return;

        // 优先使用 Dispatcher（WPF）
        if (Dispatcher != null)
        {
            if (Dispatcher.CheckAccess())
            {
                handler(this, args);
            }
            else
            {
                Dispatcher.Invoke(() => handler(this, args));
            }
            return;
        }

        // 回退到 ISynchronizeInvoke（WinForms）
        if (SynchronizingObject != null && SynchronizingObject.InvokeRequired)
        {
            SynchronizingObject.Invoke(() => handler(this, args), null);
        }
        else
        {
            handler(this, args);
        }
    }

    public void Dispose()
    {
        StopWatching();
        _processTimer?.Dispose();
    }
}

/// <summary>
/// 文件变化批量事件参数
/// </summary>
public class FileChangedBatchEventArgs : EventArgs
{
    /// <summary>
    /// 变化的文件路径列表
    /// </summary>
    public List<string> FilePaths { get; set; } = new();

    /// <summary>
    /// 变化类型
    /// </summary>
    public WatcherChangeTypes ChangeType { get; set; }

    /// <summary>
    /// 事件时间
    /// </summary>
    public DateTime EventTime { get; set; } = DateTime.Now;
}

/// <summary>
/// 文件重命名批量事件参数
/// </summary>
public class FileRenamedBatchEventArgs : EventArgs
{
    /// <summary>
    /// 重命名的文件列表 (旧路径, 新路径)
    /// </summary>
    public List<(string oldPath, string newPath)> RenamedFiles { get; set; } = new();

    /// <summary>
    /// 事件时间
    /// </summary>
    public DateTime EventTime { get; set; } = DateTime.Now;
}
