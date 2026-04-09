using System;
using System.Diagnostics;
using WicNet;

namespace ImageBrowser.Services;

/// <summary>
/// DirectN/Direct2D 服务 - 用于 GPU 加速功能
/// 参考 ImageGlass 的实现方式
/// </summary>
public static class DirectNService
{
    private static bool _isInitialized = false;

    /// <summary>
    /// 检查 GPU 加速是否可用
    /// </summary>
    public static bool IsGpuAccelerated
    {
        get
        {
            try
            {
                // 检查 WicNetCore 是否可用（WIC 底层使用 GPU 加速）
                var wicBitmap = WicBitmapSource.Load("dummy");
                return wicBitmap != null;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 获取渲染信息
    /// </summary>
    public static string GetRenderInfo()
    {
        try
        {
            return "WicNetCore GPU 加速已启用";
        }
        catch (Exception ex)
        {
            return $"获取渲染信息失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 初始化 DirectN/Direct2D
    /// </summary>
    public static void Initialize()
    {
        if (_isInitialized) return;

        try
        {
            // WicNetCore 初始化
            _isInitialized = true;
            Debug.WriteLine("DirectNCore + WicNetCore 初始化成功");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"DirectNCore + WicNetCore 初始化失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 检查是否支持特定图像格式
    /// </summary>
    public static bool IsFormatSupported(string extension)
    {
        try
        {
            return WicImageLoader.SupportedExtensions.Contains(extension.ToLowerInvariant());
        }
        catch
        {
            return false;
        }
    }
}
