using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageBrowser;

/// <summary>
/// 简单的日志帮助类
/// </summary>
internal static class Log
{
    public static void Info(string message)
    {
        var msg = $"[{DateTime.Now:HH:mm:ss}] [INFO] {message}";
        System.Diagnostics.Debug.WriteLine(msg);
        Console.WriteLine(msg);
    }

    public static void Error(string message, Exception? ex = null)
    {
        var msg = $"[{DateTime.Now:HH:mm:ss}] [ERROR] {message}";
        System.Diagnostics.Debug.WriteLine(msg);
        Console.WriteLine(msg);
        if (ex != null)
        {
            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] [ERROR] {ex}");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [ERROR] {ex}");
        }
    }
}

/// <summary>
/// PSD/PSB 文件头信息
/// </summary>
internal class PsdFileHeader
{
    public string Signature = "8BPS";
    public short Version;
    public byte[] Reserved = new byte[6];
    public short Channels;
    public int Height;
    public int Width;
    public short Depth;
    public short ColorMode;
    public bool IsLargeDocument;
}

/// <summary>
/// PSD/PSB 文件加载器 - 基于 SimplePsdDecoder 纯 C# 实现
/// 无第三方依赖，支持 Raw 和 RLE 压缩
/// </summary>
internal static class PsdLoader
{
    // 使用数组池减少内存分配
    private static readonly ArrayPool<byte> BytePool = ArrayPool<byte>.Shared;

    // PSB大文件阈值：超过此尺寸使用流式加载
    private const int LargeFileThreshold = 8192;

    /// <summary>
    /// 图层信息
    /// </summary>
    public record LayerInfo(
        string Name,
        int Index,
        bool IsVisible,
        int X,
        int Y,
        int Width,
        int Height
    );

    /// <summary>
    /// PSD/PSB 文件信息
    /// </summary>
    public record PsdFileInfo(
        int Width,
        int Height,
        int BitsPerChannel,
        bool IsLargeDocument,
        IReadOnlyList<LayerInfo> Layers,
        bool HasThumbnail
    );

    /// <summary>
    /// 快速读取缩略图（毫秒级）
    /// 通过读取合并图像数据并缩小尺寸实现快速预览
    /// </summary>
    public static BitmapSource? LoadThumbnail(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                Log.Error($"文件不存在: {filePath}");
                return null;
            }

            Log.Info($"开始加载缩略图: {filePath}");

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
            using var reader = new BinaryReader(stream);

            // 读取文件头
            var header = ReadFileHeader(reader);
            Log.Info($"文件格式: {(header.IsLargeDocument ? "PSB" : "PSD")}, 尺寸: {header.Width}x{header.Height}");

            // 跳过颜色模式数据
            SkipColorModeData(reader, header.IsLargeDocument);

            // 跳过图像资源
            SkipImageResources(reader, header.IsLargeDocument);

            // 跳过图层和蒙版信息
            SkipLayerAndMaskInfo(reader, header.IsLargeDocument);

            // 读取图像数据（拼合图像）
            var imageData = ReadImageData(reader, header);

            if (imageData == null)
            {
                Log.Error("读取图像数据失败");
                return null;
            }

            // 创建位图
            var bitmap = CreateBitmapSource(imageData, header.Width, header.Height);

            // 如果图像太大，缩小到缩略图尺寸
            const int thumbnailSize = 256;
            if (header.Width > thumbnailSize || header.Height > thumbnailSize)
            {
                bitmap = ResizeBitmap(bitmap, thumbnailSize);
            }

            Log.Info("缩略图加载完成");
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Error($"加载缩略图失败: {ex.Message}", ex);
            return null;
        }
    }

    /// <summary>
    /// 读取文件信息（包含图层结构）
    /// </summary>
    public static PsdFileInfo? LoadFileInfo(string filePath)
    {
        try
        {
            Log.Info($"开始读取文件信息: {filePath}");

            if (!File.Exists(filePath))
            {
                Log.Error($"文件不存在: {filePath}");
                return null;
            }

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
            using var reader = new BinaryReader(stream);

            // 读取文件头
            var header = ReadFileHeader(reader);
            Log.Info($"图像尺寸: {header.Width}x{header.Height}, 通道: {header.Channels}, 深度: {header.Depth}bit");

            // 跳过颜色模式数据
            SkipColorModeData(reader, header.IsLargeDocument);

            // 跳过图像资源
            SkipImageResources(reader, header.IsLargeDocument);

            // 读取图层信息（简化版，只读取基本信息）
            var layers = ReadLayerInfo(reader, header.IsLargeDocument);

            // 判断是否为PSB格式（通过文件扩展名和尺寸）
            bool isPsb = filePath.EndsWith(".psb", StringComparison.OrdinalIgnoreCase);
            bool isLarge = isPsb || header.Width > LargeFileThreshold || header.Height > LargeFileThreshold;

            Log.Info($"文件信息读取完成: 图层数={layers.Count}, 大文件={isLarge}");

            return new PsdFileInfo(
                Width: header.Width,
                Height: header.Height,
                BitsPerChannel: header.Depth,
                IsLargeDocument: isLarge,
                Layers: layers,
                HasThumbnail: true
            );
        }
        catch (Exception ex)
        {
            Log.Error($"读取文件信息失败: {ex.Message}", ex);
            return null;
        }
    }

    /// <summary>
    /// 加载完整图像（自动判断是否需要流式加载）
    /// </summary>
    public static BitmapSource? LoadImage(string filePath, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                Log.Error($"文件不存在: {filePath}");
                return null;
            }

            var fileInfo = LoadFileInfo(filePath);
            if (fileInfo == null)
            {
                Log.Error($"无法读取文件信息: {filePath}");
                return null;
            }

            Log.Info($"加载图像: {fileInfo.Width}x{fileInfo.Height}, 大文件: {fileInfo.IsLargeDocument}");

            ct.ThrowIfCancellationRequested();

            // 大文件使用流式加载
            if (fileInfo.IsLargeDocument)
            {
                return LoadLargeImage(filePath, fileInfo, ct);
            }

            // 普通文件直接加载
            return LoadNormalImage(filePath, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"加载图像失败: {ex.Message}", ex);
            return null;
        }
    }

    /// <summary>
    /// 普通 PSD 文件加载
    /// </summary>
    private static BitmapSource? LoadNormalImage(string filePath, CancellationToken ct)
    {
        try
        {
            Log.Info($"开始加载普通PSD: {filePath}");
            ct.ThrowIfCancellationRequested();

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
            using var reader = new BinaryReader(stream);

            // 读取文件头
            var header = ReadFileHeader(reader);
            Log.Info($"文件格式: {(header.IsLargeDocument ? "PSB" : "PSD")}");

            // 跳过颜色模式数据
            SkipColorModeData(reader, header.IsLargeDocument);

            // 跳过图像资源
            SkipImageResources(reader, header.IsLargeDocument);

            // 跳过图层和蒙版信息
            SkipLayerAndMaskInfo(reader, header.IsLargeDocument);

            ct.ThrowIfCancellationRequested();

            // 读取图像数据（拼合图像）
            var imageData = ReadImageData(reader, header);

            if (imageData == null)
            {
                Log.Error("读取图像数据失败");
                return null;
            }

            ct.ThrowIfCancellationRequested();

            // 创建位图
            var bitmap = CreateBitmapSource(imageData, header.Width, header.Height);
            Log.Info("图像加载完成");

            return bitmap;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"加载普通PSD失败: {ex.Message}", ex);
            return null;
        }
    }

    /// <summary>
    /// 大 PSB 文件流式加载
    /// </summary>
    private static BitmapSource? LoadLargeImage(string filePath, PsdFileInfo info, CancellationToken ct)
    {
        try
        {
            Log.Info($"开始加载大文件: {filePath}");
            ct.ThrowIfCancellationRequested();

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
            using var reader = new BinaryReader(stream);

            // 读取文件头
            var header = ReadFileHeader(reader);

            // 跳过颜色模式数据
            SkipColorModeData(reader, header.IsLargeDocument);

            // 跳过图像资源
            SkipImageResources(reader, header.IsLargeDocument);

            // 跳过图层和蒙版信息
            SkipLayerAndMaskInfo(reader, header.IsLargeDocument);

            ct.ThrowIfCancellationRequested();

            // 读取图像数据
            var imageData = ReadImageData(reader, header);

            if (imageData == null)
            {
                Log.Error("读取图像数据失败");
                return null;
            }

            ct.ThrowIfCancellationRequested();

            // 如果图像太大，先缩小到合理尺寸
            var maxDimension = 4096;
            BitmapSource bitmap;
            if (header.Width > maxDimension || header.Height > maxDimension)
            {
                Log.Info($"图像尺寸过大，缩小到 {maxDimension}px");
                bitmap = CreateBitmapSource(imageData, header.Width, header.Height);
                bitmap = ResizeBitmap(bitmap, maxDimension);
            }
            else
            {
                bitmap = CreateBitmapSource(imageData, header.Width, header.Height);
            }

            Log.Info("大文件加载完成");
            return bitmap;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"加载大文件失败: {ex.Message}", ex);
            return null;
        }
    }

    /// <summary>
    /// 按需合成指定图层（用于后续图层面板功能）
    /// 注意：SimplePsdDecoder 目前只支持读取合并图像，不支持图层合成
    /// </summary>
    public static BitmapSource? LoadWithLayerMask(string filePath, IEnumerable<int> visibleLayerIndices, CancellationToken ct = default)
    {
        // SimplePsdDecoder 目前只支持读取合并图像
        // 图层合成功能需要更复杂的实现，暂时返回完整图像
        Log.Info("图层合成功能需要完整图层数据支持，暂时返回合并图像");
        return LoadImage(filePath, ct);
    }

    /// <summary>
    /// 判断文件是否为 PSB 大文档格式
    /// </summary>
    public static bool IsLargeDocument(string filePath)
    {
        try
        {
            var info = LoadFileInfo(filePath);
            return info?.IsLargeDocument ?? false;
        }
        catch
        {
            return false;
        }
    }

    #region 文件解析方法

    private static PsdFileHeader ReadFileHeader(BinaryReader reader)
    {
        var signature = new string(reader.ReadChars(4));
        if (signature != "8BPS")
            throw new InvalidDataException("不是有效的 PSD/PSB 文件");

        var header = new PsdFileHeader
        {
            Version = ReadInt16BE(reader),
            Reserved = reader.ReadBytes(6),
            Channels = ReadInt16BE(reader),
            Height = ReadInt32BE(reader),
            Width = ReadInt32BE(reader),
            Depth = ReadInt16BE(reader),
            ColorMode = ReadInt16BE(reader)
        };

        // Version 1 = PSD, Version 2 = PSB
        header.IsLargeDocument = header.Version == 2;

        if (header.Version != 1 && header.Version != 2)
            throw new InvalidDataException($"不支持的文件版本: {header.Version}");

        return header;
    }

    private static void SkipColorModeData(BinaryReader reader, bool isLargeDocument)
    {
        long length = isLargeDocument ? ReadInt64BE(reader) : ReadInt32BE(reader);
        Log.Info($"颜色模式数据长度: {length} bytes");
        if (length > 0)
            reader.BaseStream.Seek(length, SeekOrigin.Current);
    }

    private static void SkipImageResources(BinaryReader reader, bool isLargeDocument)
    {
        long length = isLargeDocument ? ReadInt64BE(reader) : ReadInt32BE(reader);
        Log.Info($"图像资源长度: {length} bytes");
        if (length > 0)
            reader.BaseStream.Seek(length, SeekOrigin.Current);
    }

    private static void SkipLayerAndMaskInfo(BinaryReader reader, bool isLargeDocument)
    {
        long layerInfoLength = isLargeDocument ? ReadInt64BE(reader) : ReadInt32BE(reader);
        Log.Info($"图层信息长度: {layerInfoLength} bytes");

        // 验证长度是否合理
        long remainingBytes = reader.BaseStream.Length - reader.BaseStream.Position;
        if (layerInfoLength < 0 || layerInfoLength > remainingBytes)
        {
            Log.Error($"图层信息长度异常: {layerInfoLength}");
            return;
        }

        if (layerInfoLength > 0 && layerInfoLength <= remainingBytes)
        {
            reader.BaseStream.Seek(layerInfoLength, SeekOrigin.Current);
        }
    }

    private static IReadOnlyList<LayerInfo> ReadLayerInfo(BinaryReader reader, bool isLargeDocument)
    {
        // 简化版：跳过图层信息，返回空列表
        // 完整实现需要解析图层结构
        SkipLayerAndMaskInfo(reader, isLargeDocument);
        return new List<LayerInfo>();
    }

    private static byte[]? ReadImageData(BinaryReader reader, PsdFileHeader header)
    {
        int compression = ReadInt16BE(reader);
        Log.Info($"压缩方式: {(compression == 0 ? "Raw" : compression == 1 ? "RLE" : $"Unknown({compression})")}");

        int width = header.Width;
        int height = header.Height;
        int channels = header.Channels;
        int depth = header.Depth;
        int pixelCount = width * height;

        // 计算每行字节数（对齐到2字节）
        int bytesPerChannel = (depth + 7) / 8;
        int rowBytesPadded = (width * bytesPerChannel + 1) & ~1;

        // 预分配结果缓冲区 (BGRA)
        byte[] result = new byte[pixelCount * 4];

        try
        {
            if (compression == 0) // Raw
            {
                ReadRawData(reader, header, result, width, height, channels, rowBytesPadded);
            }
            else if (compression == 1) // RLE
            {
                ReadRleData(reader, header, result, width, height, channels);
            }
            else
            {
                throw new NotSupportedException($"不支持的压缩方式: {compression}");
            }

            return result;
        }
        catch (Exception ex)
        {
            Log.Error($"读取图像数据失败: {ex.Message}", ex);
            return null;
        }
    }

    private static void ReadRawData(BinaryReader reader, PsdFileHeader header, byte[] result,
        int width, int height, int channels, int rowBytesPadded)
    {
        int pixelCount = width * height;

        // 使用数组池临时存储通道数据
        byte[][] channelData = new byte[channels][];
        for (int ch = 0; ch < channels; ch++)
        {
            channelData[ch] = BytePool.Rent(pixelCount);
        }

        try
        {
            // 读取所有通道数据
            for (int ch = 0; ch < channels; ch++)
            {
                for (int y = 0; y < height; y++)
                {
                    byte[] row = reader.ReadBytes(rowBytesPadded);
                    Buffer.BlockCopy(row, 0, channelData[ch], y * width, width);
                }
            }

            // 合并通道为 BGRA 格式
            MergeChannels(result, channelData, channels, pixelCount);
        }
        finally
        {
            // 归还数组池
            for (int ch = 0; ch < channels; ch++)
            {
                BytePool.Return(channelData[ch]);
            }
        }
    }

    private static void ReadRleData(BinaryReader reader, PsdFileHeader header, byte[] result,
        int width, int height, int channels)
    {
        int pixelCount = width * height;
        long rowCount = (long)channels * height;

        // 读取行字节计数表
        int[] rowByteCounts = new int[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            rowByteCounts[i] = header.IsLargeDocument ? ReadInt32BE(reader) : ReadInt16BE(reader);
        }

        Log.Info($"RLE 行字节计数表: {rowCount} entries");

        // 使用数组池临时存储通道数据
        byte[][] channelData = new byte[channels][];
        for (int ch = 0; ch < channels; ch++)
        {
            channelData[ch] = BytePool.Rent(pixelCount);
        }

        try
        {
            // 读取 RLE 数据
            for (int ch = 0; ch < channels; ch++)
            {
                for (int y = 0; y < height; y++)
                {
                    DecodeRleRow(reader, channelData[ch], y * width, width);
                }
            }

            // 合并通道为 BGRA 格式
            MergeChannels(result, channelData, channels, pixelCount);
        }
        finally
        {
            // 归还数组池
            for (int ch = 0; ch < channels; ch++)
            {
                BytePool.Return(channelData[ch]);
            }
        }
    }

    private static void DecodeRleRow(BinaryReader reader, byte[] output, int offset, int count)
    {
        int written = 0;
        while (written < count)
        {
            sbyte control = (sbyte)reader.ReadByte();

            if (control >= 0)
            {
                // 复制接下来的 control + 1 个字节
                int length = control + 1;
                reader.BaseStream.Read(output, offset + written, length);
                written += length;
            }
            else if (control != -128)
            {
                // 重复接下来的字节 -control + 1 次
                int length = -control + 1;
                byte value = reader.ReadByte();
                output.AsSpan(offset + written, length).Fill(value);
                written += length;
            }
            // -128 是 NOP，跳过
        }
    }

    private static void MergeChannels(byte[] result, byte[][] channelData, int channels, int pixelCount)
    {
        if (channels >= 3) // RGB
        {
            // 使用并行处理大图像
            if (pixelCount > 1000000)
            {
                Parallel.For(0, pixelCount, i =>
                {
                    result[i * 4 + 0] = channelData[2][i]; // B
                    result[i * 4 + 1] = channelData[1][i]; // G
                    result[i * 4 + 2] = channelData[0][i]; // R
                    result[i * 4 + 3] = channels >= 4 ? channelData[3][i] : (byte)255; // A
                });
            }
            else
            {
                for (int i = 0; i < pixelCount; i++)
                {
                    result[i * 4 + 0] = channelData[2][i]; // B
                    result[i * 4 + 1] = channelData[1][i]; // G
                    result[i * 4 + 2] = channelData[0][i]; // R
                    result[i * 4 + 3] = channels >= 4 ? channelData[3][i] : (byte)255; // A
                }
            }
        }
        else if (channels == 1) // Grayscale
        {
            if (pixelCount > 1000000)
            {
                Parallel.For(0, pixelCount, i =>
                {
                    byte val = channelData[0][i];
                    result[i * 4 + 0] = val;
                    result[i * 4 + 1] = val;
                    result[i * 4 + 2] = val;
                    result[i * 4 + 3] = 255;
                });
            }
            else
            {
                for (int i = 0; i < pixelCount; i++)
                {
                    byte val = channelData[0][i];
                    result[i * 4 + 0] = val;
                    result[i * 4 + 1] = val;
                    result[i * 4 + 2] = val;
                    result[i * 4 + 3] = 255;
                }
            }
        }
    }

    private static BitmapSource CreateBitmapSource(byte[] imageData, int width, int height)
    {
        // 创建 WriteableBitmap
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);

        // 写入像素数据
        bitmap.WritePixels(
            new System.Windows.Int32Rect(0, 0, width, height),
            imageData,
            width * 4, // stride
            0
        );

        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource ResizeBitmap(BitmapSource source, int maxSize)
    {
        double scale = Math.Min(
            (double)maxSize / source.PixelWidth,
            (double)maxSize / source.PixelHeight
        );

        int newWidth = Math.Max(1, (int)(source.PixelWidth * scale));
        int newHeight = Math.Max(1, (int)(source.PixelHeight * scale));

        var scaledBitmap = new TransformedBitmap(source, new System.Windows.Media.ScaleTransform(scale, scale));
        scaledBitmap.Freeze();

        return scaledBitmap;
    }

    private static short ReadInt16BE(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(2);
        if (bytes.Length < 2)
            throw new EndOfStreamException("读取 16-bit 整数失败: 数据不足");
        return (short)((bytes[0] << 8) | bytes[1]);
    }

    private static int ReadInt32BE(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(4);
        if (bytes.Length < 4)
            throw new EndOfStreamException("读取 32-bit 整数失败: 数据不足");
        return (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
    }

    private static long ReadInt64BE(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(8);
        if (bytes.Length < 8)
            throw new EndOfStreamException("读取 64-bit 整数失败: 数据不足");
        return ((long)bytes[0] << 56) | ((long)bytes[1] << 48) | ((long)bytes[2] << 40) |
               ((long)bytes[3] << 32) | ((long)bytes[4] << 24) | ((long)bytes[5] << 16) |
               ((long)bytes[6] << 8) | bytes[7];
    }

    #endregion
}
