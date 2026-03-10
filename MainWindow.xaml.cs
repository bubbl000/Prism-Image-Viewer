using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageViewer
{
    public partial class MainWindow : Window
    {
        // ─── 图片列表与当前索引 ────────────────────────────────────────
        private List<string> _imageFiles = new();
        private int _currentIndex = -1;
        private double _currentRotation = 0;

        // Ctrl+拖拽外部拖出
        private Point _dragStartPoint;
        private bool _isDragging = false;

        // 缩略图异步加载
        private CancellationTokenSource? _thumbCts;

        private static readonly string[] SupportedExts =
            { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp" };

        public MainWindow()
        {
            InitializeComponent();
        }

        // ─── 标题栏拖动 ───────────────────────────────────────────────
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                return;
            }

            DependencyObject? current = e.OriginalSource as DependencyObject;
            while (current != null)
            {
                if (current is Button) return;
                if (ReferenceEquals(current, TitleBar)) break;
                current = VisualTreeHelper.GetParent(current);
            }

            DragMove();
        }

        // ─── 窗口控制按钮 ─────────────────────────────────────────────
        private void BtnMinimize_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState.Minimized;

        private void BtnMaximize_Click(object sender, RoutedEventArgs e) =>
            ToggleMaximize();

        private void BtnClose_Click(object sender, RoutedEventArgs e) =>
            Close();

        private void ToggleMaximize()
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
                BtnMaximize.Content = "□";
            }
            else
            {
                WindowState = WindowState.Maximized;
                BtnMaximize.Content = "❐";
            }
        }

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
        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                DropOverlay.Visibility = Visibility.Visible;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
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
        private void LoadFromPath(string path)
        {
            if (Directory.Exists(path))
            {
                var files = Directory.GetFiles(path)
                    .Where(f => SupportedExts.Contains(
                        Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
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
                    var files = Directory.GetFiles(dir)
                        .Where(f => SupportedExts.Contains(
                            Path.GetExtension(f).ToLowerInvariant()))
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    _imageFiles = files;
                    ReloadThumbnails();
                    int idx = files.FindIndex(f =>
                        string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
                    ShowImage(idx >= 0 ? idx : 0);
                }
                else
                {
                    _imageFiles = new List<string> { path };
                    ReloadThumbnails();
                    ShowImage(0);
                }
            }
        }

        // ─── 显示指定索引的图片 ───────────────────────────────────────
        private void ShowImage(int index)
        {
            if (index < 0 || index >= _imageFiles.Count) return;

            string filePath = _imageFiles[index];
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(filePath);
                bitmap.EndInit();

                MainImage.Source = bitmap;
                _currentIndex = index;
                _currentRotation = 0;
                ImageRotation.Angle = 0;

                ZoomBorder.Uniform();

                UpdateTitleInfo(filePath, bitmap);
                UpdateNavButtons();
                ThumbStrip.Visibility = Visibility.Visible;
                UpdateThumbSelection(index);

                EmptyState.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法加载图片：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ─── 更新标题栏信息 ───────────────────────────────────────────
        private void UpdateTitleInfo(string filePath, BitmapImage bitmap)
        {
            var info = new FileInfo(filePath);
            TxtFileName.Text = info.Name;
            TxtDimensions.Text = $"{bitmap.PixelWidth}×{bitmap.PixelHeight}";
            TxtFileSize.Text = FormatSize(info.Length);

            TxtSep1.Visibility = Visibility.Visible;
            TxtDimensions.Visibility = Visibility.Visible;
            TxtSep2.Visibility = Visibility.Visible;
            TxtFileSize.Visibility = Visibility.Visible;

            if (_imageFiles.Count > 1)
                TxtIndex.Text = $"{_currentIndex + 1} / {_imageFiles.Count}";
            else
                TxtIndex.Text = "";
        }

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
            int idx = (_currentIndex - 1 + _imageFiles.Count) % _imageFiles.Count;
            ShowImage(idx);
        }

        private void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            if (_imageFiles.Count == 0) return;
            int idx = (_currentIndex + 1) % _imageFiles.Count;
            ShowImage(idx);
        }

        // ─── 键盘导航 ─────────────────────────────────────────────────
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (TxtZoom.IsFocused) return;

            switch (e.Key)
            {
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

        // ─── 缩放显示同步 ─────────────────────────────────────────────
        private void ZoomBorder_LayoutUpdated(object? sender, EventArgs e)
        {
            UpdateZoomDisplay();
        }

        private void UpdateZoomDisplay()
        {
            if (MainImage.Source == null) return;

            double zoom = ZoomBorder.ZoomX * 100.0;
            TxtZoom.Text = $"{zoom:0}%";
        }

        // ─── 缩放文本框 ───────────────────────────────────────────────
        private void TxtZoom_GotFocus(object sender, RoutedEventArgs e)
        {
            TxtZoom.SelectAll();
        }

        private void TxtZoom_LostFocus(object sender, RoutedEventArgs e)
        {
            UpdateZoomDisplay();
        }

        private void TxtZoom_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ApplyZoomFromTextBox();
                Keyboard.ClearFocus();
            }
            else if (e.Key == Key.Escape)
            {
                UpdateZoomDisplay();
                Keyboard.ClearFocus();
            }
        }

        private void ApplyZoomFromTextBox()
        {
            string raw = TxtZoom.Text.Replace("%", "").Trim();
            if (double.TryParse(raw, out double pct) && pct > 0)
            {
                double zoom = pct / 100.0;
                zoom = Math.Clamp(zoom, 0.05, 32.0);

                // 以控件中心为缩放基点
                double cx = ZoomBorder.ActualWidth / 2;
                double cy = ZoomBorder.ActualHeight / 2;
                ZoomBorder.ZoomTo(zoom, cx, cy);
            }
            else
            {
                UpdateZoomDisplay();
            }
        }

        // ─── 旋转 ─────────────────────────────────────────────────────
        private void BtnRotateLeft_Click(object sender, RoutedEventArgs e)
        {
            _currentRotation = (_currentRotation - 90 + 360) % 360;
            ImageRotation.Angle = _currentRotation;
            ZoomBorder.Uniform();
        }

        private void BtnRotateRight_Click(object sender, RoutedEventArgs e)
        {
            _currentRotation = (_currentRotation + 90) % 360;
            ImageRotation.Angle = _currentRotation;
            ZoomBorder.Uniform();
        }

        // ─── 信息按钮 ─────────────────────────────────────────────────
        private void BtnInfo_Click(object sender, RoutedEventArgs e)
        {
            if (_currentIndex < 0 || _currentIndex >= _imageFiles.Count) return;

            string path = _imageFiles[_currentIndex];
            var fi = new FileInfo(path);
            var src = MainImage.Source as BitmapSource;
            string info = $"文件名：{fi.Name}\n" +
                          $"路径：{fi.DirectoryName}\n" +
                          $"大小：{FormatSize(fi.Length)}\n" +
                          (src != null ? $"分辨率：{src.PixelWidth} × {src.PixelHeight}\n" : "") +
                          $"修改时间：{fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}";

            MessageBox.Show(info, "图片信息", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ─── 清空图片状态 ─────────────────────────────────────────────
        private void ClearImageState()
        {
            _thumbCts?.Cancel();
            ThumbPanel.Children.Clear();
            ThumbStrip.Visibility = Visibility.Collapsed;
            MainImage.Source = null;
            _currentIndex = -1;
            TxtFileName.Text = "";
            TxtSep1.Visibility = Visibility.Collapsed;
            TxtDimensions.Visibility = Visibility.Collapsed;
            TxtSep2.Visibility = Visibility.Collapsed;
            TxtFileSize.Visibility = Visibility.Collapsed;
            TxtIndex.Text = "";
            BtnPrev.Visibility = Visibility.Collapsed;
            BtnNext.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
        }

        // ─── 更多按钮（触发 ContextMenu） ────────────────────────────
        private void BtnMore_Click(object sender, RoutedEventArgs e)
        {
            if (BtnMore.ContextMenu != null)
            {
                BtnMore.ContextMenu.PlacementTarget = BtnMore;
                BtnMore.ContextMenu.IsOpen = true;
            }
        }

        // ─── 更多菜单项 ───────────────────────────────────────────────
        private void MenuShowInExplorer_Click(object sender, RoutedEventArgs e)
        {
            if (_currentIndex < 0 || _currentIndex >= _imageFiles.Count) return;
            string path = _imageFiles[_currentIndex];
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        }

        private void MenuCopyPath_Click(object sender, RoutedEventArgs e)
        {
            if (_currentIndex < 0 || _currentIndex >= _imageFiles.Count) return;
            Clipboard.SetText(_imageFiles[_currentIndex]);
        }

        // ─── Ctrl+拖拽：拖出图片到其他程序 ───────────────────────────
        private void ZoomBorder_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(ZoomBorder);
            _isDragging = false;
        }

        private void ZoomBorder_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (MainImage.Source == null) return;
            if (!Keyboard.IsKeyDown(Key.LeftCtrl) && !Keyboard.IsKeyDown(Key.RightCtrl)) return;

            if (_isDragging) return;

            Point pos = e.GetPosition(ZoomBorder);
            double dx = pos.X - _dragStartPoint.X;
            double dy = pos.Y - _dragStartPoint.Y;
            if (Math.Sqrt(dx * dx + dy * dy) < 8) return;

            if (_currentIndex < 0 || _currentIndex >= _imageFiles.Count) return;

            _isDragging = true;
            string path = _imageFiles[_currentIndex];
            var data = new DataObject(DataFormats.FileDrop, new string[] { path });
            DragDrop.DoDragDrop(ZoomBorder, data, DragDropEffects.Copy | DragDropEffects.Move);
            _isDragging = false;
        }

        // ─── 缩略图 ───────────────────────────────────────────────────
        private void ReloadThumbnails()
        {
            _thumbCts?.Cancel();
            _thumbCts = new CancellationTokenSource();
            ThumbPanel.Children.Clear();
            _ = LoadThumbnailsAsync(_imageFiles.ToList(), _thumbCts.Token);
        }

        private async Task LoadThumbnailsAsync(List<string> files, CancellationToken ct)
        {
            // 先同步添加所有占位符
            for (int i = 0; i < files.Count; i++)
            {
                if (ct.IsCancellationRequested) return;
                ThumbPanel.Children.Add(CreateThumbPlaceholder(i));
            }

            // 再异步逐个加载真实缩略图
            for (int i = 0; i < files.Count; i++)
            {
                if (ct.IsCancellationRequested) return;

                string file = files[i];
                BitmapImage? bmp = null;
                try
                {
                    bmp = await Task.Run(() =>
                    {
                        using var stream = new FileStream(file, FileMode.Open,
                            FileAccess.Read, FileShare.Read);
                        var b = new BitmapImage();
                        b.BeginInit();
                        b.CacheOption = BitmapCacheOption.OnLoad;
                        b.DecodePixelHeight = 60;
                        b.StreamSource = stream;
                        b.EndInit();
                        b.Freeze();
                        return b;
                    }, ct);
                }
                catch (OperationCanceledException) { return; }
                catch { /* 跳过加载失败的缩略图 */ }

                if (ct.IsCancellationRequested) return;

                if (bmp != null && i < ThumbPanel.Children.Count &&
                    ThumbPanel.Children[i] is Border border &&
                    border.Child is Image img)
                {
                    img.Source = bmp;
                }
            }
        }

        private Border CreateThumbPlaceholder(int index)
        {
            var img = new Image
            {
                Stretch = Stretch.Uniform,
                MaxWidth = 66,
                MaxHeight = 54,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            var border = new Border
            {
                Width = 74,
                Height = 66,
                Margin = new Thickness(2, 4, 2, 4),
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                CornerRadius = new CornerRadius(4),
                Cursor = Cursors.Hand,
                Tag = index,
                Child = img,
            };

            border.MouseLeftButtonDown += (s, _) => ShowImage((int)((Border)s).Tag);
            return border;
        }

        private static readonly SolidColorBrush ThumbActiveBrush =
            (SolidColorBrush)new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)).GetAsFrozen();

        private void UpdateThumbSelection(int index)
        {
            for (int i = 0; i < ThumbPanel.Children.Count; i++)
            {
                if (ThumbPanel.Children[i] is Border b)
                    b.BorderBrush = (i == index) ? ThumbActiveBrush : Brushes.Transparent;
            }
            ScrollThumbIntoView(index);
        }

        private void ScrollThumbIntoView(int index)
        {
            if (index < 0 || ThumbScroll.ActualWidth == 0) return;
            const double itemWidth = 78; // 74 width + 4 margin
            double offset = ThumbScroll.HorizontalOffset;
            double view = ThumbScroll.ActualWidth;
            double pos = index * itemWidth;

            if (pos < offset)
                ThumbScroll.ScrollToHorizontalOffset(pos);
            else if (pos + itemWidth > offset + view)
                ThumbScroll.ScrollToHorizontalOffset(pos + itemWidth - view);
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
}
