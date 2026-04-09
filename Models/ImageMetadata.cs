using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;

namespace ImageBrowser.Models;

/// <summary>
/// 图像元数据
/// 参考 ImageGlass 的 IgMetadata 实现
/// </summary>
public class ImageMetadata
{
    #region 文件信息

    /// <summary>文件路径</summary>
    public string? FilePath { get; set; }

    /// <summary>文件名</summary>
    public string? FileName { get; set; }

    /// <summary>文件大小（字节）</summary>
    public long FileSize { get; set; }

    /// <summary>文件创建时间</summary>
    public DateTime FileCreationTime { get; set; }

    /// <summary>文件修改时间</summary>
    public DateTime FileModifiedTime { get; set; }

    /// <summary>文件扩展名</summary>
    public string? FileExtension { get; set; }

    #endregion

    #region 图像信息

    /// <summary>图像宽度</summary>
    public int Width { get; set; }

    /// <summary>图像高度</summary>
    public int Height { get; set; }

    /// <summary>水平 DPI</summary>
    public double DpiX { get; set; }

    /// <summary>垂直 DPI</summary>
    public double DpiY { get; set; }

    /// <summary>位深度</summary>
    public int BitsPerPixel { get; set; }

    /// <summary>图像格式</summary>
    public string? Format { get; set; }

    /// <summary>动画帧数（对于 GIF/WebP 等）</summary>
    public int FrameCount { get; set; } = 1;

    /// <summary>当前帧索引</summary>
    public int FrameIndex { get; set; } = 0;

    /// <summary>颜色空间</summary>
    public string? ColorSpace { get; set; }

    /// <summary>是否透明</summary>
    public bool HasTransparency { get; set; }

    #endregion

    #region EXIF 信息

    /// <summary>拍摄时间</summary>
    public DateTime? DateTaken { get; set; }

    /// <summary>相机制造商</summary>
    public string? CameraMaker { get; set; }

    /// <summary>相机型号</summary>
    public string? CameraModel { get; set; }

    /// <summary>镜头型号</summary>
    public string? LensModel { get; set; }

    /// <summary>光圈值 (FNumber)</summary>
    public double? Aperture { get; set; }

    /// <summary>最大光圈</summary>
    public double? MaxAperture { get; set; }

    /// <summary>曝光时间（秒）</summary>
    public double? ExposureTime { get; set; }

    /// <summary>曝光补偿 (EV)</summary>
    public double? ExposureBias { get; set; }

    /// <summary>ISO 感光度</summary>
    public int? ISOSpeed { get; set; }

    /// <summary>焦距 (mm)</summary>
    public double? FocalLength { get; set; }

    /// <summary>35mm 等效焦距</summary>
    public int? FocalLength35mm { get; set; }

    /// <summary>测光模式</summary>
    public string? MeteringMode { get; set; }

    /// <summary>闪光灯模式</summary>
    public string? FlashMode { get; set; }

    /// <summary>白平衡</summary>
    public string? WhiteBalance { get; set; }

    /// <summary>亮度值</summary>
    public double? Brightness { get; set; }

    /// <summary>曝光程序</summary>
    public string? ExposureProgram { get; set; }

    /// <summary>场景类型</summary>
    public string? SceneType { get; set; }

    /// <summary>对比度</summary>
    public string? Contrast { get; set; }

    /// <summary>饱和度</summary>
    public string? Saturation { get; set; }

    /// <summary>锐度</summary>
    public string? Sharpness { get; set; }

    /// <summary>方向/旋转</summary>
    public int? Orientation { get; set; }

    /// <summary>GPS 纬度</summary>
    public string? GPSLatitude { get; set; }

    /// <summary>GPS 经度</summary>
    public string? GPSLongitude { get; set; }

    /// <summary>GPS 海拔</summary>
    public double? GPSAltitude { get; set; }

    #endregion

    #region 其他元数据

    /// <summary>软件/应用程序</summary>
    public string? Software { get; set; }

    /// <summary>作者/艺术家</summary>
    public string? Artist { get; set; }

    /// <summary>版权信息</summary>
    public string? Copyright { get; set; }

    /// <summary>标题</summary>
    public string? Title { get; set; }

    /// <summary>描述/注释</summary>
    public string? Description { get; set; }

    /// <summary>关键词</summary>
    public List<string> Keywords { get; set; } = new();

    /// <summary>评级（1-5 星）</summary>
    public int? Rating { get; set; }

    /// <summary>颜色配置文件</summary>
    public string? ColorProfile { get; set; }

    /// <summary>ICC 配置文件名称</summary>
    public string? IccProfileName { get; set; }

    /// <summary>是否包含 AI 生成信息</summary>
    public bool HasAiMetadata { get; set; }

    /// <summary>AI 提示词</summary>
    public string? AiPrompt { get; set; }

    /// <summary>原始 EXIF 数据字典</summary>
    public Dictionary<string, object> RawExifData { get; set; } = new();

    #endregion

    #region 辅助方法

    /// <summary>
    /// 获取格式化的文件大小
    /// </summary>
    public string GetFormattedFileSize()
    {
        return FormatFileSize(FileSize);
    }

    /// <summary>
    /// 获取格式化的尺寸
    /// </summary>
    public string GetFormattedDimensions()
    {
        return $"{Width} × {Height}";
    }

    /// <summary>
    /// 获取格式化的 DPI
    /// </summary>
    public string GetFormattedDpi()
    {
        if (DpiX == DpiY)
            return $"{DpiX:F0} DPI";
        return $"{DpiX:F0} × {DpiY:F0} DPI";
    }

    /// <summary>
    /// 获取格式化的曝光时间
    /// </summary>
    public string? GetFormattedExposureTime()
    {
        if (!ExposureTime.HasValue) return null;
        var et = ExposureTime.Value;
        if (et >= 1)
            return $"{et:F1} 秒";
        return $"1/{Math.Round(1 / et)} 秒";
    }

    /// <summary>
    /// 获取格式化的光圈值
    /// </summary>
    public string? GetFormattedAperture()
    {
        return Aperture.HasValue ? $"f/{Aperture.Value:F1}" : null;
    }

    /// <summary>
    /// 获取格式化的焦距
    /// </summary>
    public string? GetFormattedFocalLength()
    {
        if (!FocalLength.HasValue) return null;
        if (FocalLength35mm.HasValue)
            return $"{FocalLength.Value:F0} mm ({FocalLength35mm.Value} mm 等效)";
        return $"{FocalLength.Value:F0} mm";
    }

    /// <summary>
    /// 获取格式化的曝光补偿
    /// </summary>
    public string? GetFormattedExposureBias()
    {
        if (!ExposureBias.HasValue) return null;
        var eb = ExposureBias.Value;
        return eb == 0 ? "0 EV" : $"{eb:+0.##;-0.##} EV";
    }

    private static string FormatFileSize(long bytes)
    {
        const long KB = 1024;
        const long MB = KB * 1024;
        const long GB = MB * 1024;

        return bytes switch
        {
            >= GB => $"{bytes / (double)GB:F2} GB",
            >= MB => $"{bytes / (double)MB:F2} MB",
            >= KB => $"{bytes / (double)KB:F2} KB",
            _ => $"{bytes} B"
        };
    }

    #endregion
}

/// <summary>
/// 元数据加载选项
/// </summary>
public class MetadataLoadOptions
{
    /// <summary>加载文件信息</summary>
    public bool LoadFileInfo { get; set; } = true;

    /// <summary>加载图像信息</summary>
    public bool LoadImageInfo { get; set; } = true;

    /// <summary>加载 EXIF 数据</summary>
    public bool LoadExif { get; set; } = true;

    /// <summary>加载颜色配置文件</summary>
    public bool LoadColorProfile { get; set; } = true;

    /// <summary>使用快速模式（不解码像素）</summary>
    public bool FastMode { get; set; } = true;
}
