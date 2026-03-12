using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace ImageViewer;

/// <summary>
/// 缩略图磁盘缓存
/// - 路径：%TEMP%\ImageViewerThumbCache\
/// - Key：MD5(filePath + lastWriteTime)，避免旧缓存命中
/// - 软件关闭时写入 .lastuse 标记；下次启动若超过指定分钟则清空全部缓存
/// </summary>
internal static class ThumbnailCache
{
    private static readonly string CacheDir  = Path.Combine(Path.GetTempPath(), "ImageViewerThumbCache");
    private static readonly string MarkerFile = Path.Combine(CacheDir, ".lastuse");

    // ── 启动时调用：超时则清空缓存 ──────────────────────────────
    public static void CheckAndClean(int expireMinutes = 30)
    {
        try
        {
            if (!Directory.Exists(CacheDir)) return;

            if (!File.Exists(MarkerFile))
            {
                CleanAll();
                return;
            }
            if ((DateTime.Now - File.GetLastWriteTime(MarkerFile)).TotalMinutes > expireMinutes)
                CleanAll();
        }
        catch { }
    }

    // ── 关闭时调用：刷新 .lastuse 时间戳 ────────────────────────
    public static void TouchMarker()
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(MarkerFile, DateTime.Now.ToString("O"));
        }
        catch { }
    }

    // ── 读取缓存缩略图 ───────────────────────────────────────────
    public static BitmapSource? TryLoad(string filePath)
    {
        try
        {
            string cacheFile = GetCachePath(filePath);
            if (!File.Exists(cacheFile)) return null;

            using var fs = new FileStream(cacheFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = fs;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    // ── 写入缓存缩略图 ───────────────────────────────────────────
    public static void TrySave(string filePath, BitmapSource thumb)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            string cacheFile = GetCachePath(filePath);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(thumb));
            using var fs = new FileStream(cacheFile, FileMode.Create, FileAccess.Write, FileShare.None);
            enc.Save(fs);
        }
        catch { }
    }

    // ── Key = MD5(路径 + 修改时间) ────────────────────────────────
    private static string GetCachePath(string filePath)
    {
        long mtime = File.Exists(filePath) ? File.GetLastWriteTime(filePath).Ticks : 0L;
        string key  = $"{filePath}|{mtime}";
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(CacheDir, Convert.ToHexString(hash) + ".png");
    }

    private static void CleanAll()
    {
        try { if (Directory.Exists(CacheDir)) Directory.Delete(CacheDir, recursive: true); }
        catch { }
    }
}
