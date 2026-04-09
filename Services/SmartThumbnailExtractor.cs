using ImageMagick;
using System.IO;
using System.Windows.Media.Imaging;

namespace ImageBrowser.Services;

/// <summary>
/// 智能缩略图提取器
/// 优先使用内嵌缩略图，大幅提升 RAW 文件加载速度
/// 参考 ImageGlass 的三级缩略图策略
/// </summary>
public static class SmartThumbnailExtractor
{
    /// <summary>
    /// 获取智能缩略图
    /// 优先级：1. RAW内嵌缩略图 → 2. EXIF缩略图 → 3. 解码完整图片
    /// </summary>
    public static async Task<BitmapSource?> GetThumbnailAsync(string filePath, int width, int height, bool useEmbeddedOnly = false)
    {
        if (!File.Exists(filePath)) return null;

        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        // 1. 尝试 RAW 内嵌缩略图（最快）
        if (IsRawFile(ext))
        {
            var rawThumb = await GetRawEmbeddedThumbnailAsync(filePath);
            if (rawThumb != null)
            {
                var scaled = ScaleThumbnail(rawThumb, width, height);
                rawThumb.Dispose();
                return scaled;
            }
        }

        // 2. 尝试 EXIF 缩略图（较快）
        var exifThumb = await GetExifThumbnailAsync(filePath);
        if (exifThumb != null)
        {
            var scaled = ScaleThumbnail(exifThumb, width, height);
            exifThumb.Dispose();
            return scaled;
        }

        // 3. 如果只需要内嵌缩略图，到此为止
        if (useEmbeddedOnly) return null;

        // 4. 解码完整图片并缩放（最慢）
        return await DecodeAndScaleAsync(filePath, width, height);
    }

    /// <summary>
    /// 快速检查是否有内嵌缩略图（不加载图片）
    /// </summary>
    public static async Task<bool> HasEmbeddedThumbnailAsync(string filePath)
    {
        if (!File.Exists(filePath)) return false;

        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        // RAW 文件检查
        if (IsRawFile(ext))
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var img = new MagickImage();
                    img.Ping(filePath);
                    var profile = img.GetProfile("dng:thumbnail");
                    return profile != null;
                }
                catch { return false; }
            });
        }

        // 普通图片检查 EXIF
        return await Task.Run(() =>
        {
            try
            {
                using var img = new MagickImage();
                img.Ping(filePath);
                var exif = img.GetExifProfile();
                return exif?.CreateThumbnail() != null;
            }
            catch { return false; }
        });
    }

    /// <summary>
    /// 获取 EXIF 缩略图
    /// </summary>
    private static async Task<IMagickImage<ushort>?> GetExifThumbnailAsync(string filePath)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var img = new MagickImage();
                // Ping 只读取元数据，不解码像素
                img.Ping(filePath);

                var exif = img.GetExifProfile();
                if (exif == null) return null;

                using var thumb = exif.CreateThumbnail();
                if (thumb == null) return null;

                // 自动调整方向
                thumb.AutoOrient();
                return thumb.Clone();
            }
            catch { return null; }
        });
    }

    /// <summary>
    /// 获取 RAW 内嵌缩略图
    /// </summary>
    private static async Task<IMagickImage<ushort>?> GetRawEmbeddedThumbnailAsync(string filePath)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var img = new MagickImage();
                img.Ping(filePath);

                // 尝试获取 DNG 缩略图
                var dngProfile = img.GetProfile("dng:thumbnail");
                if (dngProfile != null)
                {
                    var bytes = dngProfile.ToByteArray();
                    if (bytes != null && bytes.Length > 0)
                    {
                        using var thumb = new MagickImage(bytes);
                        thumb.AutoOrient();
                        return thumb.Clone();
                    }
                }

                // 尝试获取 RAW 预览图
                var rawProfile = img.GetProfile("raw:preview");
                if (rawProfile != null)
                {
                    var bytes = rawProfile.ToByteArray();
                    if (bytes != null && bytes.Length > 0)
                    {
                        using var thumb = new MagickImage(bytes);
                        thumb.AutoOrient();
                        return thumb.Clone();
                    }
                }

                // 尝试其他可能的配置文件
                var profiles = img.ProfileNames;
                foreach (var profileName in profiles)
                {
                    if (profileName.Contains("thumb", StringComparison.OrdinalIgnoreCase) ||
                        profileName.Contains("preview", StringComparison.OrdinalIgnoreCase))
                    {
                        var profile = img.GetProfile(profileName);
                        if (profile != null)
                        {
                            var bytes = profile.ToByteArray();
                            if (bytes != null && bytes.Length > 1000) // 至少 1KB
                            {
                                try
                                {
                                    using var thumb = new MagickImage(bytes);
                                    thumb.AutoOrient();
                                    return thumb.Clone();
                                }
                                catch { /* 忽略解析失败的配置文件 */ }
                            }
                        }
                    }
                }

                return null;
            }
            catch { return null; }
        });
    }

    /// <summary>
    /// 解码完整图片并缩放
    /// </summary>
    private static async Task<BitmapSource?> DecodeAndScaleAsync(string filePath, int width, int height)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var img = new MagickImage(filePath);
                img.AutoOrient();

                // 计算缩放尺寸
                var (newWidth, newHeight) = CalculateScaleSize((int)img.Width, (int)img.Height, width, height);

                // 缩放（使用高质量采样）
                img.Resize((uint)newWidth, (uint)newHeight);
                img.Quality = 90;

                return ToBitmapSource(img);
            }
            catch { return null; }
        });
    }

    /// <summary>
    /// 缩放缩略图到目标尺寸
    /// </summary>
    private static BitmapSource? ScaleThumbnail(IMagickImage<ushort> image, int targetWidth, int targetHeight)
    {
        try
        {
            // 如果缩略图已经比目标尺寸小，直接返回
            if (image.Width <= (uint)targetWidth && image.Height <= (uint)targetHeight)
            {
                return ToBitmapSource(image);
            }

            // 计算缩放尺寸
            var (newWidth, newHeight) = CalculateScaleSize((int)image.Width, (int)image.Height, targetWidth, targetHeight);

            // 缩放
            image.Resize((uint)newWidth, (uint)newHeight);
            image.Quality = 90;

            return ToBitmapSource(image);
        }
        catch { return null; }
    }

    /// <summary>
    /// 计算缩放尺寸（保持宽高比）
    /// </summary>
    private static (int width, int height) CalculateScaleSize(int sourceWidth, int sourceHeight, int maxWidth, int maxHeight)
    {
        var ratioX = (double)maxWidth / sourceWidth;
        var ratioY = (double)maxHeight / sourceHeight;
        var ratio = Math.Min(ratioX, ratioY);

        var newWidth = (int)(sourceWidth * ratio);
        var newHeight = (int)(sourceHeight * ratio);

        return (newWidth, newHeight);
    }

    /// <summary>
    /// 判断是否为 RAW 文件
    /// </summary>
    private static bool IsRawFile(string ext)
    {
        var rawExtensions = new[] { ".cr2", ".cr3", ".nef", ".arw", ".dng", ".orf", ".raf", ".pef", ".raw", ".rw2" };
        return rawExtensions.Contains(ext);
    }

    /// <summary>
    /// 将 MagickImage 转换为 BitmapSource
    /// </summary>
    private static BitmapSource ToBitmapSource(IMagickImage<ushort> image)
    {
        var width = (int)image.Width;
        var height = (int)image.Height;
        var pixelData = image.GetPixels().ToByteArray(PixelMapping.RGBA);

        if (pixelData == null)
        {
            pixelData = image.GetPixels().ToByteArray(PixelMapping.BGRA);
        }

        if (pixelData == null)
        {
            throw new InvalidOperationException("无法获取图像像素数据");
        }

        var stride = width * 4;
        var bitmap = BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null, pixelData, stride);
        bitmap.Freeze();
        return bitmap;
    }
}
