using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using ImageBrowser.Services;

namespace ImageBrowser;

public partial class MainWindow
{
    // ─── 文件监视 ─────────────────────────────────────────────────
    private void StartFileWatcher(string folderPath)
    {
        StopFileWatcher();

        // 使用增强型 SmartFileWatcher（带防抖和批量处理）
        _fileWatcher = new SmartFileWatcher
        {
            Dispatcher = this.Dispatcher,  // 使用 WPF Dispatcher 同步到 UI 线程
            IncludeSubdirectories = false
        };

        // 订阅批量事件（已防抖）
        _fileWatcher.OnDeleted += OnFilesDeleted;
        _fileWatcher.OnRenamed += OnFilesRenamed;

        _fileWatcher.StartWatching(folderPath);
    }

    private void StopFileWatcher()
    {
        if (_fileWatcher != null)
        {
            _fileWatcher.OnDeleted -= OnFilesDeleted;
            _fileWatcher.OnRenamed -= OnFilesRenamed;
            _fileWatcher.StopWatching();
            _fileWatcher.Dispose();
            _fileWatcher = null;
        }
    }

    /// <summary>
    /// 批量处理文件删除事件（已防抖）
    /// </summary>
    private void OnFilesDeleted(object? sender, FileChangedBatchEventArgs e)
    {
        // 已经在 UI 线程（通过 SynchronizingObject）
        foreach (var deletedFile in e.Files)
        {
            _imageCache.Remove(deletedFile);   // 移除失效缓存
            int idx = _imageFiles.FindIndex(f =>
                string.Equals(f, deletedFile, StringComparison.OrdinalIgnoreCase));

            if (idx >= 0)
            {
                bool wasCurrentFile = (idx == _currentIndex);
                _imageFiles.RemoveAt(idx);
                if (idx < _currentIndex) _currentIndex--;

                if (_imageFiles.Count == 0)
                {
                    ClearImageState();
                }
                else if (wasCurrentFile)
                {
                    int newIndex = Math.Min(_currentIndex, _imageFiles.Count - 1);
                    ShowImage(newIndex);
                }
            }
        }

        // 批量事件只刷新一次缩略图
        if (e.Files.Count > 0 && _imageFiles.Count > 0)
        {
            ReloadThumbnails();
        }
    }

    /// <summary>
    /// 批量处理文件重命名事件（已防抖）
    /// </summary>
    private void OnFilesRenamed(object? sender, FileRenamedBatchEventArgs e)
    {
        // 已经在 UI 线程（通过 SynchronizingObject）
        bool needReloadThumbnails = false;

        foreach (var (oldPath, newPath) in e.RenamedFiles)
        {
            int idx = _imageFiles.FindIndex(f =>
                string.Equals(f, oldPath, StringComparison.OrdinalIgnoreCase));

            if (idx >= 0)
            {
                // 检查新文件名是否是支持的图片格式
                string ext = Path.GetExtension(newPath).ToLowerInvariant();
                if (SupportedExts.Contains(ext))
                {
                    _imageFiles[idx] = newPath;
                    if (idx == _currentIndex && MainImageViewer.Source is BitmapImage bmp)
                    {
                        UpdateTitleInfo(newPath, bmp);
                    }
                    needReloadThumbnails = true;
                }
                else
                {
                    // 新扩展名不支持，当作删除处理
                    _imageFiles.RemoveAt(idx);
                    needReloadThumbnails = true;

                    if (_imageFiles.Count == 0)
                    {
                        ClearImageState();
                    }
                    else
                    {
                        int newIndex = Math.Min(idx, _imageFiles.Count - 1);
                        ShowImage(newIndex);
                    }
                }
            }
            else
            {
                // 可能是新文件移入，刷新列表
                RefreshCurrentFolder();
                return;  // RefreshCurrentFolder 已经刷新，不需要再刷新
            }
        }

        // 批量事件只刷新一次缩略图
        if (needReloadThumbnails)
        {
            ReloadThumbnails();
        }
    }

    private void RefreshCurrentFolder()
    {
        if (_imageFiles.Count == 0) return;

        string? currentDir = Path.GetDirectoryName(_imageFiles[0]);
        if (string.IsNullOrEmpty(currentDir)) return;

        var currentFile = _currentIndex >= 0 && _currentIndex < _imageFiles.Count
            ? _imageFiles[_currentIndex]
            : null;

        var files = Directory.GetFiles(currentDir)
            .Where(f => SupportedExts.Contains(
                Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _imageFiles = files;
        ReloadThumbnails();

        if (currentFile != null)
        {
            int idx = files.FindIndex(f =>
                string.Equals(f, currentFile, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                ShowImage(idx);
            }
            else if (files.Count > 0)
            {
                ShowImage(0);
            }
            else
            {
                ClearImageState();
            }
        }
        else if (files.Count > 0)
        {
            ShowImage(0);
        }
        else
        {
            ClearImageState();
        }
    }
}
