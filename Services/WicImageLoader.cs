using ImageBrowser.Models;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageBrowser.Services;

/// <summary>
/// WIC 图像加载服务
/// 使用 WicNet 进行 GPU 加速解码
/// </summary>
public class WicImageLoader
{
    public static readonly string[] SupportedExtensions = new[]
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp",
        ".raw", ".cr2", ".cr3", ".nef", ".orf", ".raf", ".arw", ".dng"
    };

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

            await Task.Run(() =>
            {
                try
                {
                    var decoder = BitmapDecoder.Create(new Uri(filePath), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    if (decoder.Frames.Count > 0)
                    {
                        var frame = decoder.Frames[0];
                        item.Width = frame.PixelWidth;
                        item.Height = frame.PixelHeight;
                        item.FrameCount = decoder.Frames.Count;
                    }
                }
                catch
                {
                    // 如果 WPF 解码失败，尝试使用 WicNet
                    try
                    {
                        using var wicBitmap = WicNet.WicBitmapSource.Load(filePath);
                        if (wicBitmap != null)
                        {
                            item.Width = (int)wicBitmap.Width;
                            item.Height = (int)wicBitmap.Height;
                            item.FrameCount = 1;
                        }
                    }
                    catch { }
                }
            });

            return item;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WIC 加载元数据失败: {filePath}, {ex.Message}");
            return null;
        }
    }

    public static async Task<BitmapSource?> LoadThumbnailAsync(string filePath, int width, int height)
    {
        try
        {
            return await Task.Run<BitmapSource?>(() =>
            {
                try
                {
                    var decoder = BitmapDecoder.Create(new Uri(filePath), BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnLoad);
                    if (decoder.Frames.Count > 0)
                    {
                        var frame = decoder.Frames[0];
                        var scale = Math.Min((double)width / frame.PixelWidth, (double)height / frame.PixelHeight);

                        BitmapSource source = frame;
                        if (scale < 1)
                        {
                            source = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
                        }

                        var bitmap = new WriteableBitmap(source);
                        bitmap.Freeze();
                        return bitmap;
                    }
                }
                catch { }

                // 如果 WPF 解码失败，尝试使用 WicNet
                try
                {
                    using var wicBitmap = WicNet.WicBitmapSource.Load(filePath);
                    if (wicBitmap != null)
                    {
                        var pixels = wicBitmap.CopyPixels();
                        var wicWidth = (int)wicBitmap.Width;
                        var wicHeight = (int)wicBitmap.Height;
                        var stride = wicWidth * 4;

                        var bitmap = new WriteableBitmap(wicWidth, wicHeight, 96, 96, PixelFormats.Bgra32, null);
                        bitmap.WritePixels(new Int32Rect(0, 0, wicWidth, wicHeight), pixels, stride, 0);
                        bitmap.Freeze();
                        return bitmap;
                    }
                }
                catch { }

                return null;
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WIC 加载缩略图失败: {filePath}, {ex.Message}");
            return null;
        }
    }

    public static async Task<BitmapSource?> LoadImageAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            return await Task.Run<BitmapSource?>(() =>
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var decoder = BitmapDecoder.Create(new Uri(filePath), BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnLoad);
                    if (decoder.Frames.Count > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        var frame = decoder.Frames[0];

                        BitmapSource source = frame;
                        if (frame.PixelWidth > 4096 || frame.PixelHeight > 4096)
                        {
                            var scale = Math.Min(4096.0 / frame.PixelWidth, 4096.0 / frame.PixelHeight);
                            source = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
                        }

                        var bitmap = new WriteableBitmap(source);
                        bitmap.Freeze();
                        return bitmap;
                    }
                }
                catch { }

                // 如果 WPF 解码失败，尝试使用 WicNet
                try
                {
                    ct.ThrowIfCancellationRequested();
                    using var wicBitmap = WicNet.WicBitmapSource.Load(filePath);
                    if (wicBitmap != null)
                    {
                        ct.ThrowIfCancellationRequested();

                        var pixels = wicBitmap.CopyPixels();
                        var width = (int)wicBitmap.Width;
                        var height = (int)wicBitmap.Height;
                        var stride = width * 4;

                        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
                        bitmap.Freeze();
                        return bitmap;
                    }
                }
                catch { }

                return null;
            }, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WIC 加载图片失败: {filePath}, {ex.Message}");
            return null;
        }
    }
}
