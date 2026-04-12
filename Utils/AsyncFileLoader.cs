using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;

namespace ImageBrowser.Utils;

/// <summary>
/// 异步文件加载器 - 使用真正的异步 IO，避免阻塞线程池
/// 适用于大文件（200MB+）加载场景
/// </summary>
public static class AsyncFileLoader
{
    // 默认缓冲区大小 80KB（Windows 文件系统最佳块大小）
    private const int DefaultBufferSize = 81920;

    /// <summary>
    /// 异步读取整个文件到内存流
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>包含文件数据的 MemoryStream</returns>
    public static async Task<MemoryStream> ReadAllBytesAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException("文件不存在", filePath);

        // 小文件（< 1MB）使用快速路径
        if (fileInfo.Length < 1024 * 1024)
        {
            var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
            return new MemoryStream(bytes, writable: false);
        }

        // 大文件使用异步流式读取
        return await ReadLargeFileAsync(filePath, fileInfo.Length, cancellationToken);
    }

    /// <summary>
    /// 异步读取大文件（真正的异步 IO，不阻塞线程）
    /// </summary>
    private static async Task<MemoryStream> ReadLargeFileAsync(
        string filePath,
        long fileSize,
        CancellationToken cancellationToken)
    {
        // 预分配内存流（避免扩容）
        var memoryStream = new MemoryStream((int)Math.Min(fileSize, int.MaxValue));

        // 使用异步文件流
        await using var fileStream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 0, // 我们自己管理缓冲
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        // 从缓冲区池租用缓冲区
        byte[] buffer = ImageBufferPool.Medium.Rent(DefaultBufferSize);
        try
        {
            int read;
            while ((read = await fileStream.ReadAsync(
                buffer.AsMemory(0, DefaultBufferSize),
                cancellationToken).ConfigureAwait(false)) > 0)
            {
                await memoryStream.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ImageBufferPool.Medium.Return(buffer, clearArray: false);
        }

        memoryStream.Position = 0;
        return memoryStream;
    }

    /// <summary>
    /// 异步分块读取文件（适用于超大文件，不需要全部加载到内存）
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <param name="bufferSize">缓冲区大小</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(
        string filePath,
        int bufferSize = DefaultBufferSize,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var fileStream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 0,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        byte[] buffer = ImageBufferPool.Medium.Rent(bufferSize);
        try
        {
            int read;
            while ((read = await fileStream.ReadAsync(
                buffer.AsMemory(0, bufferSize),
                cancellationToken).ConfigureAwait(false)) > 0)
            {
                // 创建副本返回（因为缓冲区会被重用）
                var chunk = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                yield return chunk.AsMemory(0, read);
            }
        }
        finally
        {
            ImageBufferPool.Medium.Return(buffer, clearArray: false);
        }
    }

    /// <summary>
    /// 异步加载图片文件（自动选择最佳方式）
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>图片数据流</returns>
    public static async Task<Stream> LoadImageStreamAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException("文件不存在", filePath);

        // 超大文件（> 100MB）使用文件流直接读取，不缓存到内存
        if (fileInfo.Length > 100 * 1024 * 1024)
        {
            return new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: DefaultBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        // 中等文件使用异步读取到内存流
        return await ReadAllBytesAsync(filePath, cancellationToken);
    }
}
