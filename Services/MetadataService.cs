using ImageBrowser.Models;
using System;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;

namespace ImageBrowser.Services;

/// <summary>
/// 元数据服务
/// 参考 ImageGlass 的 IgMetadata 实现
/// </summary>
public static class MetadataService
{
    /// <summary>
    /// 加载图像元数据（优化版：单次文件访问）
    /// </summary>
    public static ImageMetadata? LoadMetadata(string? filePath, MetadataLoadOptions? options = null)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return null;

        options ??= new MetadataLoadOptions();
        var metadata = new ImageMetadata
        {
            FilePath = filePath,
            FileName = Path.GetFileName(filePath),
            FileExtension = Path.GetExtension(filePath).ToLowerInvariant()
        };

        try
        {
            // 优化：单次文件 Info 获取，避免重复访问磁盘
            FileInfo? fileInfo = null;
            if (options.LoadFileInfo)
            {
                fileInfo = new FileInfo(filePath);
                metadata.FileSize = fileInfo.Length;
                metadata.FileCreationTime = fileInfo.CreationTime;
                metadata.FileModifiedTime = fileInfo.LastWriteTime;
            }

            // 优化：合并图像信息和 EXIF 加载为单次文件打开
            if (options.LoadImageInfo || options.LoadExif)
            {
                LoadImageAndExifInfo(metadata, filePath, options);
            }

            return metadata;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载元数据失败: {filePath}, 错误: {ex.Message}");
            return metadata;
        }
    }

    /// <summary>
    /// 快速加载元数据（不解码像素）
    /// </summary>
    public static ImageMetadata? LoadMetadataFast(string? filePath)
    {
        return LoadMetadata(filePath, new MetadataLoadOptions { FastMode = true });
    }

    #region 私有方法

    /// <summary>
    /// 合并加载图像信息和 EXIF 数据（单次文件打开）
    /// </summary>
    private static void LoadImageAndExifInfo(ImageMetadata metadata, string filePath, MetadataLoadOptions options)
    {
        try
        {
            // 单次文件打开，同时读取图像信息和 EXIF
            using var stream = new FileStream(
                filePath, 
                FileMode.Open, 
                FileAccess.Read, 
                FileShare.Read,
                bufferSize: 4096,  // 小缓冲，元数据通常很小
                FileOptions.SequentialScan);  // 顺序读取优化

            var decoder = BitmapDecoder.Create(stream, 
                options.FastMode ? BitmapCreateOptions.DelayCreation : BitmapCreateOptions.None, 
                BitmapCacheOption.None);

            if (decoder.Frames.Count == 0) return;

            var frame = decoder.Frames[0];
            var bitmapMetadata = frame.Metadata as BitmapMetadata;

            // 加载图像信息
            if (options.LoadImageInfo)
            {
                metadata.Width = frame.PixelWidth;
                metadata.Height = frame.PixelHeight;
                metadata.DpiX = frame.DpiX;
                metadata.DpiY = frame.DpiY;
                metadata.Format = decoder.CodecInfo?.FriendlyName ?? "Unknown";
                metadata.FrameCount = decoder.Frames.Count;
                metadata.BitsPerPixel = frame.Format.BitsPerPixel;
                metadata.HasTransparency = frame.Format == System.Windows.Media.PixelFormats.Bgra32 ||
                                          frame.Format == System.Windows.Media.PixelFormats.Pbgra32 ||
                                          frame.Format == System.Windows.Media.PixelFormats.Prgba64;
                metadata.ColorSpace = frame.Format.ToString();
            }

            // 加载 EXIF 数据
            if (options.LoadExif && bitmapMetadata != null)
            {
                LoadExifFromMetadata(metadata, bitmapMetadata);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载图像和 EXIF 信息失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 从 BitmapMetadata 加载 EXIF 数据
    /// </summary>
    private static void LoadExifFromMetadata(ImageMetadata metadata, BitmapMetadata bitmapMetadata)
    {
        try
        {
            // 基本 EXIF 信息
            metadata.CameraMaker = bitmapMetadata.CameraManufacturer;
            metadata.CameraModel = bitmapMetadata.CameraModel;
            metadata.Software = bitmapMetadata.ApplicationName;
            metadata.Title = bitmapMetadata.Title;
            metadata.Description = bitmapMetadata.Comment;
            metadata.Copyright = bitmapMetadata.Copyright;
            metadata.Artist = GetMetaString(bitmapMetadata, "System.Author");
            metadata.Rating = ParseRating(GetMetaString(bitmapMetadata, "System.SimpleRating"));

            // 拍摄时间
            var dateTaken = GetMetaString(bitmapMetadata, "System.Photo.DateTaken");
            if (!string.IsNullOrEmpty(dateTaken) && DateTime.TryParse(dateTaken, out var dt))
                metadata.DateTaken = dt;

            // 镜头信息
            metadata.LensModel = GetMetaString(bitmapMetadata, "System.Photo.LensModel");

            // 曝光信息
            metadata.Aperture = ParseDouble(GetMetaString(bitmapMetadata, "System.Photo.FNumber"));
            metadata.MaxAperture = ParseDouble(GetMetaString(bitmapMetadata, "System.Photo.MaxAperture"));
            metadata.ExposureTime = ParseDouble(GetMetaString(bitmapMetadata, "System.Photo.ExposureTime"));
            metadata.ExposureBias = ParseDouble(GetMetaString(bitmapMetadata, "System.Photo.ExposureBias"));

            // ISO
            metadata.ISOSpeed = ParseInt(GetMetaString(bitmapMetadata, "System.Photo.ISOSpeed"));

            // 焦距
            metadata.FocalLength = ParseDouble(GetMetaString(bitmapMetadata, "System.Photo.FocalLength"));
            metadata.FocalLength35mm = ParseInt(GetMetaString(bitmapMetadata, "System.Photo.FocalLengthInFilm"));

            // 闪光灯
            metadata.FlashMode = GetMetaString(bitmapMetadata, "System.Photo.Flash");

            // 白平衡
            metadata.WhiteBalance = GetMetaString(bitmapMetadata, "System.Photo.WhiteBalance");

            // 测光模式
            metadata.MeteringMode = GetMetaString(bitmapMetadata, "System.Photo.MeteringMode");

            // 曝光程序
            metadata.ExposureProgram = GetMetaString(bitmapMetadata, "System.Photo.ExposureProgram");

            // 场景类型
            metadata.SceneType = GetMetaString(bitmapMetadata, "System.Photo.SceneType");

            // 方向
            metadata.Orientation = ParseInt(GetMetaString(bitmapMetadata, "System.Photo.Orientation"));

            // 色彩空间
            metadata.ColorSpace = GetMetaString(bitmapMetadata, "System.Image.ColorSpace");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载 EXIF 数据失败: {ex.Message}");
        }
    }

    #region 保留旧方法（用于兼容，但主流程已改用 LoadImageAndExifInfo）

    private static void LoadFileInfo(ImageMetadata metadata, string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        metadata.FileSize = fileInfo.Length;
        metadata.FileCreationTime = fileInfo.CreationTime;
        metadata.FileModifiedTime = fileInfo.LastWriteTime;
    }

    #endregion

    private static void DetectAiMetadata(ImageMetadata metadata, BitmapMetadata bitmapMetadata)
    {
        // 检测 AI 生成内容
        var software = metadata.Software?.ToLowerInvariant() ?? "";
        var comment = metadata.Description?.ToLowerInvariant() ?? "";

        var aiTools = new[] { "stable diffusion", "comfyui", "midjourney", "dall-e", "dalle", 
                              "novelai", "firefly", "diffusion", "ai generated", "generated by ai" };

        metadata.HasAiMetadata = aiTools.Any(tool => software.Contains(tool) || comment.Contains(tool));

        if (metadata.HasAiMetadata)
        {
            metadata.AiPrompt = metadata.Description;
        }
    }

    #region 辅助方法

    private static string? GetMetaString(BitmapMetadata metadata, string query)
    {
        try
        {
            if (metadata.ContainsQuery(query))
            {
                var value = metadata.GetQuery(query);
                return value?.ToString();
            }
        }
        catch { }
        return null;
    }

    private static double? ParseDouble(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (double.TryParse(value, out var result))
            return result;
        return null;
    }

    private static int? ParseInt(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (int.TryParse(value, out var result))
            return result;
        return null;
    }

    private static int? ParseRating(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (int.TryParse(value, out var result))
            return result;
        return null;
    }

    private static string? FormatMeteringMode(string? value)
    {
        return value switch
        {
            "1" => "平均",
            "2" => "中央重点",
            "3" => "点测",
            "4" => "多点",
            "5" => "模式",
            "6" => "局部",
            "255" => "其他",
            _ => value
        };
    }

    private static string? FormatFlash(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (int.TryParse(value, out var flash))
        {
            return (flash & 1) == 1 ? "已闪光" : "未闪光";
        }
        return value;
    }

    private static string? FormatWhiteBalance(string? value)
    {
        return value switch
        {
            "0" => "自动",
            "1" => "日光",
            "2" => "荧光灯",
            "3" => "钨丝灯",
            "4" => "闪光灯",
            "9" => "晴天",
            "10" => "阴天",
            _ => value
        };
    }

    private static string? FormatExposureProgram(string? value)
    {
        return value switch
        {
            "0" => "未定义",
            "1" => "手动",
            "2" => "标准程序",
            "3" => "光圈优先",
            "4" => "快门优先",
            "5" => "创意程序",
            "6" => "运动程序",
            "7" => "人像模式",
            "8" => "风景模式",
            _ => value
        };
    }

    #endregion

    #endregion
}
