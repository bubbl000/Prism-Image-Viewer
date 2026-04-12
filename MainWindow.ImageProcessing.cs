using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageBrowser.Utils;

namespace ImageBrowser;

public partial class MainWindow
{
    // ─── 专业格式缓存目录 ──────────────────────────────────────
    private static readonly string _professionalCacheDir = 
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PrismImageViewer", "ProfessionalCache");

    // ─── 专业格式加载（使用 Magick.NET + 异步 IO + 缓存优化）─────────────────
    private static async Task<BitmapSource> LoadProfessionalImageAsync(string filePath)
    {
        try
        {
            // 生成缓存路径
            string cacheKey = Path.GetFileNameWithoutExtension(filePath) + "_" + 
                              new FileInfo(filePath).Length.GetHashCode().ToString("X");
            string cachePath = Path.Combine(_professionalCacheDir, cacheKey + ".jpg");

            // 检查缓存是否有效
            if (File.Exists(cachePath) && 
                File.GetLastWriteTime(cachePath) > File.GetLastWriteTime(filePath))
            {
                // 缓存命中，直接加载缓存文件（非常快）
                return LoadStandardImage(cachePath);
            }

            // 使用异步加载大文件（真正的异步 IO，不阻塞线程池）
            using var fileStream = await AsyncFileLoader.LoadImageStreamAsync(filePath);
            
            // 使用超时机制防止无限阻塞（10秒超时）
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            
            // 在独立任务中执行 Magick.NET 操作（CPU 密集型）
            var loadTask = Task.Run(() =>
            {
                using (var image = new ImageMagick.MagickImage())
                {
                    // 设置图像处理配置
                    image.Settings.SetDefine("psd:maximum-size", "8192x8192"); // 限制最大尺寸
                    image.Settings.Compression = ImageMagick.CompressionMethod.Zip; // 优化压缩
                    
                    // 检查取消令牌
                    timeoutCts.Token.ThrowIfCancellationRequested();
                    
                    // 从流读取文件（支持异步加载的大文件）
                    image.Read(fileStream);
                    
                    // 关键优化：大图像自动缩小到合理尺寸（4K以内）
                    if (image.Width > 4000 || image.Height > 4000)
                    {
                        // 按比例缩放到 4K 以内
                        int newWidth = Math.Min(4000, (int)image.Width);
                        int newHeight = (int)((long)image.Height * newWidth / image.Width);
                        
                        // 使用 Geometry 对象进行缩放（使用 uint 类型）
                        var geometry = new ImageMagick.MagickGeometry((uint)newWidth, (uint)newHeight)
                        {
                            IgnoreAspectRatio = false
                        };
                        image.Resize(geometry);
                    }

                    // 创建缓存目录（如果不存在）
                    try
                    {
                        if (!Directory.Exists(_professionalCacheDir))
                        {
                            Directory.CreateDirectory(_professionalCacheDir);
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // 如果无法创建缓存目录，直接使用内存加载而不缓存
                        image.Format = ImageMagick.MagickFormat.Jpeg;
                        image.Quality = 95;
                        
                        // 使用 ArrayPool 减少内存分配
                        using var memStream = new MemoryStream();
                        image.Write(memStream);
                        memStream.Position = 0;
                        
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.StreamSource = memStream;
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.EndInit();
                        bitmap.Freeze();
                        return bitmap;
                    }
                    
                    // 关键优化：使用 JPEG 作为中间格式（速度快 3-5 倍）
                    image.Format = ImageMagick.MagickFormat.Jpeg;
                    image.Quality = 95;  // 高质量
                    
                    // 生成缓存文件
                    image.Write(cachePath);
                    
                    // 从缓存文件加载（确保一致性）
                    return LoadStandardImage(cachePath);
                }
            }, timeoutCts.Token);

            // 等待任务完成，支持超时
            if (await Task.WhenAny(loadTask, Task.Delay(TimeSpan.FromSeconds(10), timeoutCts.Token)) == loadTask)
            {
                return await loadTask;
            }
            else
            {
                timeoutCts.Cancel();
                throw new TimeoutException($"加载专业格式文件超时 ({Path.GetExtension(filePath)})");
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"加载专业格式文件被取消 ({Path.GetExtension(filePath)})");
        }
        catch (ImageMagick.MagickException magickEx)
        {
            // Magick.NET 特定错误处理
            throw new NotSupportedException($"Magick.NET 无法读取专业格式文件 ({Path.GetExtension(filePath)}): {magickEx.Message}");
        }
        catch (Exception ex)
        {
            throw new NotSupportedException($"无法读取专业格式文件 ({Path.GetExtension(filePath)}): {ex.Message}");
        }
    }

    // ─── 同步包装方法（保持兼容性）─────────────────────────────
    private static BitmapSource LoadProfessionalImage(string filePath)
    {
        // 大文件（> 50MB）使用真正的异步 IO 避免阻塞线程池
        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length > 50 * 1024 * 1024)
        {
            return LoadProfessionalImageAsync(filePath).GetAwaiter().GetResult();
        }
        
        // 小文件使用原有同步方式（更快）
        return LoadProfessionalImageSync(filePath);
    }

    // ─── 同步版本（用于小文件）─────────────────────────────
    private static BitmapSource LoadProfessionalImageSync(string filePath)
    {
        try
        {
            string cacheKey = Path.GetFileNameWithoutExtension(filePath) + "_" + 
                              new FileInfo(filePath).Length.GetHashCode().ToString("X");
            string cachePath = Path.Combine(_professionalCacheDir, cacheKey + ".jpg");

            if (File.Exists(cachePath) && 
                File.GetLastWriteTime(cachePath) > File.GetLastWriteTime(filePath))
            {
                return LoadStandardImage(cachePath);
            }

            using var image = new ImageMagick.MagickImage();
            image.Settings.SetDefine("psd:maximum-size", "8192x8192");
            image.Settings.Compression = ImageMagick.CompressionMethod.Zip;
            image.Read(filePath);
            
            if (image.Width > 4000 || image.Height > 4000)
            {
                int newWidth = Math.Min(4000, (int)image.Width);
                int newHeight = (int)((long)image.Height * newWidth / image.Width);
                var geometry = new ImageMagick.MagickGeometry((uint)newWidth, (uint)newHeight)
                {
                    IgnoreAspectRatio = false
                };
                image.Resize(geometry);
            }

            try
            {
                if (!Directory.Exists(_professionalCacheDir))
                {
                    Directory.CreateDirectory(_professionalCacheDir);
                }
            }
            catch (UnauthorizedAccessException)
            {
                image.Format = ImageMagick.MagickFormat.Jpeg;
                image.Quality = 95;
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.StreamSource = new MemoryStream(image.ToByteArray());
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            
            image.Format = ImageMagick.MagickFormat.Jpeg;
            image.Quality = 95;
            image.Write(cachePath);
            return LoadStandardImage(cachePath);
        }
        catch (Exception ex)
        {
            throw new NotSupportedException($"无法读取专业格式文件 ({Path.GetExtension(filePath)}): {ex.Message}");
        }
    }

    // ─── 标准格式加载（用于缓存文件）─────────────────────────────
    private static BitmapSource LoadStandardImage(string filePath)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(filePath);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    // ─── 专业格式缩略图加载（使用 Magick.NET + 缓存优化）─────────────
    private static BitmapSource? LoadProfessionalThumbnail(string filePath)
    {
        try
        {
            // 生成缩略图缓存路径
            string cacheKey = Path.GetFileNameWithoutExtension(filePath) + "_thumb_" + 
                              new FileInfo(filePath).Length.GetHashCode().ToString("X");
            string cachePath = Path.Combine(_professionalCacheDir, cacheKey + ".jpg");

            // 检查缓存是否有效
            if (File.Exists(cachePath) && 
                File.GetLastWriteTime(cachePath) > File.GetLastWriteTime(filePath))
            {
                // 缓存命中，直接加载缓存文件
                return LoadStandardImage(cachePath);
            }

            // 使用超时机制防止无限阻塞（5秒超时）
            var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            
            // 在独立任务中执行 Magick.NET 操作
            var loadTask = Task.Run(() =>
            {
                using (var image = new ImageMagick.MagickImage())
                {
                    // 设置缩略图专用配置
                    image.Settings.SetDefine("psd:maximum-size", "200x200"); // 限制最大尺寸
                    image.Settings.Compression = ImageMagick.CompressionMethod.Zip; // 优化压缩
                    
                    // 检查取消令牌
                    timeoutCts.Token.ThrowIfCancellationRequested();
                    
                    // 读取文件
                    image.Read(filePath);
                    
                    // 缩略图专用处理
                    image.Thumbnail(60, 60); // 缩放到 60px 高度
                    
                    // 创建缓存目录
                    Directory.CreateDirectory(_professionalCacheDir);
                    
                    // 关键优化：使用 JPEG 作为中间格式
                    image.Format = ImageMagick.MagickFormat.Jpeg;
                    image.Quality = 80;  // 缩略图质量稍低
                    
                    // 生成缓存文件
                    image.Write(cachePath);
                    
                    // 从缓存文件加载
                    return LoadStandardImage(cachePath);
                }
            }, timeoutCts.Token);

            // 等待任务完成，支持超时
            if (loadTask.Wait(TimeSpan.FromSeconds(5)))
            {
                return loadTask.Result;
            }
            else
            {
                timeoutCts.Cancel();
                return null; // 缩略图加载超时，静默返回 null
            }
        }
        catch (OperationCanceledException)
        {
            return null; // 缩略图加载被取消，静默返回 null
        }
        catch (ImageMagick.MagickException)
        {
            // 缩略图加载失败时静默处理，返回 null
            return null;
        }
        catch (Exception)
        {
            // 其他异常也静默处理
            return null;
        }
    }

    // ─── RAW 内嵌预览提取 ─────────────────────────────────────────
    // 扫描文件找到最后一个 JPEG 起始标记（FF D8 FF），提取完整 JPEG 数据。
    // 大多数相机 RAW 格式在文件尾部内嵌一张全尺寸 JPEG 预览。
    private static byte[]? TryExtractRawPreview(string path)
    {
        try
        {
            byte[] data = System.IO.File.ReadAllBytes(path);
            int lastStart = -1;
            for (int i = 0; i < data.Length - 3; i++)
            {
                if (data[i] == 0xFF && data[i + 1] == 0xD8 && data[i + 2] == 0xFF)
                    lastStart = i;
            }
            if (lastStart < 0) return null;
            for (int j = lastStart + 2; j < data.Length - 1; j++)
            {
                if (data[j] == 0xFF && data[j + 1] == 0xD9)
                    return data[lastStart..(j + 2)];
            }
        }
        catch { }
        return null;
    }

    // ─── PSD/PSB 全分辨率合并图层解码 ────────────────────────────
    // 解码文件末尾 Image Data 块（扁平合成图像），支持：
    //   色彩模式：RGB / 灰度 / CMYK
    //   位深度：8-bit / 16-bit（16-bit 取高字节，视觉无差异）
    //   压缩方式：Raw(0) / RLE PackBits(1) / ZIP(2) / ZIP+预测(3)
    private static BitmapSource? TryDecodePsdComposite(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);

            // ── Header ──────────────────────────────────────────────
            byte[] sig = br.ReadBytes(4);
            if (sig[0] != '8' || sig[1] != 'B' || sig[2] != 'P' || sig[3] != 'S') return null;
            ushort version  = PsdReadU16(br);           // 1=PSD  2=PSB
            if (version != 1 && version != 2) return null;
            br.ReadBytes(6);                            // reserved
            int channels  = PsdReadU16(br);
            int height    = (int)PsdReadU32(br);
            int width     = (int)PsdReadU32(br);
            int bitDepth  = PsdReadU16(br);             // bits per channel
            int colorMode = PsdReadU16(br);             // 2=Gray 4=RGB 5=CMYK

            if (bitDepth  != 8 && bitDepth  != 16) return null;
            if (colorMode != 2 && colorMode != 4 && colorMode != 5) return null;

            // ── 跳过 Color Mode Data / Image Resources ───────────────
            fs.Seek(PsdReadU32(br), SeekOrigin.Current);
            fs.Seek(PsdReadU32(br), SeekOrigin.Current);

            // ── 跳过 Layer and Mask Information ─────────────────────
            long layerLen = version == 2 ? (long)PsdReadU64(br) : (long)PsdReadU32(br);
            fs.Seek(layerLen, SeekOrigin.Current);

            // ── Image Data ───────────────────────────────────────────
            ushort compression = PsdReadU16(br);
            if (compression > 3) return null;

            int bps          = bitDepth / 8;            // bytes per sample
            int numColorCh   = colorMode == 2 ? 1 : (colorMode == 5 ? 4 : 3);
            if (channels < numColorCh) return null;
            bool hasAlpha    = channels > numColorCh;
            int  decodeCh    = numColorCh + (hasAlpha ? 1 : 0);
            int  rowBytes    = width * bps;
            int  channelSize = height * rowBytes;

            byte[][] ch = new byte[decodeCh][];

            if (compression == 1)   // RLE PackBits
            {
                int rcSize = version == 2 ? 4 : 2;
                int[] rc   = new int[channels * height];
                for (int i = 0; i < rc.Length; i++)
                    rc[i] = rcSize == 4 ? (int)PsdReadU32(br) : (int)PsdReadU16(br);

                for (int c = 0; c < channels; c++)
                {
                    if (c < decodeCh)
                    {
                        ch[c] = new byte[channelSize];
                        int off = 0;
                        for (int row = 0; row < height; row++)
                        {
                            byte[] comp = br.ReadBytes(rc[c * height + row]);
                            off += PackBitsDecode(comp, ch[c], off, rowBytes);
                        }
                    }
                    else
                    {
                        for (int row = 0; row < height; row++)
                            fs.Seek(rc[c * height + row], SeekOrigin.Current);
                    }
                }
            }
            else if (compression == 2 || compression == 3)   // ZIP（type2=无预测 type3=有预测）
            {
                // 剩余文件内容为单一 zlib 流，按通道顺序存放所有像素数据
                using var decompMs = new System.IO.MemoryStream();
                using (var zlib = new System.IO.Compression.ZLibStream(
                           fs, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true))
                    zlib.CopyTo(decompMs);

                byte[] allData = decompMs.ToArray();
                if (allData.Length < decodeCh * channelSize) return null;

                for (int c = 0; c < decodeCh; c++)
                {
                    ch[c] = new byte[channelSize];
                    Buffer.BlockCopy(allData, c * channelSize, ch[c], 0, channelSize);
                }

                // ZIP with prediction (type3)：逐行 undo delta（字节级别，与位深无关）
                if (compression == 3)
                {
                    for (int c = 0; c < decodeCh; c++)
                        for (int row = 0; row < height; row++)
                        {
                            int off = row * rowBytes;
                            for (int col = 1; col < rowBytes; col++)
                                ch[c][off + col] = (byte)(ch[c][off + col] + ch[c][off + col - 1]);
                        }
                }
            }
            else    // Raw (compression == 0)
            {
                for (int c = 0; c < channels; c++)
                {
                    if (c < decodeCh) ch[c] = br.ReadBytes(channelSize);
                    else              fs.Seek(channelSize, SeekOrigin.Current);
                }
            }

            // ── 组装 BGRA 像素 ───────────────────────────────────────
            byte[] pixels = new byte[width * height * 4];
            bool isGray   = colorMode == 2;
            bool isCmyk   = colorMode == 5;

            for (int i = 0; i < width * height; i++)
            {
                int s = i * bps;    // 16-bit 时 s = i*2，取高字节即可
                byte r, g, b, a = 255;

                if (isGray)
                {
                    r = g = b = ch[0][s];
                }
                else if (isCmyk)
                {
                    // PSD CMYK 存为 255 - 实际油墨量
                    float cv = (255 - ch[0][s]) / 255f;
                    float mv = (255 - ch[1][s]) / 255f;
                    float yv = (255 - ch[2][s]) / 255f;
                    float kv = (255 - ch[3][s]) / 255f;
                    r = (byte)((1f - Math.Min(1f, cv + kv)) * 255);
                    g = (byte)((1f - Math.Min(1f, mv + kv)) * 255);
                    b = (byte)((1f - Math.Min(1f, yv + kv)) * 255);
                    if (hasAlpha && decodeCh >= 5) a = ch[4][s];
                }
                else    // RGB
                {
                    r = ch[0][s]; g = ch[1][s]; b = ch[2][s];
                    if (hasAlpha) a = ch[3][s];
                }

                int p = i * 4;
                pixels[p] = b; pixels[p+1] = g; pixels[p+2] = r; pixels[p+3] = a;
            }

            var bmpSrc = BitmapSource.Create(width, height, 96, 96,
                PixelFormats.Bgra32, null, pixels, width * 4);
            bmpSrc.Freeze();
            return bmpSrc;
        }
        catch { return null; }
    }

    private static int PackBitsDecode(byte[] src, byte[] dst, int dstOff, int expectedBytes)
    {
        int si = 0, di = 0;
        while (si < src.Length && di < expectedBytes)
        {
            sbyte n = (sbyte)src[si++];
            if (n >= 0)
            {
                int count = n + 1;
                Buffer.BlockCopy(src, si, dst, dstOff + di, count);
                si += count; di += count;
            }
            else if (n != -128)
            {
                int count = -n + 1;
                new Span<byte>(dst, dstOff + di, count).Fill(src[si++]);
                di += count;
            }
        }
        return di;
    }

    private static ulong PsdReadU64(BinaryReader br)
    {
        ulong v = 0;
        for (int i = 0; i < 8; i++) v = (v << 8) | br.ReadByte();
        return v;
    }

    private static ushort PsdReadU16(BinaryReader br)
    {
        byte b0 = br.ReadByte(), b1 = br.ReadByte();
        return (ushort)((b0 << 8) | b1);
    }

    private static uint PsdReadU32(BinaryReader br)
    {
        byte b0 = br.ReadByte(), b1 = br.ReadByte(),
             b2 = br.ReadByte(), b3 = br.ReadByte();
        return (uint)((b0 << 24) | (b1 << 16) | (b2 << 8) | b3);
    }

    // ─── WIC 解码（UI 线程外调用，可复用于 ShowImage 和 StartPrefetch）─
    private static BitmapSource LoadBitmapSource(string filePath, bool isRaw)
    {
        // RAW 文件使用 Magick.NET 解码
        if (isRaw || filePath.EndsWith(".cr3", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return LoadProfessionalImage(filePath);
            }
            catch (Exception ex)
            {
                // Magick.NET 无法读取时，尝试使用 WIC 内置解码器
                System.Diagnostics.Debug.WriteLine($"Magick.NET 无法读取 {filePath}: {ex.Message}，尝试 WIC 解码");
                
                // 尝试使用 Windows 内置的 WIC 解码器（可能支持某些 Magick.NET 不支持的格式变体）
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(filePath);
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }
                catch
                {
                    // 如果 WIC 也失败，抛出原始错误
                    throw;
                }
            }
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource   = new Uri(filePath);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    // ── 后台预读取相邻图片（+1, +2, -1）──────────────────────────
    private void StartPrefetch(int centerIndex)
    {
        _prefetchCts?.Cancel();
        _prefetchCts = new CancellationTokenSource();
        var ct   = _prefetchCts.Token;
        var files = _imageFiles.ToList();

        // 预读顺序：前后各3张，方向对称；GIF 跳过（需特殊构造）
        var candidates = new[] { +1, +2, +3, -1, -2, -3 }
            .Select(d => centerIndex + d)
            .Where(i => i >= 0 && i < files.Count)
            .Where(i => !files[i].EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            .Where(i => !_imageCache.Contains(files[i]))
            .Take(6)   // 最多同时预取6张
            .Select(i => files[i])
            .ToList();

        if (candidates.Count == 0) return;

        _ = Task.Run(() =>
        {
            foreach (string path in candidates)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    BitmapSource? bmp = null;
                    bool isRaw = RawLoader.IsRawFile(path);
                    bool isPsd = path.EndsWith(".psd", StringComparison.OrdinalIgnoreCase);
                    bool isPsb = path.EndsWith(".psb", StringComparison.OrdinalIgnoreCase);

                    if (isPsd || isPsb)
                    {
                        // PSD/PSB 使用 Aspose.PSD 预取
                        bmp = PsdLoader.LoadImage(path, ct);
                    }
                    else if (isRaw)
                    {
                        // RAW 文件使用 LibRaw 预取（半尺寸模式）
                        bmp = RawLoader.LoadHalfSize(path, ct);
                    }
                    else
                    {
                        bmp = LoadBitmapSource(path, isRaw);
                    }

                    if (bmp != null && !ct.IsCancellationRequested)
                        Dispatcher.Invoke(() => _imageCache.Put(path, bmp));
                }
                catch { }
            }
        }, ct);
    }
}
