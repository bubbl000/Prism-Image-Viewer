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
    /// 加载图像元数据
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
            // 1. 加载文件信息
            if (options.LoadFileInfo)
            {
                LoadFileInfo(metadata, filePath);
            }

            // 2. 加载图像信息
            if (options.LoadImageInfo)
            {
                LoadImageInfo(metadata, filePath, options.FastMode);
            }

            // 3. 加载 EXIF 数据
            if (options.LoadExif)
            {
                LoadExifData(metadata, filePath);
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

    private static void LoadFileInfo(ImageMetadata metadata, string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        metadata.FileSize = fileInfo.Length;
        metadata.FileCreationTime = fileInfo.CreationTime;
        metadata.FileModifiedTime = fileInfo.LastWriteTime;
    }

    private static void LoadImageInfo(ImageMetadata metadata, string filePath, bool fastMode)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            // 尝试使用 WPF 的 BitmapDecoder
            var decoder = BitmapDecoder.Create(stream, 
                fastMode ? BitmapCreateOptions.DelayCreation : BitmapCreateOptions.None, 
                BitmapCacheOption.None);

            if (decoder.Frames.Count > 0)
            {
                var frame = decoder.Frames[0];
                metadata.Width = frame.PixelWidth;
                metadata.Height = frame.PixelHeight;
                metadata.DpiX = frame.DpiX;
                metadata.DpiY = frame.DpiY;
                metadata.Format = decoder.CodecInfo?.FriendlyName ?? "Unknown";
                metadata.FrameCount = decoder.Frames.Count;

                // 位深度
                metadata.BitsPerPixel = frame.Format.BitsPerPixel;

                // 透明通道检测
                metadata.HasTransparency = frame.Format == System.Windows.Media.PixelFormats.Bgra32 ||
                                          frame.Format == System.Windows.Media.PixelFormats.Pbgra32 ||
                                          frame.Format == System.Windows.Media.PixelFormats.Prgba64;

                // 颜色空间
                metadata.ColorSpace = frame.Format.ToString();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载图像信息失败: {ex.Message}");
        }
    }

    private static void LoadExifData(ImageMetadata metadata, string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            var bitmapMetadata = frame.Metadata as BitmapMetadata;

            if (bitmapMetadata == null) return;

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
            metadata.ISOSpeed = ParseInt(GetMetaString(bitmapMetadata, "System.Photo.ISOSpeed"));
            metadata.FocalLength = ParseDouble(GetMetaString(bitmapMetadata, "System.Photo.FocalLength"));
            metadata.FocalLength35mm = ParseInt(GetMetaString(bitmapMetadata, "System.Photo.FocalLengthInFilm"));

            // 测光、闪光灯、白平衡
            metadata.MeteringMode = FormatMeteringMode(GetMetaString(bitmapMetadata, "System.Photo.MeteringMode"));
            metadata.FlashMode = FormatFlash(GetMetaString(bitmapMetadata, "System.Photo.Flash"));
            metadata.WhiteBalance = FormatWhiteBalance(GetMetaString(bitmapMetadata, "System.Photo.WhiteBalance"));

            // 其他
            metadata.Brightness = ParseDouble(GetMetaString(bitmapMetadata, "System.Photo.Brightness"));
            metadata.ExposureProgram = FormatExposureProgram(GetMetaString(bitmapMetadata, "System.Photo.ProgramMode"));
            metadata.Orientation = ParseInt(GetMetaString(bitmapMetadata, "System.Photo.Orientation"));

            // GPS 信息
            metadata.GPSLatitude = GetMetaString(bitmapMetadata, "System.GPS.Latitude");
            metadata.GPSLongitude = GetMetaString(bitmapMetadata, "System.GPS.Longitude");
            metadata.GPSAltitude = ParseDouble(GetMetaString(bitmapMetadata, "System.GPS.Altitude"));

            // AI 信息检测
            DetectAiMetadata(metadata, bitmapMetadata);

            // 颜色配置文件
            metadata.ColorProfile = GetMetaString(bitmapMetadata, "System.Image.ColorSpace");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载 EXIF 失败: {ex.Message}");
        }
    }

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
