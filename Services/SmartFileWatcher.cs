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
    private CancellationTokenSource? _cts;

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
        // 创建 CancellationTokenSource 用于取消操作
        _cts = new CancellationTokenSource();
        // 创建定时器，定期批量处理事件
        _processTimer = new Timer(ProcessEventsCallback, _cts.Token, DebounceIntervalMs, DebounceIntervalMs);
    }

    /// <summary>
    /// 开始监视目录
    /// </summary>
    public void StartWatching(string path)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"目录不存在: {path}");

        StopWatching();

        // 创建新的 CancellationTokenSource
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

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
        // 取消所有正在进行的操作
        _cts?.Cancel();

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
        // 检查是否已取消
        if (state is CancellationToken ct && ct.IsCancellationRequested)
            return;

        if (_eventQueue.IsEmpty) return;

        lock (_lockObject)
        {
            // 再次检查取消状态
            if (state is CancellationToken ct2 && ct2.IsCancellationRequested)
                return;

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
                        if (evt is RenamedEventArgs renamedEvt &&
                            !renamedFiles.Any(r => r.oldPath == renamedEvt.OldFullPath && r.newPath == renamedEvt.FullPath))
                        {
                            renamedFiles.Add((renamedEvt.OldFullPath, renamedEvt.FullPath));
                        }
                        break;
                }
            }

            // 触发批量事件（在 UI 线程）
            if (createdFiles.Count > 0)
            {
                InvokeOnUIThread(() => OnCreated?.Invoke(this, new FileChangedBatchEventArgs(createdFiles)));
            }

            if (deletedFiles.Count > 0)
            {
                InvokeOnUIThread(() => OnDeleted?.Invoke(this, new FileChangedBatchEventArgs(deletedFiles)));
            }

            if (changedFiles.Count > 0)
            {
                InvokeOnUIThread(() => OnChanged?.Invoke(this, new FileChangedBatchEventArgs(changedFiles)));
            }

            if (renamedFiles.Count > 0)
            {
                InvokeOnUIThread(() => OnRenamed?.Invoke(this, new FileRenamedBatchEventArgs(renamedFiles)));
            }
        }
    }

    /// <summary>
    /// 在 UI 线程上执行操作
    /// </summary>
    private void InvokeOnUIThread(Action action)
    {
        if (Dispatcher != null)
        {
            if (Dispatcher.CheckAccess())
                action();
            else
                Dispatcher.Invoke(action);
        }
        else if (SynchronizingObject != null)
        {
            if (SynchronizingObject.InvokeRequired)
                SynchronizingObject.Invoke(action, null);
            else
                action();
        }
        else
        {
            action();
        }
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        StopWatching();
        _processTimer?.Dispose();
        _cts?.Dispose();
    }
}

/// <summary>
/// 文件变化批量事件参数
/// </summary>
public class FileChangedBatchEventArgs : EventArgs
{
    public IReadOnlyList<string> Files { get; }

    public FileChangedBatchEventArgs(IReadOnlyList<string> files)
    {
        Files = files;
    }
}

/// <summary>
/// 文件重命名批量事件参数
/// </summary>
public class FileRenamedBatchEventArgs : EventArgs
{
    public IReadOnlyList<(string OldPath, string NewPath)> RenamedFiles { get; }

    public FileRenamedBatchEventArgs(IReadOnlyList<(string OldPath, string NewPath)> renamedFiles)
    {
        RenamedFiles = renamedFiles;
    }
}
