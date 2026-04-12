using ImageMagick;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageBrowser.Services;

/// <summary>
/// 渐进式图片加载器
/// 先显示低质量预览/占位符，后台解码完整图片
/// 参考 ImageGlass 的渐进式加载策略
/// </summary>
public class ProgressiveImageLoader
{
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// 加载完成事件
    /// </summary>
    public event EventHandler<ProgressiveLoadEventArgs>? LoadCompleted;

    /// <summary>
    /// 加载进度事件
    /// </summary>
    public event EventHandler<ProgressiveLoadProgressEventArgs>? LoadProgress;

    /// <summary>
    /// 渐进式加载图片
    /// 流程：元数据读取 → 显示占位符 → 后台解码完整图片 → 渐进显示
    /// </summary>
    public async Task<ProgressiveLoadResult> LoadAsync(string filePath, int targetWidth = 0, int targetHeight = 0)
    {
        if (!File.Exists(filePath))
            return new ProgressiveLoadResult { Success = false, ErrorMessage = "文件不存在" };

        try
        {
            // 第1步：快速读取元数据（Ping）
            LoadProgress?.Invoke(this, new ProgressiveLoadProgressEventArgs
            {
                Stage = LoadStage.Metadata,
                Progress = 0,
                Message = "读取元数据..."
            });

            var metadata = await ReadMetadataAsync(filePath);
            if (metadata == null)
                return new ProgressiveLoadResult { Success = false, ErrorMessage = "无法读取图片元数据" };

            LoadProgress?.Invoke(this, new ProgressiveLoadProgressEventArgs
            {
                Stage = LoadStage.Metadata,
                Progress = 100,
                Message = "元数据读取完成",
                ImageWidth = metadata.Width,
                ImageHeight = metadata.Height
            });

            // 第2步：尝试获取快速预览（内嵌缩略图）
            LoadProgress?.Invoke(this, new ProgressiveLoadProgressEventArgs
            {
                Stage = LoadStage.Preview,
                Progress = 0,
                Message = "加载预览..."
            });

            var previewImage = await GetPreviewImageAsync(filePath, metadata);

            if (previewImage != null)
            {
                LoadProgress?.Invoke(this, new ProgressiveLoadProgressEventArgs
                {
                    Stage = LoadStage.Preview,
                    Progress = 100,
                    Message = "预览加载完成",
                    PreviewImage = previewImage
                });
            }

            // 检查是否已取消
            if (_cts.Token.IsCancellationRequested)
                return new ProgressiveLoadResult { Success = false, ErrorMessage = "已取消" };

            // 第3步：后台解码完整图片
            LoadProgress?.Invoke(this, new ProgressiveLoadProgressEventArgs
            {
                Stage = LoadStage.FullImage,
                Progress = 0,
                Message = "解码完整图片...",
                PreviewImage = previewImage
            });

            var fullImage = await DecodeFullImageAsync(filePath, metadata, targetWidth, targetHeight, _cts.Token);

            if (fullImage == null)
            {
                // 如果完整解码失败但有预览，返回预览
                if (previewImage != null)
                {
                    return new ProgressiveLoadResult
                    {
                        Success = true,
                        Image = previewImage,
                        IsPreview = true,
                        Width = metadata.Width,
                        Height = metadata.Height
                    };
                }
                return new ProgressiveLoadResult { Success = false, ErrorMessage = "无法解码图片" };
            }

            // 第4步：加载完成
            var result = new ProgressiveLoadResult
            {
                Success = true,
                Image = fullImage,
                IsPreview = false,
                Width = metadata.Width,
                Height = metadata.Height,
                Format = metadata.Format,
                HasAnimation = metadata.FrameCount > 1,
                FrameCount = metadata.FrameCount
            };

            LoadProgress?.Invoke(this, new ProgressiveLoadProgressEventArgs
            {
                Stage = LoadStage.FullImage,
                Progress = 100,
                Message = "加载完成",
                PreviewImage = previewImage
            });

            LoadCompleted?.Invoke(this, new ProgressiveLoadEventArgs { Result = result });

            return result;
        }
        catch (OperationCanceledException)
        {
            return new ProgressiveLoadResult { Success = false, ErrorMessage = "已取消", IsCancelled = true };
        }
        catch (Exception ex)
        {
            return new ProgressiveLoadResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// 取消加载
    /// </summary>
    public void Cancel()
    {
        _cts.Cancel();
    }

    /// <summary>
    /// 快速读取元数据
    /// </summary>
    private async Task<ImageMetadataInfo?> ReadMetadataAsync(string filePath)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var img = new MagickImage();
                // Ping 只读取元数据，不解码像素，速度极快
                img.Ping(filePath);

                // 检查是否有 EXIF 缩略图
                bool hasExifThumbnail = false;
                var exifProfile = img.GetExifProfile();
                if (exifProfile != null)
                {
                    using var thumb = exifProfile.CreateThumbnail();
                    hasExifThumbnail = thumb != null;
                }

                return new ImageMetadataInfo
                {
                    Width = (int)img.Width,
                    Height = (int)img.Height,
                    Format = img.Format.ToString(),
                    FrameCount = 1, // MagickImage Ping 模式下无法获取帧数
                    FileSize = new FileInfo(filePath).Length,
                    HasExifThumbnail = hasExifThumbnail
                };
            }
            catch { return null; }
        });
    }

    /// <summary>
    /// 获取预览图（内嵌缩略图或低质量版本）
    /// </summary>
    private async Task<BitmapSource?> GetPreviewImageAsync(string filePath, ImageMetadataInfo metadata)
    {
        // 尝试获取 EXIF 缩略图
        var exifThumb = await SmartThumbnailExtractor.GetThumbnailAsync(filePath, 256, 256, useEmbeddedOnly: true);
        if (exifThumb != null)
            return exifThumb;

        // 对于大图，先加载一个低质量的预览版本
        if (metadata.Width > 2000 || metadata.Height > 2000 || metadata.FileSize > 5 * 1024 * 1024)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var img = new MagickImage(filePath);
                    img.AutoOrient();

                    // 快速缩放到低分辨率
                    img.Resize(800, 800);
                    img.Quality = 70; // 低质量以加快速度

                    return ToBitmapSource(img);
                }
                catch { return null; }
            });
        }

        return null;
    }

    /// <summary>
    /// 解码完整图片
    /// </summary>
    private async Task<BitmapSource?> DecodeFullImageAsync(
        string filePath,
        ImageMetadataInfo metadata,
        int targetWidth,
        int targetHeight,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                using var img = new MagickImage(filePath);
                img.AutoOrient();

                ct.ThrowIfCancellationRequested();

                // 如果需要缩放到特定尺寸
                if (targetWidth > 0 && targetHeight > 0)
                {
                    img.Resize((uint)targetWidth, (uint)targetHeight);
                }
                // 对于超大图片，限制最大尺寸以节省内存
                else if (metadata.Width > 8000 || metadata.Height > 8000)
                {
                    img.Resize(4000, 4000);
                }

                img.Quality = 95;

                ct.ThrowIfCancellationRequested();

                return ToBitmapSource(img);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch { return null; }
        }, ct);
    }

    /// <summary>
    /// 将 MagickImage 转换为 BitmapSource
    /// </summary>
    private static BitmapSource ToBitmapSource(IMagickImage<ushort> image)
    {
        var width = (int)image.Width;
        var height = (int)image.Height;
        byte[]? pixelData = null;

        // 尝试 RGBA 映射
        try
        {
            pixelData = image.GetPixels().ToByteArray(PixelMapping.RGBA);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProgressiveImageLoader] RGBA 映射失败: {ex.Message}");
        }

        // 如果 RGBA 失败，尝试 BGRA
        if (pixelData == null)
        {
            try
            {
                pixelData = image.GetPixels().ToByteArray(PixelMapping.BGRA);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ProgressiveImageLoader] BGRA 映射失败: {ex.Message}");
            }
        }

        // 如果两种映射都失败，尝试其他格式
        if (pixelData == null)
        {
            try
            {
                // 尝试使用 MagickImage 的 Write 方法转换为 PNG 字节流
                using var ms = new MemoryStream();
                image.Format = MagickFormat.Png;
                image.Write(ms);
                ms.Position = 0;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ProgressiveImageLoader] PNG 回退转换失败: {ex.Message}");
                throw new InvalidOperationException("无法获取图像像素数据，所有转换方式均失败", ex);
            }
        }

        var stride = width * 4;
        var bitmapSource = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixelData, stride);
        bitmapSource.Freeze();
        return bitmapSource;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}

/// <summary>
/// 图片元数据信息
/// </summary>
public class ImageMetadataInfo
{
    public int Width { get; set; }
    public int Height { get; set; }
    public string Format { get; set; } = "Unknown";
    public int FrameCount { get; set; } = 1;
    public long FileSize { get; set; }
    public bool HasExifThumbnail { get; set; }
}

/// <summary>
/// 加载阶段
/// </summary>
public enum LoadStage
{
    Metadata,
    Preview,
    FullImage
}

/// <summary>
/// 渐进式加载结果
/// </summary>
public class ProgressiveLoadResult
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = "";
    public bool IsCancelled { get; set; }
    public BitmapSource? Image { get; set; }
    public bool IsPreview { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Format { get; set; } = "";
    public bool HasAnimation { get; set; }
    public int FrameCount { get; set; } = 1;
}

/// <summary>
/// 渐进式加载事件参数
/// </summary>
public class ProgressiveLoadEventArgs : EventArgs
{
    public ProgressiveLoadResult Result { get; set; } = new();
}

/// <summary>
/// 渐进式加载进度事件参数
/// </summary>
public class ProgressiveLoadProgressEventArgs : EventArgs
{
    public LoadStage Stage { get; set; }
    public int Progress { get; set; }
    public string Message { get; set; } = "";
    public BitmapSource? PreviewImage { get; set; }
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
}
