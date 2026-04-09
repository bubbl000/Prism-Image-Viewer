using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace ImageBrowser.Models;

/// <summary>
/// 图片项模型
/// </summary>
public class ImageItem
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName => Path.GetFileName(FilePath);
    public string Directory => Path.GetDirectoryName(FilePath) ?? string.Empty;
    public long FileSize { get; set; }
    public DateTime LastWriteTime { get; set; }
    
    // 图像信息
    public int Width { get; set; }
    public int Height { get; set; }
    public int FrameCount { get; set; } = 1;
    public int CurrentFrame { get; set; } = 0;
    
    // 缩略图
    public BitmapSource? Thumbnail { get; set; }
    
    // 是否已加载
    public bool IsLoaded => Thumbnail != null;
    
    // 文件扩展名
    public string Extension => Path.GetExtension(FilePath).ToLowerInvariant();
    
    // 格式化文件大小
    public string FormattedFileSize
    {
        get
        {
            const long KB = 1024;
            const long MB = KB * 1024;
            const long GB = MB * 1024;
            
            return FileSize switch
            {
                >= GB => $"{FileSize / (double)GB:F2} GB",
                >= MB => $"{FileSize / (double)MB:F2} MB",
                >= KB => $"{FileSize / (double)KB:F2} KB",
                _ => $"{FileSize} B"
            };
        }
    }
    
    // 格式化尺寸
    public string FormattedDimensions => $"{Width} x {Height}";
}
