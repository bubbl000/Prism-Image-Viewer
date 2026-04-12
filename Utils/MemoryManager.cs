using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace ImageBrowser.Utils;

/// <summary>
/// 内存管理工具类
/// 用于优化大图片加载后的内存释放
/// </summary>
public static class MemoryManager
{
    /// <summary>
    /// 大图片阈值（10MB）
    /// </summary>
    private const long LargeImageThreshold = 10 * 1024 * 1024;

    /// <summary>
    /// 加载大图片后建议进行垃圾回收的阈值
    /// </summary>
    private const long GcSuggestThreshold = 50 * 1024 * 1024;

    /// <summary>
    /// 上次 GC 时间
    /// </summary>
    private static DateTime _lastGcTime = DateTime.MinValue;

    /// <summary>
    /// GC 最小间隔（避免频繁 GC）
    /// </summary>
    private static readonly TimeSpan MinGcInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Windows API 用于获取物理内存信息
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>
    /// 释放 BitmapSource 资源
    /// </summary>
    public static void ReleaseBitmapSource(BitmapSource? bitmap)
    {
        if (bitmap == null) return;

        try
        {
            // 如果 BitmapSource 没有被冻结，尝试清理
            if (!bitmap.IsFrozen)
            {
                // 对于可写的 BitmapSource，可以尝试清理
                if (bitmap is System.Windows.Media.Imaging.WriteableBitmap writeableBmp)
                {
                    // WriteableBitmap 会在不再被引用时自动释放
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"释放 BitmapSource 失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 在加载大图片后尝试释放内存
    /// </summary>
    /// <param name="imageSize">图片文件大小</param>
    /// <param name="forceGc">是否强制 GC</param>
    public static void TryReleaseMemoryAfterLoad(long imageSize, bool forceGc = false)
    {
        // 只有大图片才触发内存释放
        if (imageSize < LargeImageThreshold && !forceGc)
            return;

        // 检查 GC 间隔
        var now = DateTime.Now;
        if (!forceGc && now - _lastGcTime < MinGcInterval)
            return;

        // 对于超大图片，建议进行垃圾回收
        if (imageSize > GcSuggestThreshold || forceGc)
        {
            TriggerGarbageCollection();
            _lastGcTime = now;
        }
    }

    /// <summary>
    /// 触发垃圾回收
    /// </summary>
    public static void TriggerGarbageCollection()
    {
        try
        {
            // 第一次收集：回收大部分对象
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, false);
            
            // 等待终结器执行完成（这会清理终结器队列中的对象）
            GC.WaitForPendingFinalizers();
            
            // 注意：不需要第二次 Collect，WaitForPendingFinalizers 已经清理了终结器队列
            // 第二次 Collect 在 .NET 中通常是冗余的

            Debug.WriteLine($"[MemoryManager] GC 已触发，当前内存: {GetMemoryUsageMB():F1} MB");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MemoryManager] GC 触发失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取当前内存使用量（MB）
    /// </summary>
    public static double GetMemoryUsageMB()
    {
        return GC.GetTotalMemory(false) / (1024.0 * 1024.0);
    }

    /// <summary>
    /// 获取内存使用信息字符串
    /// </summary>
    public static string GetMemoryInfo()
    {
        var process = Process.GetCurrentProcess();
        var workingSetMB = process.WorkingSet64 / (1024.0 * 1024.0);
        var managedMB = GetMemoryUsageMB();
        
        return $"工作集: {workingSetMB:F1} MB, 托管堆: {managedMB:F1} MB";
    }

    /// <summary>
    /// 获取系统物理内存信息（使用 Windows API）
    /// </summary>
    public static (ulong TotalPhysical, ulong AvailablePhysical) GetPhysicalMemoryInfo()
    {
        try
        {
            var memStatus = new MEMORYSTATUSEX();
            memStatus.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            
            if (GlobalMemoryStatusEx(ref memStatus))
            {
                return (memStatus.ullTotalPhys, memStatus.ullAvailPhys);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MemoryManager] 获取物理内存信息失败: {ex.Message}");
        }
        
        // 回退到 GC 方法
        var gcInfo = GC.GetGCMemoryInfo();
        return ((ulong)gcInfo.TotalAvailableMemoryBytes, (ulong)gcInfo.TotalAvailableMemoryBytes);
    }

    /// <summary>
    /// 获取格式化的内存信息字符串（包含物理内存）
    /// </summary>
    public static string GetDetailedMemoryInfo()
    {
        var process = Process.GetCurrentProcess();
        var workingSetMB = process.WorkingSet64 / (1024.0 * 1024.0);
        var managedMB = GetMemoryUsageMB();
        var (totalPhys, availPhys) = GetPhysicalMemoryInfo();
        
        return $"工作集: {workingSetMB:F1} MB, 托管堆: {managedMB:F1} MB, " +
               $"物理内存: {totalPhys / (1024.0 * 1024.0):F0} MB (可用: {availPhys / (1024.0 * 1024.0):F0} MB)";
    }

    /// <summary>
    /// 设置大对象堆压缩模式
    /// </summary>
    public static void ConfigureLargeObjectHeap()
    {
        // 在 .NET 5+ 中，LOH 默认就是压缩的
        // 这个设置确保大对象堆会被压缩，减少内存碎片
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
    }
}
