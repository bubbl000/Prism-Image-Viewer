using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageBrowser;

/// <summary>
/// Tile 加载器
/// 处理超大图像（>8192px）的分块加载和金字塔层级
/// 使用 PsdLoader 作为底层解码器
/// </summary>
internal static class TileLoader
{
    // Tile 大小
    public const int TileSize = 512;

    // 启用 tile 模式的阈值
    // 警告：当前实现每次都会加载完整图像，所以阈值不能太激进
    // 对于超大文件（几GB的PSB），Tile 模式实际上会失效
    // TODO: 实现真正的 tile 裁剪解码
    public const int TileModeThreshold = 4096;  // 从 8192 降低到 4096 更保守

    /// <summary>
    /// 判断是否需要启用 tile 模式
    /// </summary>
    public static bool NeedsTileMode(string filePath)
    {
        try
        {
            var info = PsdLoader.LoadFileInfo(filePath);
            if (info == null) return false;
            return info.Width > TileModeThreshold || info.Height > TileModeThreshold;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 获取图像尺寸
    /// </summary>
    public static (int Width, int Height) GetImageSize(string filePath)
    {
        try
        {
            var info = PsdLoader.LoadFileInfo(filePath);
            if (info == null) return (0, 0);
            return (info.Width, info.Height);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// 计算金字塔层级数
    /// </summary>
    public static int CalculateLevelCount(int width, int height)
    {
        int maxDim = Math.Max(width, height);
        int levels = 0;

        while (maxDim > TileSize)
        {
            maxDim /= 2;
            levels++;
        }

        return Math.Max(1, levels);
    }

    /// <summary>
    /// 计算指定层级的图像尺寸
    /// </summary>
    public static (int Width, int Height) GetLevelSize(int originalWidth, int originalHeight, int level)
    {
        int scale = 1 << level; // 2^level
        return (originalWidth / scale, originalHeight / scale);
    }

    /// <summary>
    /// 计算视口需要加载的 tiles
    /// </summary>
    public static List<TileInfo> CalculateVisibleTiles(
        int imageWidth,
        int imageHeight,
        ViewportInfo viewport,
        int level)
    {
        var tiles = new List<TileInfo>();

        // 当前层级的图像尺寸
        var (levelWidth, levelHeight) = GetLevelSize(imageWidth, imageHeight, level);
        double scale = 1.0 / (1 << level);

        // 计算视口在层级图像中的像素坐标
        double viewX = viewport.X * scale / viewport.Scale;
        double viewY = viewport.Y * scale / viewport.Scale;
        double viewW = viewport.Width * scale / viewport.Scale;
        double viewH = viewport.Height * scale / viewport.Scale;

        // 计算需要加载的 tile 范围
        int startTileX = Math.Max(0, (int)(viewX / TileSize));
        int startTileY = Math.Max(0, (int)(viewY / TileSize));
        int endTileX = Math.Min((int)Math.Ceiling((viewX + viewW) / TileSize), (int)Math.Ceiling((double)levelWidth / TileSize));
        int endTileY = Math.Min((int)Math.Ceiling((viewY + viewH) / TileSize), (int)Math.Ceiling((double)levelHeight / TileSize));

        // 生成 tile 信息列表
        for (int ty = startTileY; ty < endTileY; ty++)
        {
            for (int tx = startTileX; tx < endTileX; tx++)
            {
                int pixelX = tx * TileSize;
                int pixelY = ty * TileSize;
                int tileW = Math.Min(TileSize, levelWidth - pixelX);
                int tileH = Math.Min(TileSize, levelHeight - pixelY);

                tiles.Add(new TileInfo(
                    X: tx,
                    Y: ty,
                    Level: level,
                    PixelX: pixelX,
                    PixelY: pixelY,
                    Width: tileW,
                    Height: tileH,
                    Scale: scale
                ));
            }
        }

        return tiles;
    }

    // 文件大小阈值（MB）- 超过此值的文件将拒绝使用 tile 模式
    private const int MaxFileSizeForTileModeMB = 200;
    private const long MaxFileSizeForTileModeBytes = MaxFileSizeForTileModeMB * 1024L * 1024L;

    /// <summary>
    /// 检查文件是否适合使用 tile 模式
    /// </summary>
    public static bool IsFileSuitableForTileMode(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists) return false;
            return fileInfo.Length <= MaxFileSizeForTileModeBytes;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 加载单个 tile
    /// ⚠️ 警告：当前实现每次都会加载完整图像，然后裁剪出 tile 区域
    /// 对于超大文件（>200MB），会拒绝加载以避免内存问题
    /// TODO: 实现真正的 tile 裁剪解码，只加载需要的图像区域
    /// </summary>
    public static BitmapSource? LoadTile(string filePath, TileInfo tile, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            // 安全检查：如果文件太大，拒绝加载以避免内存问题
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Exists && fileInfo.Length > MaxFileSizeForTileModeBytes)
            {
                System.Diagnostics.Debug.WriteLine($"[TileLoader] 文件过大 ({fileInfo.Length / 1024 / 1024}MB)，跳过 tile 加载: {filePath}");
                return null;
            }

            // 使用 PsdLoader 加载图像
            // ⚠️ 注意：这里加载的是完整图像，不是 tile 区域！
            // 对于超大文件，这会导致严重的性能问题
            var bitmap = PsdLoader.LoadImage(filePath, ct);
            if (bitmap == null) return null;

            ct.ThrowIfCancellationRequested();

            // 如果需要，先调整图像到对应层级大小
            if (tile.Level > 0)
            {
                var (levelW, levelH) = GetLevelSize(bitmap.PixelWidth, bitmap.PixelHeight, tile.Level);

                // 调整图像大小
                var scaleTransform = new ScaleTransform(
                    (double)levelW / bitmap.PixelWidth,
                    (double)levelH / bitmap.PixelHeight
                );
                var scaledBitmap = new TransformedBitmap(bitmap, scaleTransform);
                scaledBitmap.Freeze();
                bitmap = scaledBitmap;

                ct.ThrowIfCancellationRequested();
            }

            // 裁剪出 tile 区域
            int cropX = tile.PixelX;
            int cropY = tile.PixelY;
            int cropW = Math.Min(tile.Width, bitmap.PixelWidth - cropX);
            int cropH = Math.Min(tile.Height, bitmap.PixelHeight - cropY);

            if (cropW <= 0 || cropH <= 0)
                return null;

            // 使用 CroppedBitmap 裁剪
            var croppedBitmap = new CroppedBitmap(bitmap, new System.Windows.Int32Rect(cropX, cropY, cropW, cropH));
            croppedBitmap.Freeze();

            return croppedBitmap;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 批量加载 tiles（带缓存检查）
    /// </summary>
    public static async Task<Dictionary<TileKey, BitmapSource>> LoadTilesAsync(
        string filePath,
        List<TileInfo> tiles,
        TileCache cache,
        CancellationToken ct = default)
    {
        var results = new Dictionary<TileKey, BitmapSource>();

        await Task.Run(() =>
        {
            foreach (var tile in tiles)
            {
                if (ct.IsCancellationRequested)
                    break;

                var key = new TileKey(filePath, tile.Level, tile.X, tile.Y);

                // 检查缓存
                var cached = cache.TryGet(key);
                if (cached != null)
                {
                    results[key] = cached;
                    continue;
                }

                // 加载 tile
                var bitmap = LoadTile(filePath, tile, ct);
                if (bitmap != null)
                {
                    cache.Put(key, bitmap);
                    results[key] = bitmap;
                }
            }
        }, ct);

        return results;
    }

    /// <summary>
    /// 预生成所有层级的 tiles（用于后台处理）
    /// </summary>
    public static async Task PrecomputeAllLevelsAsync(
        string filePath,
        string outputDirectory,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            var (width, height) = GetImageSize(filePath);
            if (width == 0 || height == 0) return;

            int levelCount = CalculateLevelCount(width, height);

            for (int level = 0; level < levelCount; level++)
            {
                if (ct.IsCancellationRequested) break;

                var (levelW, levelH) = GetLevelSize(width, height, level);
                int tilesX = (int)Math.Ceiling((double)levelW / TileSize);
                int tilesY = (int)Math.Ceiling((double)levelH / TileSize);

                for (int ty = 0; ty < tilesY; ty++)
                {
                    for (int tx = 0; tx < tilesX; tx++)
                    {
                        if (ct.IsCancellationRequested) break;

                        var tile = new TileInfo(
                            X: tx,
                            Y: ty,
                            Level: level,
                            PixelX: tx * TileSize,
                            PixelY: ty * TileSize,
                            Width: Math.Min(TileSize, levelW - tx * TileSize),
                            Height: Math.Min(TileSize, levelH - ty * TileSize),
                            Scale: 1.0 / (1 << level)
                        );

                        var bitmap = LoadTile(filePath, tile, ct);
                        if (bitmap != null)
                        {
                            // 保存 tile 到磁盘
                            string tilePath = Path.Combine(outputDirectory, $"{level}_{tx}_{ty}.png");
                            SaveTileToDisk(bitmap, tilePath);
                        }

                        // 报告进度
                        double currentProgress = (level * tilesX * tilesY + ty * tilesX + tx) / (double)(levelCount * tilesX * tilesY);
                        progress?.Report(currentProgress);
                    }
                }
            }

            progress?.Report(1.0);
        }, ct);
    }

    /// <summary>
    /// 保存 tile 到磁盘
    /// </summary>
    private static void SaveTileToDisk(BitmapSource bitmap, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using var fs = new FileStream(path, FileMode.Create);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(fs);
        }
        catch { }
    }

    /// <summary>
    /// 加载预览缩略图（指定最大尺寸）
    /// </summary>
    public static BitmapSource? LoadPreviewThumbnail(string filePath, int maxSize)
    {
        try
        {
            // 使用 PsdLoader 加载缩略图
            var thumbnail = PsdLoader.LoadThumbnail(filePath);
            if (thumbnail == null) return null;

            // 如果缩略图已经小于最大尺寸，直接返回
            if (thumbnail.PixelWidth <= maxSize && thumbnail.PixelHeight <= maxSize)
                return thumbnail;

            // 否则缩小到指定尺寸
            return ResizeBitmap(thumbnail, maxSize);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 选择最佳层级
    /// </summary>
    public static int SelectBestLevel(int imageWidth, int imageHeight, double viewportScale)
    {
        int levelCount = CalculateLevelCount(imageWidth, imageHeight);

        // 根据视口缩放选择层级
        // viewportScale > 1 表示放大，需要更高分辨率
        // viewportScale < 1 表示缩小，可以使用更低分辨率

        if (viewportScale >= 1.0)
        {
            // 放大时使用最高分辨率（层级 0）
            return 0;
        }

        // 缩小时选择合适的层级
        double effectiveScale = viewportScale;
        int level = 0;

        while (effectiveScale < 0.5 && level < levelCount - 1)
        {
            effectiveScale *= 2;
            level++;
        }

        return level;
    }

    /// <summary>
    /// 调整位图大小
    /// </summary>
    private static BitmapSource ResizeBitmap(BitmapSource source, int maxSize)
    {
        double scale = Math.Min(
            (double)maxSize / source.PixelWidth,
            (double)maxSize / source.PixelHeight
        );

        int newWidth = Math.Max(1, (int)(source.PixelWidth * scale));
        int newHeight = Math.Max(1, (int)(source.PixelHeight * scale));

        var scaledBitmap = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        scaledBitmap.Freeze();

        return scaledBitmap;
    }
}
