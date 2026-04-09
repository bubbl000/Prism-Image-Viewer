using ImageBrowser.Models;
using ImageMagick;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace ImageBrowser.Services;

/// <summary>
/// 图片加载服务
/// 融合 ImageGlass 的智能加载策略
/// </summary>
public class ImageLoader
{
    // 支持的格式
    public static readonly string[] SupportedExtensions = new[]
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp",
        ".psd", ".psb", ".raw", ".cr2", ".cr3", ".nef", ".orf", ".raf",
        ".arw", ".dng", ".rw2", ".pef", ".sr2", ".x3f", ".kdc", ".mos"
    };

    /// <summary>
    /// 加载图片元数据（不解码像素）
    /// </summary>
    public static async Task<ImageItem?> LoadMetadataAsync(string filePath)
    {
        if (!File.Exists(filePath)) return null;

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (!Array.Exists(SupportedExtensions, e => e == ext))
            return null;

        try
        {
            var fi = new FileInfo(filePath);
            var item = new ImageItem
            {
                FilePath = filePath,
                FileSize = fi.Length,
                LastWriteTime = fi.LastWriteTime
            };

            // 使用 Magick.NET Ping 快速读取元数据（不解码）
            await Task.Run(() =>
            {
                var settings = new MagickReadSettings
                {
                    BackgroundColor = MagickColors.Transparent
                };

                using var image = new MagickImage();
                image.Ping(filePath, settings);

                item.Width = (int)image.Width;
                item.Height = (int)image.Height;

                // 获取帧数
                try
                {
                    using var collection = new MagickImageCollection(filePath, settings);
                    item.FrameCount = collection.Count;
                }
                catch
                {
                    item.FrameCount = 1;
                }

                // 自动旋转
                image.AutoOrient();
                if (image.Width != (uint)item.Width || image.Height != (uint)item.Height)
                {
                    item.Width = (int)image.Width;
                    item.Height = (int)image.Height;
                }
            });

            return item;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载元数据失败: {filePath}, {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 加载缩略图（优先使用内嵌缩略图）
    /// 从 ImageGlass 借鉴的智能策略
    /// </summary>
    public static async Task<BitmapSource?> LoadThumbnailAsync(string filePath, int width, int height)
    {
        try
        {
            // 1. 尝试 EXIF 缩略图（最快）
            var exifThumb = await GetExifThumbnailAsync(filePath);
            if (exifThumb != null)
            {
                return ResizeBitmap(exifThumb, width, height);
            }

            // 2. 尝试 RAW 内嵌缩略图
            var rawThumb = await GetRawThumbnailAsync(filePath);
            if (rawThumb != null)
            {
                return ResizeBitmap(rawThumb, width, height);
            }

            // 3. 解码完整图片并缩放
            return await DecodeAndScaleAsync(filePath, width, height);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载缩略图失败: {filePath}, {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 加载完整图片
    /// </summary>
    public static async Task<BitmapSource?> LoadImageAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();

                using var image = new MagickImage(filePath);
                ct.ThrowIfCancellationRequested();

                image.AutoOrient();

                // 大图片优化：如果超过 4K，先缩放
                const uint maxDimension = 4096;
                if (image.Width > maxDimension || image.Height > maxDimension)
                {
                    image.Resize(maxDimension, maxDimension);
                }

                ct.ThrowIfCancellationRequested();
                return ToBitmapSource(image);
            }, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载图片失败: {filePath}, {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 获取 EXIF 缩略图
    /// </summary>
    private static async Task<BitmapSource?> GetExifThumbnailAsync(string filePath)
    {
        try
        {
            return await Task.Run(() =>
            {
                using var image = new MagickImage();
                image.Ping(filePath);

                var exifProfile = image.GetExifProfile();
                using var thumbnail = exifProfile?.CreateThumbnail();

                if (thumbnail != null)
                {
                    thumbnail.AutoOrient();
                    return ToBitmapSource(thumbnail);
                }

                return null;
            });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 获取 RAW 内嵌缩略图
    /// </summary>
    private static async Task<BitmapSource?> GetRawThumbnailAsync(string filePath)
    {
        try
        {
            return await Task.Run(() =>
            {
                using var image = new MagickImage();
                image.Ping(filePath);

                // 尝试获取 RAW 内嵌缩略图
                var profile = image.GetProfile("dng:thumbnail");
                if (profile != null)
                {
                    var data = profile.ToByteArray();
                    if (data != null && data.Length > 0)
                    {
                        using var thumb = new MagickImage(data);
                        thumb.AutoOrient();
                        return ToBitmapSource(thumb);
                    }
                }

                return null;
            });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 解码并缩放图片
    /// </summary>
    private static async Task<BitmapSource?> DecodeAndScaleAsync(string filePath, int width, int height)
    {
        try
        {
            return await Task.Run(() =>
            {
                using var image = new MagickImage(filePath);
                image.AutoOrient();

                // 计算缩放尺寸
                var scale = Math.Min(
                    (double)width / image.Width,
                    (double)height / image.Height);

                if (scale < 1)
                {
                    var newWidth = (uint)(image.Width * scale);
                    var newHeight = (uint)(image.Height * scale);
                    image.Resize(newWidth, newHeight);
                }

                return ToBitmapSource(image);
            });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 将 MagickImage 转换为 BitmapSource
    /// </summary>
    private static BitmapSource ToBitmapSource(IMagickImage<ushort> image)
    {
        // 获取像素数据
        var width = (int)image.Width;
        var height = (int)image.Height;
        var pixelData = image.GetPixels().ToByteArray(PixelMapping.RGBA);

        if (pixelData == null)
        {
            // 尝试 BGRA 格式
            pixelData = image.GetPixels().ToByteArray(PixelMapping.BGRA);
        }

        if (pixelData == null)
        {
            throw new InvalidOperationException("无法获取图像像素数据");
        }

        // 计算步长
        var stride = width * 4; // RGBA = 4 bytes per pixel

        // 创建 BitmapSource
        var bitmap = BitmapSource.Create(
            width,
            height,
            96, // DPI X
            96, // DPI Y
            System.Windows.Media.PixelFormats.Pbgra32,
            null,
            pixelData,
            stride);

        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// 缩放 BitmapSource
    /// </summary>
    private static BitmapSource ResizeBitmap(BitmapSource source, int width, int height)
    {
        var scale = Math.Min(
            (double)width / source.PixelWidth,
            (double)height / source.PixelHeight);

        if (scale >= 1) return source;

        var scaledBitmap = new TransformedBitmap(
            source,
            new System.Windows.Media.ScaleTransform(scale, scale));

        scaledBitmap.Freeze();
        return scaledBitmap;
    }
}
