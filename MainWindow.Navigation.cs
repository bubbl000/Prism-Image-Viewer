using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace ImageBrowser;

public partial class MainWindow
{
    // ─── 更新导航按钮可见性 ───────────────────────────────────────
    private void UpdateNavButtons()
    {
        bool multi = _imageFiles.Count > 1;
        BtnPrev.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        BtnNext.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
    }

    // ─── 左右导航 ─────────────────────────────────────────────────
    private void BtnPrev_Click(object sender, RoutedEventArgs e)
    {
        if (_imageFiles.Count == 0) return;
        if (_currentIndex == 0 && _folderTraverse)
        {
            TryEnterAdjacentFolder(forward: false);
            return;
        }
        int idx = (_currentIndex - 1 + _imageFiles.Count) % _imageFiles.Count;
        EnqueueNavigation(idx);
    }

    private void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        if (_imageFiles.Count == 0) return;
        if (_currentIndex == _imageFiles.Count - 1 && _folderTraverse)
        {
            TryEnterAdjacentFolder(forward: true);
            return;
        }
        int idx = (_currentIndex + 1) % _imageFiles.Count;
        EnqueueNavigation(idx);
    }

    /// <summary>
    /// 导航入队——连续按键时合并请求，直接跳到最新的
    /// </summary>
    private void EnqueueNavigation(int index)
    {
        _navigationQueue.Enqueue(index);
        
        // 触发处理（如果没在处理就启动）
        if (_navCts == null || _navCts.IsCancellationRequested)
        {
            _navCts = new CancellationTokenSource();
            _ = ProcessNavigationQueueAsync(_navCts.Token);
        }
    }

    /// <summary>
    /// 处理导航队列——连续按键时跳过中间图片，直接显示最新的
    /// </summary>
    private async Task ProcessNavigationQueueAsync(CancellationToken ct)
    {
        int lastIndex = -1;
        
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // 清空队列，只保留最后一个
                while (_navigationQueue.TryDequeue(out int idx))
                {
                    lastIndex = idx;
                }
                
                if (lastIndex >= 0)
                {
                    // 显示最后一张请求的图片
                    ShowImage(lastIndex);
                    lastIndex = -1;
                    
                    // 留点时间让 ShowImage 完成
                    await Task.Delay(16, ct);
                }
                else
                {
                    // 队列空了，退出循环
                    break;
                }
            }
        }
        finally
        {
            // 处理完成后重置 CTS，允许下次启动新任务
            _navCts = null;
        }
    }

    // ─── 键盘导航 ─────────────────────────────────────────────────
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (_isFullScreen)
                {
                    ExitFullScreen();
                }
                else if (WindowState == WindowState.Maximized)
                {
                    WindowState = WindowState.Normal;
                }
                break;
            case Key.Left:
            case Key.PageUp:
                BtnPrev_Click(sender, e);
                break;
            case Key.Right:
            case Key.PageDown:
                BtnNext_Click(sender, e);
                break;
        }
    }

    // ─── 滚轮模式 ─────────────────────────────────────────────────
    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        string mode = AppSettings.Current.WheelMode;
        if (mode == "zoom") return; // 让 ZoomBorder 处理

        // 仅当鼠标在图片区域内时拦截
        Point pos = e.GetPosition(ZoomBorder);
        if (pos.X < 0 || pos.Y < 0 ||
            pos.X > ZoomBorder.ActualWidth || pos.Y > ZoomBorder.ActualHeight) return;

        if (mode == "page")
        {
            if (e.Delta < 0) BtnNext_Click(sender, e);
            else             BtnPrev_Click(sender, e);
            e.Handled = true;
        }
        // "scroll" 模式交给 ZoomBorder 默认平移处理
    }

    // ─── 文件夹穿透：进入上/下一个兄弟文件夹 ────────────────────────
    private void TryEnterAdjacentFolder(bool forward)
    {
        if (_imageFiles.Count == 0) return;
        string currentDir = Path.GetDirectoryName(_imageFiles[0]) ?? "";
        string parentDir  = Path.GetDirectoryName(currentDir) ?? "";
        if (string.IsNullOrEmpty(parentDir)) return;

        // 同级所有文件夹，按名称排序
        var siblings = Directory.GetDirectories(parentDir)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();
        int ci = siblings.FindIndex(d => string.Equals(d, currentDir, StringComparison.OrdinalIgnoreCase));
        if (ci < 0) return;

        string msg = forward ? "已是最后一张，是否进入下一个文件夹？" : "已是第一张，是否进入上一个文件夹？";
        var dlg = new ConfirmDialog("文件夹穿透", msg) { Owner = this };
        dlg.ShowDialog();
        if (!dlg.Confirmed) return;

        // 向前或向后依次查找有图片的文件夹（自动跳过无图片的）
        int step = forward ? 1 : -1;
        for (int i = ci + step; i >= 0 && i < siblings.Count; i += step)
        {
            var files = Directory.GetFiles(siblings[i])
                .Where(f => SupportedExts.Contains(
                    Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (files.Count == 0) continue;

            _imageFiles = files;
            ReloadThumbnails();
            ShowImage(forward ? 0 : files.Count - 1);
            return;
        }

        // 无相邻文件夹时，循环回到第一个/最后一个有图片的文件夹
        var wrapFiles = FindFirstOrLastFolderWithImages(siblings, forward);
        if (wrapFiles != null && wrapFiles.Count > 0)
        {
            _imageFiles = wrapFiles;
            ReloadThumbnails();
            ShowImage(forward ? 0 : wrapFiles.Count - 1);
        }
    }

    // 查找第一个或最后一个包含图片的文件夹
    private List<string>? FindFirstOrLastFolderWithImages(List<string> siblings, bool findFirst)
    {
        if (findFirst)
        {
            // 找第一个有图片的文件夹
            foreach (var dir in siblings)
            {
                var files = Directory.GetFiles(dir)
                    .Where(f => SupportedExts.Contains(
                        Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (files.Count > 0) return files;
            }
        }
        else
        {
            // 找最后一个有图片的文件夹
            for (int i = siblings.Count - 1; i >= 0; i--)
            {
                var files = Directory.GetFiles(siblings[i])
                    .Where(f => SupportedExts.Contains(
                        Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (files.Count > 0) return files;
            }
        }
        return null;
    }

    // ─── Ctrl+拖拽：拖出图片到其他程序 ───────────────────────────
    private void ZoomBorder_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(ZoomBorder);
        _dragCurrentPoint = _dragStartPoint;
        _isDragging = false;
        _isExternalDrag = false;
    }

    private void ZoomBorder_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (MainImageViewer.Source == null) return;
        if (_isDragging) return;
        if (_currentIndex < 0 || _currentIndex >= _imageFiles.Count) return;

        Point pos = e.GetPosition(ZoomBorder);
        _dragCurrentPoint = pos;

        bool ctrlHeld = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);

        if (ctrlHeld)
        {
            // Ctrl + 拖拽：移动超过 8px 即触发（原有行为）
            double dx = pos.X - _dragStartPoint.X;
            double dy = pos.Y - _dragStartPoint.Y;
            if (Math.Sqrt(dx * dx + dy * dy) < 8) return;
        }
        else
        {
            // 无 Ctrl：鼠标拖出窗口范围外才触发（不干扰窗口内的平移操作）
            Point winPos = e.GetPosition(this);
            bool isOutside = winPos.X < -20 || winPos.Y < -20 ||
                             winPos.X > ActualWidth + 20 || winPos.Y > ActualHeight + 20;
            if (!isOutside) return;
        }

        BtnPrev.Visibility = Visibility.Collapsed;
        BtnNext.Visibility = Visibility.Collapsed;

        _isDragging = true;
        _isExternalDrag = true;
        var data = new DataObject(DataFormats.FileDrop, new string[] { _imageFiles[_currentIndex] });
        DragDrop.DoDragDrop(ZoomBorder, data, DragDropEffects.Copy | DragDropEffects.Move);

        _isDragging = false;
        _isExternalDrag = false;
        UpdateNavButtons();
        ZoomBorder.Uniform();
    }

    private void ZoomBorder_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 如果不是跨软件拖拽，恢复适应窗口居中
        if (!_isExternalDrag && _isDragging)
        {
            ZoomBorder.Uniform();
        }
        _isDragging = false;
        _isExternalDrag = false;
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isGifPanelDragging && e.LeftButton == MouseButtonState.Pressed)
        {
            Point currentPosition = e.GetPosition(this);
            
            // 计算偏移量
            double deltaX = currentPosition.X - _gifPanelDragStartPoint.X;
            double deltaY = currentPosition.Y - _gifPanelDragStartPoint.Y;

            // 计算新的位置
            double newLeft = _gifPanelStartPosition.X + deltaX;
            double newTop = _gifPanelStartPosition.Y + deltaY;

            // 限制在窗口范围内
            newLeft = Math.Max(0, Math.Min(newLeft, ActualWidth - GifPanel.ActualWidth));
            newTop = Math.Max(0, Math.Min(newTop, ActualHeight - GifPanel.ActualHeight));

            // 更新位置
            GifPanel.HorizontalAlignment = HorizontalAlignment.Left;
            GifPanel.VerticalAlignment = VerticalAlignment.Top;
            GifPanel.Margin = new Thickness(newLeft, newTop, 0, 0);
        }
        else if (_isGifPanelDragging && e.LeftButton == MouseButtonState.Released)
        {
            _isGifPanelDragging = false;
            GifPanel.ReleaseMouseCapture();
        }
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isGifPanelDragging)
        {
            _isGifPanelDragging = false;
            GifPanel.ReleaseMouseCapture();
        }
    }
}
