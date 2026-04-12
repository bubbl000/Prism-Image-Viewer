using ImageMagick;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace ImageBrowser.Services;

/// <summary>
/// 智能缩略图提取器
/// 优先使用内嵌缩略图，大幅提升 RAW 文件加载速度
/// 参考 ImageGlass 的三级缩略图策略
/// 新增：磁盘缓存避免重复生成
/// </summary>
public static class SmartThumbnailExtractor
{
    // 缩略图磁盘缓存目录
    private static readonly string ThumbnailCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PrismImageViewer", "ThumbnailCache");
    
    // 像素缓冲区池（避免LOH分配）
    private static readonly ArrayPool<byte> PixelBufferPool = ArrayPool<byte>.Create(128 * 1024, 100); // 128KB缓冲区
    
    // 缓存有效期（7天）
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromDays(7);
    
    // 最大缩略图尺寸（128x128 = 64KB，避免进入LOH）
    private const int MaxThumbnailSize = 128;

    static SmartThumbnailExtractor()
    {
        // 确保缓存目录存在
        try
        {
            Directory.CreateDirectory(ThumbnailCacheDir);
        }
        catch { }
    }
    /// <summary>
    /// 生成缓存键（基于文件路径和修改时间）
    /// </summary>
    private static string GetCacheKey(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        var key = $"{filePath}_{fileInfo.LastWriteTimeUtc.Ticks}_{fileInfo.Length}";
        
        // 使用 SHA256 生成固定长度的缓存文件名
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash) + ".jpg";
    }

    /// <summary>
    /// 尝试从磁盘缓存加载缩略图
    /// </summary>
    private static async Task<BitmapSource?> TryLoadFromDiskCacheAsync(string filePath, int width, int height)
    {
        try
        {
            var cacheKey = GetCacheKey(filePath);
            var cachePath = Path.Combine(ThumbnailCacheDir, cacheKey);
            
            if (!File.Exists(cachePath))
                return null;
            
            // 检查缓存是否过期
            var cacheInfo = new FileInfo(cachePath);
            if (DateTime.Now - cacheInfo.LastWriteTime > CacheExpiration)
            {
                File.Delete(cachePath);
                return null;
            }
            
            // 从磁盘加载缩略图
            return await Task.Run(() =>
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(cachePath);
                    bitmap.DecodePixelWidth = width;
                    bitmap.DecodePixelHeight = height;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap as BitmapSource;
                }
                catch { return null; }
            });
        }
        catch { return null; }
    }

    /// <summary>
    /// 保存缩略图到磁盘缓存
    /// </summary>
    private static async Task SaveToDiskCacheAsync(string filePath, BitmapSource bitmap)
    {
        try
        {
            var cacheKey = GetCacheKey(filePath);
            var cachePath = Path.Combine(ThumbnailCacheDir, cacheKey);
            
            await Task.Run(() =>
            {
                try
                {
                    // 使用 JPEG 编码保存（体积小，加载快）
                    var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    
                    using var fs = new FileStream(cachePath, FileMode.Create, FileAccess.Write, FileShare.None);
                    encoder.Save(fs);
                }
                catch { }
            });
        }
        catch { }
    }

    /// <summary>
    /// 获取智能缩略图
    /// 优先级：1. 磁盘缓存 → 2. RAW内嵌缩略图 → 3. EXIF缩略图 → 4. 解码完整图片
    /// </summary>
    public static async Task<BitmapSource?> GetThumbnailAsync(string filePath, int width, int height, bool useEmbeddedOnly = false)
    {
        if (!File.Exists(filePath)) return null;

        // 限制最大尺寸以避免LOH（128x128 = 64KB）
        width = Math.Min(width, MaxThumbnailSize);
        height = Math.Min(height, MaxThumbnailSize);

        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        // 0. 尝试磁盘缓存（最快）
        var cachedThumb = await TryLoadFromDiskCacheAsync(filePath, width, height);
        if (cachedThumb != null)
            return cachedThumb;

        // 1. 尝试 RAW 内嵌缩略图（很快）
        if (IsRawFile(ext))
        {
            var rawThumb = await GetRawEmbeddedThumbnailAsync(filePath);
            if (rawThumb != null)
            {
                var scaled = ScaleThumbnail(rawThumb, width, height);
                rawThumb.Dispose();
                
                // 保存到磁盘缓存
                if (scaled != null)
                    await SaveToDiskCacheAsync(filePath, scaled);
                
                return scaled;
            }
        }

        // 2. 尝试 EXIF 缩略图（较快）
        var exifThumb = await GetExifThumbnailAsync(filePath);
        if (exifThumb != null)
        {
            var scaled = ScaleThumbnail(exifThumb, width, height);
            exifThumb.Dispose();
            
            // 保存到磁盘缓存
            if (scaled != null)
                await SaveToDiskCacheAsync(filePath, scaled);
            
            return scaled;
        }

        // 3. 如果只需要内嵌缩略图，到此为止
        if (useEmbeddedOnly) return null;

        // 4. 解码完整图片并缩放（最慢）
        var decoded = await DecodeAndScaleAsync(filePath, width, height);
        
        // 保存到磁盘缓存
        if (decoded != null)
            await SaveToDiskCacheAsync(filePath, decoded);
        
        return decoded;
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
    /// 将 MagickImage 转换为 BitmapSource（优化版，使用 ArrayPool 避免 LOH）
    /// </summary>
    private static BitmapSource ToBitmapSource(IMagickImage<ushort> image)
    {
        var width = (int)image.Width;
        var height = (int)image.Height;
        var stride = width * 4;
        var requiredSize = stride * height;
        
        // 对于小图像（< 128x128），使用 ArrayPool 避免 LOH
        if (width <= MaxThumbnailSize && height <= MaxThumbnailSize)
        {
            byte[] rentedBuffer = PixelBufferPool.Rent(requiredSize);
            try
            {
                // 获取像素数据
                var pixelData = image.GetPixels().ToByteArray(PixelMapping.RGBA)
                    ?? image.GetPixels().ToByteArray(PixelMapping.BGRA);

                if (pixelData == null)
                    throw new InvalidOperationException("无法获取图像像素数据");

                // 复制到池缓冲区
                Buffer.BlockCopy(pixelData, 0, rentedBuffer, 0, pixelData.Length);

                // 创建 BitmapSource
                var bitmap = BitmapSource.Create(width, height, 96, 96, 
                    System.Windows.Media.PixelFormats.Pbgra32, null, rentedBuffer, stride);
                bitmap.Freeze();
                return bitmap;
            }
            finally
            {
                // 归还缓冲区
                PixelBufferPool.Return(rentedBuffer, clearArray: false);
            }
        }
        else
        {
            // 大图像使用传统方式（会进入 LOH，但大图像不频繁）
            var pixelData = image.GetPixels().ToByteArray(PixelMapping.RGBA)
                ?? image.GetPixels().ToByteArray(PixelMapping.BGRA);

            if (pixelData == null)
                throw new InvalidOperationException("无法获取图像像素数据");

            var bitmap = BitmapSource.Create(width, height, 96, 96, 
                System.Windows.Media.PixelFormats.Pbgra32, null, pixelData, stride);
            bitmap.Freeze();
            return bitmap;
        }
    }
    
    /// <summary>
    /// 清理过期的缓存文件
    /// </summary>
    public static void CleanupExpiredCache()
    {
        try
        {
            if (!Directory.Exists(ThumbnailCacheDir))
                return;
            
            var files = Directory.GetFiles(ThumbnailCacheDir, "*.jpg");
            var now = DateTime.Now;
            int deletedCount = 0;
            
            foreach (var file in files)
            {
                try
                {
                    var fileInfo = new FileInfo(file);
                    if (now - fileInfo.LastWriteTime > CacheExpiration)
                    {
                        File.Delete(file);
                        deletedCount++;
                    }
                }
                catch { }
            }
            
            if (deletedCount > 0)
            {
                System.Diagnostics.Debug.WriteLine($"[SmartThumbnailExtractor] 清理了 {deletedCount} 个过期缓存文件");
            }
        }
        catch { }
    }
}
