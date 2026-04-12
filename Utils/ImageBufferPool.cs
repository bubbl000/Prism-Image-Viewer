using System.Buffers;

namespace ImageBrowser.Utils;

/// <summary>
/// 图像缓冲区池 - 系统化管理 ArrayPool，减少 GC 压力
/// </summary>
public static class ImageBufferPool
{
    // 小缓冲区 (64KB) - 用于缩略图、小图片
    public static readonly ArrayPool<byte> Small = ArrayPool<byte>.Create(64 * 1024, 100);

    // 中缓冲区 (1MB) - 用于普通图片 (1920x1080 ~ 4K)
    public static readonly ArrayPool<byte> Medium = ArrayPool<byte>.Create(1024 * 1024, 20);

    // 大缓冲区 (16MB) - 用于超大图片 (8K+、大型 PSD/PSB)
    public static readonly ArrayPool<byte> Large = ArrayPool<byte>.Create(16 * 1024 * 1024, 5);

    /// <summary>
    /// 根据所需大小获取合适的缓冲区
    /// </summary>
    public static byte[] Rent(int size)
    {
        if (size <= 64 * 1024)
            return Small.Rent(size);
        if (size <= 1024 * 1024)
            return Medium.Rent(size);
        return Large.Rent(size);
    }

    /// <summary>
    /// 归还缓冲区
    /// </summary>
    /// <param name="buffer">缓冲区</param>
    /// <param name="clearArray">是否清零（默认不清零，更快）</param>
    public static void Return(byte[] buffer, bool clearArray = false)
    {
        if (buffer == null) return;

        // 根据缓冲区大小判断归属哪个池
        if (buffer.Length <= 64 * 1024)
            Small.Return(buffer, clearArray);
        else if (buffer.Length <= 1024 * 1024)
            Medium.Return(buffer, clearArray);
        else
            Large.Return(buffer, clearArray);
    }

    /// <summary>
    /// 获取缓冲区时自动计算所需大小（根据图像尺寸）
    /// </summary>
    /// <param name="width">图像宽度</param>
    /// <param name="height">图像高度</param>
    /// <param name="bytesPerPixel">每像素字节数（默认 4 为 RGBA）</param>
    public static byte[] RentForImage(int width, int height, int bytesPerPixel = 4)
    {
        long size = (long)width * height * bytesPerPixel;
        if (size > int.MaxValue)
            throw new ArgumentException("图像尺寸过大，超出缓冲区支持范围");

        return Rent((int)size);
    }
}

/// <summary>
/// 缓冲区租用包装器 - 支持 using 语句自动归还
/// </summary>
public readonly struct BufferRental : IDisposable
{
    private readonly byte[] _buffer;
    private readonly bool _clearOnReturn;

    public byte[] Buffer => _buffer;
    public int Length => _buffer?.Length ?? 0;

    public BufferRental(int size, bool clearOnReturn = false)
    {
        _buffer = ImageBufferPool.Rent(size);
        _clearOnReturn = clearOnReturn;
    }

    public void Dispose()
    {
        if (_buffer != null)
        {
            ImageBufferPool.Return(_buffer, _clearOnReturn);
        }
    }

    // 隐式转换为 byte[]
    public static implicit operator byte[](BufferRental rental) => rental._buffer;
}
