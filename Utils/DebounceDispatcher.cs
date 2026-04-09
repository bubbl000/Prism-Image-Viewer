using System;
using System.Threading;
using System.Threading.Tasks;

namespace ImageBrowser.Utils;

/// <summary>
/// 防抖分发器
/// 从 ImageGlass 借鉴
/// </summary>
public class DebounceDispatcher
{
    private CancellationTokenSource? _cts;
    private readonly object _lock = new();
    
    /// <summary>
    /// 防抖执行
    /// </summary>
    public async Task DebounceAsync(Func<Task> action, int delayMs)
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
        }
        
        try
        {
            await Task.Delay(delayMs, _cts.Token);
            await action();
        }
        catch (TaskCanceledException)
        {
            // 忽略取消异常
        }
    }
    
    /// <summary>
    /// 防抖执行（同步版本）
    /// </summary>
    public async Task DebounceAsync(Action action, int delayMs)
    {
        await DebounceAsync(() =>
        {
            action();
            return Task.CompletedTask;
        }, delayMs);
    }
    
    /// <summary>
    /// 取消待执行的防抖操作
    /// </summary>
    public void Cancel()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _cts = null;
        }
    }
}
