using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageBrowser;

/// <summary>
/// RAW 文件三档解码加载器
/// 
/// 第1档：EXIF内嵌JPEG缩略图（毫秒级，立即响应）
/// 第2档：LibRaw半尺寸模式解码1/4尺寸（亚秒级，预览用）
/// 第3档：LibRaw全尺寸16-bit解码（后台进行，按需触发）
/// </summary>
internal static class RawLoader
{
    // RAW 文件扩展名
    private static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".arw", ".cr2", ".cr3", ".nef", ".orf", ".raf", ".rw2",
        ".dng", ".pef", ".sr2", ".srf", ".x3f", ".kdc", ".mos",
        ".raw", ".erf", ".mrw", ".nrw", ".ptx", ".r3d", ".3fr"
    };

    /// <summary>
    /// 解码档位
    /// </summary>
    public enum DecodeLevel
    {
        Thumbnail,      // 第1档：缩略图
        HalfSize,       // 第2档：半尺寸
        FullSize        // 第3档：全尺寸
    }

    /// <summary>
    /// RAW 文件信息
    /// </summary>
    public record RawFileInfo(
        int Width,
        int Height,
        int BitsPerChannel,
        string CameraMake,
        string CameraModel,
        bool HasThumbnail
    );

    /// <summary>
    /// 判断是否为 RAW 文件
    /// </summary>
    public static bool IsRawFile(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return RawExtensions.Contains(ext);
    }

    // ==================== 第1档：EXIF内嵌JPEG缩略图 ====================

    /// <summary>
    /// 第1档：读取EXIF内嵌JPEG缩略图（毫秒级）
    /// 优先使用 LibRaw 提取，失败时回退到 TryExtractRawPreview()
    /// </summary>
    public static BitmapSource? LoadThumbnail(string filePath)
    {
        // 首先尝试使用 LibRaw 提取内嵌缩略图
        var thumb = LoadThumbnailWithLibRaw(filePath);
        if (thumb != null)
            return thumb;

        // 回退到现有的 TryExtractRawPreview()
        return TryExtractRawPreview(filePath);
    }

    /// <summary>
    /// 使用 LibRaw 提取内嵌缩略图
    /// </summary>
    private static BitmapSource? LoadThumbnailWithLibRaw(string filePath)
    {
        IntPtr lr = IntPtr.Zero;
        IntPtr thumbPtr = IntPtr.Zero;

        try
        {
            lr = LibRawInterop.libraw_init(0);
            if (lr == IntPtr.Zero)
                return null;

            // 打开文件
            int result = LibRawInterop.libraw_open_file(lr, filePath);
            if (result != (int)LibRawInterop.LibRawErrors.LIBRAW_SUCCESS)
                return null;

            // 解包缩略图
            result = LibRawInterop.libraw_unpack_thumb(lr);
            if (result != (int)LibRawInterop.LibRawErrors.LIBRAW_SUCCESS)
                return null;

            // 获取缩略图内存
            int errc;
            thumbPtr = LibRawInterop.libraw_dcraw_make_mem_thumb(lr, out errc);
            if (thumbPtr == IntPtr.Zero || errc != (int)LibRawInterop.LibRawErrors.LIBRAW_SUCCESS)
                return null;

            // 读取缩略图数据
            var info = LibRawInterop.GetProcessedImageInfo(thumbPtr);
            byte[] data = LibRawInterop.ReadProcessedImageData(thumbPtr);

            if (data.Length == 0)
                return null;

            // 根据类型处理
            if (info.type == LibRawInterop.LibRawImageType.LIBRAW_IMAGE_JPEG)
            {
                return DecodeJpegData(data);
            }
            else if (info.type == LibRawInterop.LibRawImageType.LIBRAW_IMAGE_BITMAP)
            {
                return DecodeBitmapData(data, info.width, info.height, info.colors, info.bits);
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (thumbPtr != IntPtr.Zero)
                LibRawInterop.libraw_dcraw_clear_mem(thumbPtr);
            if (lr != IntPtr.Zero)
                LibRawInterop.libraw_close(lr);
        }
    }

    /// <summary>
    /// 原有的 TryExtractRawPreview() - 作为第1档的 Fallback
    /// </summary>
    private static BitmapSource? TryExtractRawPreview(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            using var br = new BinaryReader(fs);

            // 尝试查找 JPEG SOI 标记 (0xFFD8)
            const int bufferSize = 8192;
            byte[] buffer = new byte[bufferSize];
            int bytesRead;
            long lastPosition = 0;

            while ((bytesRead = fs.Read(buffer, 0, bufferSize)) > 0)
            {
                for (int i = 0; i < bytesRead - 1; i++)
                {
                    if (buffer[i] == 0xFF && buffer[i + 1] == 0xD8)
                    {
                        long jpegStart = lastPosition + i;
                        fs.Position = jpegStart;

                        // 读取到 JPEG EOI 标记 (0xFFD9)
                        using var ms = new MemoryStream();
                        int b;
                        bool foundFF = false;
                        while ((b = fs.ReadByte()) != -1)
                        {
                            ms.WriteByte((byte)b);
                            if (foundFF && b == 0xD9)
                                break;
                            foundFF = (b == 0xFF);
                        }

                        if (ms.Length > 0)
                        {
                            ms.Position = 0;
                            var bitmap = new BitmapImage();
                            bitmap.BeginInit();
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.StreamSource = ms;
                            bitmap.EndInit();
                            bitmap.Freeze();
                            return bitmap;
                        }
                    }
                }
                lastPosition = fs.Position - 1;
                if (lastPosition > 0)
                    fs.Position = lastPosition;
            }
        }
        catch
        {
            // 忽略错误
        }
        return null;
    }

    // ==================== 第2档：LibRaw半尺寸解码 ====================

    /// <summary>
    /// 第2档：LibRaw半尺寸模式解码（亚秒级，1/4尺寸）
    /// 适合快速预览和缩略图生成
    /// </summary>
    public static BitmapSource? LoadHalfSize(string filePath, CancellationToken ct = default)
    {
        IntPtr lr = IntPtr.Zero;
        IntPtr imgPtr = IntPtr.Zero;

        try
        {
            ct.ThrowIfCancellationRequested();

            Log.Info($"LoadHalfSize: 开始加载 {filePath}");

            lr = LibRawInterop.libraw_init(0);
            if (lr == IntPtr.Zero)
            {
                Log.Error("LoadHalfSize: libraw_init 失败");
                return null;
            }
            Log.Info("LoadHalfSize: libraw_init 成功");

            ct.ThrowIfCancellationRequested();

            // 打开文件
            int result = LibRawInterop.libraw_open_file(lr, filePath);
            if (result != (int)LibRawInterop.LibRawErrors.LIBRAW_SUCCESS)
            {
                Log.Error($"LoadHalfSize: libraw_open_file 失败，错误码: {result}");
                return null;
            }
            Log.Info("LoadHalfSize: libraw_open_file 成功");

            ct.ThrowIfCancellationRequested();

            // 解包RAW数据
            result = LibRawInterop.libraw_unpack(lr);
            if (result != (int)LibRawInterop.LibRawErrors.LIBRAW_SUCCESS)
            {
                Log.Error($"LoadHalfSize: libraw_unpack 失败，错误码: {result}");
                return null;
            }
            Log.Info("LoadHalfSize: libraw_unpack 成功");

            ct.ThrowIfCancellationRequested();

            // 设置半尺寸模式
            LibRawInterop.libraw_set_half_size(lr, 1);
            LibRawInterop.libraw_set_output_bps(lr, 8);  // 8-bit输出
            LibRawInterop.libraw_set_auto_wb(lr, 1);     // 自动白平衡
            LibRawInterop.libraw_set_no_auto_bright(lr, 0); // 自动亮度
            Log.Info("LoadHalfSize: 处理参数设置完成");

            ct.ThrowIfCancellationRequested();

            // 处理图像
            result = LibRawInterop.libraw_dcraw_process(lr);
            if (result != (int)LibRawInterop.LibRawErrors.LIBRAW_SUCCESS)
            {
                Log.Error($"LoadHalfSize: libraw_dcraw_process 失败，错误码: {result}");
                return null;
            }
            Log.Info("LoadHalfSize: libraw_dcraw_process 成功");

            ct.ThrowIfCancellationRequested();

            // 生成内存图像
            int errc;
            imgPtr = LibRawInterop.libraw_dcraw_make_mem_image(lr, out errc);
            if (imgPtr == IntPtr.Zero || errc != (int)LibRawInterop.LibRawErrors.LIBRAW_SUCCESS)
            {
                Log.Error($"LoadHalfSize: libraw_dcraw_make_mem_image 失败，imgPtr={imgPtr}, errc={errc}");
                return null;
            }
            Log.Info("LoadHalfSize: libraw_dcraw_make_mem_image 成功");

            ct.ThrowIfCancellationRequested();

            // 转换为 BitmapSource
            var bitmap = ConvertToBitmapSource(imgPtr);
            if (bitmap == null)
            {
                Log.Error("LoadHalfSize: ConvertToBitmapSource 失败");
            }
            else
            {
                Log.Info($"LoadHalfSize: 转换成功，尺寸 {bitmap.PixelWidth}x{bitmap.PixelHeight}");
            }
            return bitmap;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (imgPtr != IntPtr.Zero)
                LibRawInterop.libraw_dcraw_clear_mem(imgPtr);
            if (lr != IntPtr.Zero)
                LibRawInterop.libraw_close(lr);
        }
    }

    // ==================== 第3档：LibRaw全尺寸16-bit解码 ====================

    /// <summary>
    /// 第3档：LibRaw全尺寸16-bit解码（高质量，后台进行）
    /// 用户放大到一定程度才触发
    /// </summary>
    public static BitmapSource? LoadFullSize(string filePath, CancellationToken ct = default)
    {
        IntPtr lr = IntPtr.Zero;
        IntPtr imgPtr = IntPtr.Zero;

        try
        {
            ct.ThrowIfCancellationRequested();

            lr = LibRawInterop.libraw_init(0);
            if (lr == IntPtr.Zero)
                return null;

            ct.ThrowIfCancellationRequested();

            // 打开文件
            int result = LibRawInterop.libraw_open_file(lr, filePath);
            if (result != (int)LibRawInterop.LibRawErrors.LIBRAW_SUCCESS)
                return null;

            ct.ThrowIfCancellationRequested();

            // 解包RAW数据
            result = LibRawInterop.libraw_unpack(lr);
            if (result != (int)LibRawInterop.LibRawErrors.LIBRAW_SUCCESS)
                return null;

            ct.ThrowIfCancellationRequested();

            // 设置全尺寸16-bit模式
            LibRawInterop.libraw_set_half_size(lr, 0);      // 全尺寸
            LibRawInterop.libraw_set_output_bps(lr, 16);    // 16-bit输出
            LibRawInterop.libraw_set_auto_wb(lr, 1);        // 自动白平衡
            LibRawInterop.libraw_set_camera_wb(lr, 1);      // 优先使用相机白平衡
            LibRawInterop.libraw_set_no_auto_bright(lr, 1); // 禁用自动亮度调整，保持原始曝光

            ct.ThrowIfCancellationRequested();

            // 处理图像（高质量处理，可能需要较长时间）
            result = LibRawInterop.libraw_dcraw_process(lr);
            if (result != (int)LibRawInterop.LibRawErrors.LIBRAW_SUCCESS)
                return null;

            ct.ThrowIfCancellationRequested();

            // 生成内存图像
            int errc;
            imgPtr = LibRawInterop.libraw_dcraw_make_mem_image(lr, out errc);
            if (imgPtr == IntPtr.Zero || errc != (int)LibRawInterop.LibRawErrors.LIBRAW_SUCCESS)
                return null;

            ct.ThrowIfCancellationRequested();

            // 转换为 BitmapSource
            return ConvertToBitmapSource(imgPtr);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (imgPtr != IntPtr.Zero)
                LibRawInterop.libraw_dcraw_clear_mem(imgPtr);
            if (lr != IntPtr.Zero)
                LibRawInterop.libraw_close(lr);
        }
    }

    // ==================== 辅助方法 ====================

    /// <summary>
    /// 获取RAW文件信息
    /// </summary>
    public static RawFileInfo? GetFileInfo(string filePath)
    {
        IntPtr lr = IntPtr.Zero;

        try
        {
            lr = LibRawInterop.libraw_init(0);
            if (lr == IntPtr.Zero)
                return null;

            int result = LibRawInterop.libraw_open_file(lr, filePath);
            if (result != (int)LibRawInterop.LibRawErrors.LIBRAW_SUCCESS)
                return null;

            int width = LibRawInterop.libraw_get_raw_width(lr);
            int height = LibRawInterop.libraw_get_raw_height(lr);
            int bits = LibRawInterop.libraw_get_bits(lr);
            int thumbWidth = LibRawInterop.libraw_get_thumbnail_width(lr);

            return new RawFileInfo(
                Width: width,
                Height: height,
                BitsPerChannel: bits,
                CameraMake: "Unknown",  // LibRaw C API 需要额外解析
                CameraModel: "Unknown",
                HasThumbnail: thumbWidth > 0
            );
        }
        catch
        {
            return null;
        }
        finally
        {
            if (lr != IntPtr.Zero)
                LibRawInterop.libraw_close(lr);
        }
    }

    /// <summary>
    /// 智能加载：根据需求自动选择档位
    /// </summary>
    public static BitmapSource? LoadSmart(string filePath, DecodeLevel level, CancellationToken ct = default)
    {
        return level switch
        {
            DecodeLevel.Thumbnail => LoadThumbnail(filePath),
            DecodeLevel.HalfSize => LoadHalfSize(filePath, ct),
            DecodeLevel.FullSize => LoadFullSize(filePath, ct),
            _ => LoadThumbnail(filePath)
        };
    }

    /// <summary>
    /// 将 LibRaw 处理后的图像转换为 BitmapSource
    /// </summary>
    private static BitmapSource? ConvertToBitmapSource(IntPtr imgPtr)
    {
        var info = LibRawInterop.GetProcessedImageInfo(imgPtr);
        byte[] data = LibRawInterop.ReadProcessedImageData(imgPtr);

        if (data.Length == 0)
            return null;

        if (info.type == LibRawInterop.LibRawImageType.LIBRAW_IMAGE_BITMAP)
        {
            return DecodeBitmapData(data, info.width, info.height, info.colors, info.bits);
        }
        else if (info.type == LibRawInterop.LibRawImageType.LIBRAW_IMAGE_JPEG)
        {
            return DecodeJpegData(data);
        }

        return null;
    }

    /// <summary>
    /// 解码位图数据
    /// </summary>
    private static BitmapSource? DecodeBitmapData(byte[] data, ushort width, ushort height, ushort colors, ushort bits)
    {
        try
        {
            PixelFormat format;
            int stride;

            if (colors == 3 && bits == 8)
            {
                format = PixelFormats.Rgb24;
                stride = width * 3;
            }
            else if (colors == 4 && bits == 8)
            {
                format = PixelFormats.Bgra32;
                stride = width * 4;
            }
            else if (colors == 3 && bits == 16)
            {
                // 16-bit RGB 需要转换为 8-bit 或特殊处理
                format = PixelFormats.Rgb48;
                stride = width * 6;
            }
            else
            {
                // 默认使用 RGB24
                format = PixelFormats.Rgb24;
                stride = width * 3;
            }

            var bitmap = BitmapSource.Create(
                width,
                height,
                96,
                96,
                format,
                null,
                data,
                stride
            );

            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 解码 JPEG 数据
    /// </summary>
    private static BitmapSource? DecodeJpegData(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
