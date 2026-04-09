using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;

namespace ImageBrowser.Utils;

/// <summary>
/// 处理模式
/// </summary>
public enum ProcessingMode
{
    /// <summary>先进先出</summary>
    FIFO,
    /// <summary>后进先出</summary>
    LIFO,
    /// <summary>优先级</summary>
    Priority
}

/// <summary>
/// 队列工作器事件参数
/// </summary>
public class QueuedWorkerDoWorkEventArgs : DoWorkEventArgs
{
    public int Priority { get; }
    public object? AsyncOpId { get; }

    public QueuedWorkerDoWorkEventArgs(object? argument, int priority, object? asyncOpId) 
        : base(argument)
    {
        Priority = priority;
        AsyncOpId = asyncOpId;
    }
}

/// <summary>
/// 队列工作器完成事件参数
/// </summary>
public class QueuedWorkerCompletedEventArgs : AsyncCompletedEventArgs
{
    public object? Result { get; }
    public int Priority { get; }

    public QueuedWorkerCompletedEventArgs(object? result, Exception? error, bool cancelled, int priority)
        : base(error, cancelled, null)
    {
        Result = result;
        Priority = priority;
    }
}

/// <summary>
/// 多线程队列工作器
/// 支持 FIFO/LIFO/Priority 处理模式
/// </summary>
public class QueuedWorker : Component
{
    #region 成员变量

    private readonly object _lockObject = new();
    private Thread[] _threads = [];
    private int _threadCount = 5;
    private string _threadName = "QueuedWorker";
    private bool _isBackground = true;
    
    private bool _isStopping = false;
    private bool _isStarted = false;
    private bool _isPaused = false;
    
    private ProcessingMode _processingMode = ProcessingMode.FIFO;
    private int _priorityQueues = 5;
    
    // 优先级队列数组
    private LinkedList<WorkItem>[] _priorityItems = [];
    // 普通队列
    private LinkedList<WorkItem> _items = new();
    
    // 取消标记
    private readonly HashSet<object> _cancelledItems = new();

    #endregion

    #region 属性

    /// <summary>
    /// 处理模式
    /// </summary>
    public ProcessingMode ProcessingMode
    {
        get => _processingMode;
        set
        {
            if (_isStarted)
                throw new InvalidOperationException("工作器已启动，不能更改处理模式");
            _processingMode = value;
            BuildWorkQueue();
        }
    }

    /// <summary>
    /// 优先级队列数量
    /// </summary>
    public int PriorityQueues
    {
        get => _priorityQueues;
        set
        {
            if (_isStarted)
                throw new InvalidOperationException("工作器已启动，不能更改优先级队列数");
            if (value < 1) value = 1;
            _priorityQueues = value;
            BuildWorkQueue();
        }
    }

    /// <summary>
    /// 工作线程数
    /// </summary>
    public int Threads
    {
        get => _threadCount;
        set
        {
            if (_isStarted)
                throw new InvalidOperationException("工作器已启动，不能更改线程数");
            _threadCount = Math.Max(1, value);
            CreateThreads();
        }
    }

    /// <summary>
    /// 线程名称
    /// </summary>
    public string ThreadName
    {
        get => _threadName;
        set => _threadName = value;
    }

    /// <summary>
    /// 是否为后台线程
    /// </summary>
    public bool IsBackground
    {
        get => _isBackground;
        set
        {
            _isBackground = value;
            foreach (var thread in _threads)
            {
                if (thread != null)
                    thread.IsBackground = value;
            }
        }
    }

    /// <summary>
    /// 是否已启动
    /// </summary>
    public bool IsStarted => _isStarted;

    /// <summary>
    /// 是否暂停
    /// </summary>
    public bool IsPaused => _isPaused;

    /// <summary>
    /// 队列中的项目数
    /// </summary>
    public int QueueCount
    {
        get
        {
            lock (_lockObject)
            {
                int count = _items.Count;
                if (_priorityItems != null)
                {
                    foreach (var queue in _priorityItems)
                        count += queue.Count;
                }
                return count;
            }
        }
    }

    #endregion

    #region 事件

    /// <summary>
    /// 执行工作
    /// </summary>
    public event EventHandler<QueuedWorkerDoWorkEventArgs>? DoWork;

    /// <summary>
    /// 工作完成
    /// </summary>
    public event EventHandler<QueuedWorkerCompletedEventArgs>? RunWorkerCompleted;

    #endregion

    #region 构造函数

    public QueuedWorker()
    {
        CreateThreads();
        BuildWorkQueue();
    }

    public QueuedWorker(int threadCount) : this()
    {
        Threads = threadCount;
    }

    #endregion

    #region 公共方法

    /// <summary>
    /// 异步执行工作
    /// </summary>
    /// <param name="argument">参数</param>
    /// <param name="priority">优先级 (0-最高)</param>
    public void RunWorkerAsync(object? argument = null, int priority = 0)
    {
        if (priority < 0 || priority >= _priorityQueues)
            throw new ArgumentException($"优先级必须在 0 到 {_priorityQueues - 1} 之间", nameof(priority));

        // 启动工作线程
        if (!_isStarted)
        {
            StartThreads();
        }

        var workItem = new WorkItem
        {
            Argument = argument,
            Priority = priority,
            Id = Guid.NewGuid()
        };

        lock (_lockObject)
        {
            AddWorkItem(workItem);
            Monitor.Pulse(_lockObject);
        }
    }

    /// <summary>
    /// 暂停
    /// </summary>
    public void Pause()
    {
        lock (_lockObject)
        {
            _isPaused = true;
        }
    }

    /// <summary>
    /// 恢复
    /// </summary>
    public void Resume()
    {
        lock (_lockObject)
        {
            _isPaused = false;
            Monitor.PulseAll(_lockObject);
        }
    }

    /// <summary>
    /// 取消所有待处理的操作
    /// </summary>
    public void CancelAsync()
    {
        lock (_lockObject)
        {
            ClearWorkQueue();
            Monitor.PulseAll(_lockObject);
        }
    }

    /// <summary>
    /// 取消指定参数的操作
    /// </summary>
    public void CancelAsync(object argument)
    {
        lock (_lockObject)
        {
            _cancelledItems.Add(argument);
            Monitor.PulseAll(_lockObject);
        }
    }

    /// <summary>
    /// 停止工作器
    /// </summary>
    public void Stop()
    {
        lock (_lockObject)
        {
            _isStopping = true;
            ClearWorkQueue();
            Monitor.PulseAll(_lockObject);
        }

        // 等待所有线程结束
        foreach (var thread in _threads)
        {
            thread?.Join(1000);
        }

        _isStarted = false;
        _isStopping = false;
    }

    #endregion

    #region 私有方法

    private void CreateThreads()
    {
        _threads = new Thread[_threadCount];
        for (int i = 0; i < _threadCount; i++)
        {
            _threads[i] = new Thread(ThreadLoop)
            {
                IsBackground = _isBackground,
                Name = $"{_threadName}-{i}"
            };
        }
    }

    private void StartThreads()
    {
        for (int i = 0; i < _threadCount; i++)
        {
            _threads[i].Start();
        }
        _isStarted = true;
    }

    private void BuildWorkQueue()
    {
        if (_processingMode == ProcessingMode.Priority)
        {
            _priorityItems = new LinkedList<WorkItem>[_priorityQueues];
            for (int i = 0; i < _priorityQueues; i++)
            {
                _priorityItems[i] = new LinkedList<WorkItem>();
            }
        }
        else
        {
            _priorityItems = [];
            _items = new LinkedList<WorkItem>();
        }
    }

    private void AddWorkItem(WorkItem item)
    {
        if (_processingMode == ProcessingMode.Priority)
        {
            _priorityItems[item.Priority].AddLast(item);
        }
        else if (_processingMode == ProcessingMode.LIFO)
        {
            _items.AddFirst(item);
        }
        else // FIFO
        {
            _items.AddLast(item);
        }
    }

    private WorkItem? GetNextWorkItem()
    {
        if (_processingMode == ProcessingMode.Priority)
        {
            // 从高优先级到低优先级查找
            for (int i = 0; i < _priorityQueues; i++)
            {
                if (_priorityItems[i].Count > 0)
                {
                    var item = _priorityItems[i].First;
                    if (item != null)
                    {
                        _priorityItems[i].RemoveFirst();
                        return item.Value;
                    }
                }
            }
        }
        else if (_items.Count > 0)
        {
            var item = _items.First;
            if (item != null)
            {
                _items.RemoveFirst();
                return item.Value;
            }
        }

        return null;
    }

    private void ClearWorkQueue()
    {
        _items.Clear();
        if (_priorityItems != null)
        {
            foreach (var queue in _priorityItems)
                queue.Clear();
        }
        _cancelledItems.Clear();
    }

    private bool IsWorkQueueEmpty()
    {
        if (_items.Count > 0) return false;
        if (_priorityItems != null)
        {
            foreach (var queue in _priorityItems)
                if (queue.Count > 0) return false;
        }
        return true;
    }

    private bool IsCancelled(object? argument)
    {
        if (argument == null) return false;
        lock (_lockObject)
        {
            return _cancelledItems.Contains(argument);
        }
    }

    private void ThreadLoop()
    {
        while (!_isStopping)
        {
            WorkItem? workItem = null;

            lock (_lockObject)
            {
                // 等待工作
                while ((IsWorkQueueEmpty() || _isPaused) && !_isStopping)
                {
                    Monitor.Wait(_lockObject, 100);
                }

                if (_isStopping) break;

                workItem = GetNextWorkItem();
            }

            if (workItem != null)
            {
                ProcessWorkItem(workItem);
            }
        }
    }

    private void ProcessWorkItem(WorkItem workItem)
    {
        Exception? error = null;
        object? result = null;
        bool cancelled = false;

        try
        {
            // 检查是否已取消
            if (IsCancelled(workItem.Argument))
            {
                cancelled = true;
            }
            else
            {
                // 执行工作
                var args = new QueuedWorkerDoWorkEventArgs(
                    workItem.Argument, workItem.Priority, workItem.Id);
                
                DoWork?.Invoke(this, args);
                
                result = args.Result;
                cancelled = args.Cancel;
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            // 移除取消标记
            if (workItem.Argument != null)
            {
                lock (_lockObject)
                {
                    _cancelledItems.Remove(workItem.Argument);
                }
            }
        }

        // 触发完成事件
        var completedArgs = new QueuedWorkerCompletedEventArgs(
            result, error, cancelled, workItem.Priority);
        
        RunWorkerCompleted?.Invoke(this, completedArgs);
    }

    #endregion

    #region 工作项类

    private class WorkItem
    {
        public object? Argument { get; set; }
        public int Priority { get; set; }
        public Guid Id { get; set; }
    }

    #endregion

    #region 释放

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Stop();
        }
        base.Dispose(disposing);
    }

    #endregion
}
