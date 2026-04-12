using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImageBrowser.Services;
using ImageBrowser.Utils;

namespace ImageBrowser;

public partial class MainWindow
{
    // ─── 打开文件按钮 ─────────────────────────────────────────────
    private void BtnOpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择图片",
            Filter = $"图片文件|{string.Join(";", SupportedExts.Select(x => $"*{x}"))}|所有文件|*.*"
        };

        if (dialog.ShowDialog() == true)
            LoadFromPath(dialog.FileName);
    }

    // ─── 拖放支持 ─────────────────────────────────────────────────
    private bool _isDragOver = false;

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            _isDragOver = true;
            // 只在隐藏时才显示，避免闪烁
            if (DropOverlay.Visibility != Visibility.Visible)
            {
                DropOverlay.Visibility = Visibility.Visible;
            }
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        _isDragOver = false;
        // 延迟隐藏，避免在子元素间移动时闪烁
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_isDragOver)
            {
                DropOverlay.Visibility = Visibility.Collapsed;
            }
        }), DispatcherPriority.Background);
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (paths.Length > 0)
                LoadFromPath(paths[0]);
        }
        e.Handled = true;
    }

    // ─── 加载入口：文件或文件夹 ───────────────────────────────────
    public void LoadFromPath(string path)
    {
        // 停止之前的文件监视
        StopFileWatcher();

        if (Directory.Exists(path))
        {
            List<string> files;
            
            // 获取文件列表并使用自然排序（与资源管理器默认排序一致）
            files = Directory.GetFiles(path)
                .Where(f => SupportedExts.Contains(
                    Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => Path.GetFileName(f), NaturalStringComparer.Instance)
                .ToList();

            if (files.Count == 0)
            {
                MessageBox.Show("该文件夹内没有受支持的图片文件。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _imageFiles = files;
            ReloadThumbnails();
            ShowImage(0);
            StartFileWatcher(path);
        }
        else if (File.Exists(path))
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (!SupportedExts.Contains(ext))
            {
                MessageBox.Show("不支持该文件格式。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string? dir = Path.GetDirectoryName(path);
            if (dir != null)
            {
                // 获取文件列表并使用自然排序（与资源管理器默认排序一致）
                var files = Directory.GetFiles(dir)
                    .Where(f => SupportedExts.Contains(
                        Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => Path.GetFileName(f), NaturalStringComparer.Instance)
                    .ToList();

                _imageFiles = files;
                ReloadThumbnails();
                int idx = files.FindIndex(f =>
                    string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
                ShowImage(idx >= 0 ? idx : 0);
                StartFileWatcher(dir);
            }
            else
            {
                _imageFiles = new List<string> { path };
                ReloadThumbnails();
                ShowImage(0);
            }
        }
    }

    // ─── 右键菜单项 ───────────────────────────────────────────────
    private void MenuOpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0 || _currentIndex >= _imageFiles.Count) return;
        Process.Start("explorer.exe", $"/select,\"{_imageFiles[_currentIndex]}\"");
    }

    private void MenuCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0 || _currentIndex >= _imageFiles.Count) return;
        Clipboard.SetText(_imageFiles[_currentIndex]);
        ShowToast("路径已复制到剪贴板");
    }

    // ─── 显示提示信息 ─────────────────────────────────────────────
    private void ShowToast(string message)
    {
        var toast = new System.Windows.Controls.TextBlock
        {
            Text = message,
            Foreground = System.Windows.Media.Brushes.White,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(230, 0, 0, 0)),
            Padding = new Thickness(16, 8, 16, 8),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var border = new System.Windows.Controls.Border
        {
            Background = toast.Background,
            CornerRadius = new CornerRadius(8),
            Child = toast,
            Margin = new Thickness(0, 0, 0, 100),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Opacity = 0
        };

        // 添加到窗口
        var grid = Content as System.Windows.Controls.Grid;
        if (grid == null) return;

        System.Windows.Controls.Grid.SetRowSpan(border, 3);
        grid.Children.Add(border);
        Panel.SetZIndex(border, 1000);

        // 动画显示
        var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
        var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200))
        {
            BeginTime = TimeSpan.FromSeconds(3)
        };

        fadeOut.Completed += (s, ev) =>
        {
            grid.Children.Remove(border);
        };

        border.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        border.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    // ─── 工具方法 ─────────────────────────────────────────────────
    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double size = bytes;
        int i = 0;
        while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
        return $"{size:0.#} {units[i]}";
    }
}
