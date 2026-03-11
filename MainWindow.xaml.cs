using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

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

        // 缩略图显示模式
        private bool _thumbAlwaysOn = false;  // 常驻显示
        private bool _smartThumb = false;      // 智能展示（悬停渐显）
        private DispatcherTimer? _thumbHideTimer;

        // 鸟瞰图
        private bool _showBirdEye = false;

        // 文件夹穿透
        private bool _folderTraverse = false;

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
                UpdateThumbSelection(index);

                if (_thumbAlwaysOn)
                    ShowThumbImmediate();
                else if (_smartThumb && ThumbStrip.Visibility == Visibility.Collapsed)
                    PrepareSmartThumb(); // 智能模式：保持 Visible+Opacity=0，热区可触发（已显示时不重置）

                if (_showBirdEye)
                {
                    BirdEyeImage.Source = bitmap;
                    BirdEyePanel.Visibility = Visibility.Visible;
                }

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
            if (_currentIndex == 0 && _folderTraverse)
            {
                TryEnterAdjacentFolder(forward: false);
                return;
            }
            int idx = (_currentIndex - 1 + _imageFiles.Count) % _imageFiles.Count;
            ShowImage(idx);
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

        // ZoomBorder.Uniform() 将 ZoomX 重置为 1.0（内容填满视口），
        // 而非设为实际缩放系数。因此 100% 需基于 fitScale 修正：
        //   zoom% = ZoomX * fitScale * 100
        //   fitScale = min(vpW/imgW, vpH/imgH)
        private double GetFitScale(BitmapSource bmp)
        {
            bool sideways = (_currentRotation % 180 == 90);
            double imgW = sideways ? bmp.PixelHeight : bmp.PixelWidth;
            double imgH = sideways ? bmp.PixelWidth  : bmp.PixelHeight;
            double vpW = ZoomBorder.ActualWidth;
            double vpH = ZoomBorder.ActualHeight;
            if (vpW <= 0 || vpH <= 0 || imgW <= 0 || imgH <= 0) return 1.0;
            return Math.Min(vpW / imgW, vpH / imgH);
        }

        private void UpdateZoomDisplay()
        {
            if (MainImage.Source is not BitmapSource bmp) return;
            double zoom = ZoomBorder.ZoomX * GetFitScale(bmp) * 100.0;
            TxtZoom.Text = $"{zoom:0}%";
            UpdateBirdEye();
        }

        // ─── 缩放文本框 ───────────────────────────────────────────────
        private void TxtZoom_GotFocus(object sender, RoutedEventArgs e) =>
            TxtZoom.SelectAll();

        private void TxtZoom_LostFocus(object sender, RoutedEventArgs e) =>
            UpdateZoomDisplay();

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
            if (double.TryParse(raw, out double pct) && pct > 0 && ZoomBorder.ZoomX > 0
                && MainImage.Source is BitmapSource bmp)
            {
                double fitScale = GetFitScale(bmp);
                if (fitScale <= 0) return;
                // zoom% = ZoomX * fitScale * 100  →  targetZoomX = pct / (100 * fitScale)
                double targetZoomX = Math.Clamp(pct / (100.0 * fitScale), 0.01, 200.0);
                double factor = targetZoomX / ZoomBorder.ZoomX;
                ZoomBorder.ZoomTo(factor, ZoomBorder.ActualWidth / 2, ZoomBorder.ActualHeight / 2);
            }
            else
            {
                UpdateZoomDisplay();
            }
        }

        // ─── 适应窗口 / 原始大小 ──────────────────────────────────────
        private void BtnFitWidth_Click(object sender, RoutedEventArgs e)
        {
            ZoomBorder.Uniform(); // 宽/高自动约束适配窗口
        }

        private void BtnActualSize_Click(object sender, RoutedEventArgs e)
        {
            if (MainImage.Source is not BitmapSource bmp || ZoomBorder.ZoomX == 0) return;
            double fitScale = GetFitScale(bmp);
            if (fitScale <= 0) return;
            // 100% = ZoomX * fitScale = 1  →  targetZoomX = 1/fitScale
            double factor = 1.0 / (fitScale * ZoomBorder.ZoomX);
            ZoomBorder.ZoomTo(factor, ZoomBorder.ActualWidth / 2, ZoomBorder.ActualHeight / 2);
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
            StopHideTimer();
            ThumbStrip.Visibility = Visibility.Collapsed;
            BottomPanel.BeginAnimation(OpacityProperty, null);
            BottomPanel.Opacity = 1;
            BottomPanel.Visibility = Visibility.Visible;
            BirdEyePanel.Visibility = Visibility.Collapsed;
            BirdEyeImage.Source = null;
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

        // ─── 更多按钮 ─────────────────────────────────────────────────
        private void BtnMore_Click(object sender, RoutedEventArgs e)
        {
            if (!MorePopup.IsOpen)
            {
                UpdateToggleIndicators();
                MorePopup.IsOpen = true;
            }
            else
            {
                MorePopup.IsOpen = false;
            }
        }

        // ─── 弹出菜单项 ───────────────────────────────────────────────
        private void MenuShowInExplorer_Click(object sender, RoutedEventArgs e)
        {
            MorePopup.IsOpen = false;
            if (_currentIndex < 0 || _currentIndex >= _imageFiles.Count) return;
            Process.Start("explorer.exe", $"/select,\"{_imageFiles[_currentIndex]}\"");
        }

        private void MenuCopyPath_Click(object sender, RoutedEventArgs e)
        {
            MorePopup.IsOpen = false;
            if (_currentIndex < 0 || _currentIndex >= _imageFiles.Count) return;
            Clipboard.SetText(_imageFiles[_currentIndex]);
        }

        private void MenuThumb_Click(object sender, RoutedEventArgs e)
        {
            MorePopup.IsOpen = false;
            _thumbAlwaysOn = !_thumbAlwaysOn;
            if (_thumbAlwaysOn)
            {
                _smartThumb = false;
                StopHideTimer();
                if (_currentIndex >= 0) ShowThumbImmediate();
            }
            else
            {
                HideThumbImmediate();
            }
            UpdateToggleIndicators();
        }

        private void MenuBirdEye_Click(object sender, RoutedEventArgs e)
        {
            MorePopup.IsOpen = false;
            _showBirdEye = !_showBirdEye;
            if (_showBirdEye && _currentIndex >= 0)
            {
                BirdEyeImage.Source = MainImage.Source;
                BirdEyePanel.Visibility = Visibility.Visible;
                UpdateBirdEye();
            }
            else
            {
                BirdEyePanel.Visibility = Visibility.Collapsed;
            }
            UpdateToggleIndicators();
        }

        private void MenuSmart_Click(object sender, RoutedEventArgs e)
        {
            MorePopup.IsOpen = false;
            _smartThumb = !_smartThumb;
            if (_smartThumb)
            {
                _thumbAlwaysOn = false;
                if (_currentIndex >= 0)
                    PrepareSmartThumb(); // 有图片时：保持 Visible+Opacity=0
                else
                    HideThumbImmediate(); // 无图片时彻底隐藏
            }
            else
            {
                StopHideTimer();
                HideThumbImmediate();
            }
            UpdateToggleIndicators();
        }

        // ─── 缩略图热区（智能展示） ───────────────────────────────────
        private void HotZone_MouseEnter(object sender, MouseEventArgs e)
        {
            if (!_smartThumb || _currentIndex < 0) return;
            StopHideTimer();
            FadeInThumbStrip();
        }

        private void HotZone_MouseLeave(object sender, MouseEventArgs e)
        {
            if (!_smartThumb) return;
            EnsureHideTimer().Start();
        }

        private DispatcherTimer EnsureHideTimer()
        {
            if (_thumbHideTimer == null)
            {
                _thumbHideTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(150)
                };
                _thumbHideTimer.Tick += (_, _) =>
                {
                    _thumbHideTimer.Stop();
                    FadeOutThumbStrip();
                };
            }
            _thumbHideTimer.Stop();
            return _thumbHideTimer;
        }

        private void StopHideTimer() => _thumbHideTimer?.Stop();

        // ─── 缩略图渐显 / 渐隐动画 ───────────────────────────────────
        private void FadeInThumbStrip()
        {
            if (BottomPanel.Visibility == Visibility.Visible && BottomPanel.Opacity >= 1) return;
            BottomPanel.Visibility = Visibility.Visible;
            var anim = new DoubleAnimation(BottomPanel.Opacity, 1.0,
                new Duration(TimeSpan.FromMilliseconds(180)));
            BottomPanel.BeginAnimation(OpacityProperty, anim);
        }

        private void FadeOutThumbStrip()
        {
            if (BottomPanel.Visibility == Visibility.Collapsed) return;
            var anim = new DoubleAnimation(BottomPanel.Opacity, 0.0,
                new Duration(TimeSpan.FromMilliseconds(200)));
            // 淡出后保持 Visible，热区仍可触发
            BottomPanel.BeginAnimation(OpacityProperty, anim);
        }

        private void ShowThumbImmediate()
        {
            ThumbStrip.Visibility = Visibility.Visible;
            BottomPanel.BeginAnimation(OpacityProperty, null);
            BottomPanel.Opacity = 1;
            BottomPanel.Visibility = Visibility.Visible;
        }

        // 智能模式专用：BottomPanel 透明但占位，热区鼠标事件可触发
        private void PrepareSmartThumb()
        {
            ThumbStrip.Visibility = Visibility.Visible;
            BottomPanel.BeginAnimation(OpacityProperty, null);
            BottomPanel.Opacity = 0;
            BottomPanel.Visibility = Visibility.Visible;
        }

        private void HideThumbImmediate()
        {
            StopHideTimer();
            BottomPanel.BeginAnimation(OpacityProperty, null);
            if (_smartThumb)
            {
                // 智能模式：BottomPanel 保持 Visible+Opacity=0，热区继续有效
                BottomPanel.Opacity = 0;
                BottomPanel.Visibility = Visibility.Visible;
            }
            else
            {
                ThumbStrip.Visibility = Visibility.Collapsed;
                BottomPanel.Opacity = 1;
                BottomPanel.Visibility = Visibility.Visible;
            }
        }

        private void MenuFolderTraverse_Click(object sender, RoutedEventArgs e)
        {
            MorePopup.IsOpen = false;
            _folderTraverse = !_folderTraverse;
            UpdateToggleIndicators();
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

            var info = new ConfirmDialog("文件夹穿透",
                forward ? "后面没有包含图片的文件夹了。" : "前面没有包含图片的文件夹了。",
                okOnly: true) { Owner = this };
            info.ShowDialog();
        }

        // ─── 更新开关滑块 ─────────────────────────────────────────────
        private void UpdateToggleIndicators()
        {
            AnimateToggle(ThumbPill, ThumbDotSlider, _thumbAlwaysOn);
            AnimateToggle(SmartPill, SmartDotSlider, _smartThumb);
            AnimateToggle(BirdEyePill, BirdEyeDotSlider, _showBirdEye);
            AnimateToggle(FolderTraversePill, FolderTraverseDotSlider, _folderTraverse);
        }

        private static void AnimateToggle(Border pill, Border dot, bool isOn)
        {
            pill.Background = new SolidColorBrush(isOn
                ? Color.FromRgb(0x90, 0xC2, 0x08)
                : Color.FromRgb(0x3A, 0x3A, 0x3A));

            dot.BeginAnimation(Canvas.LeftProperty,
                new DoubleAnimation(isOn ? 18.0 : 2.0,
                    new Duration(TimeSpan.FromMilliseconds(160)))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                });
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
            var data = new DataObject(DataFormats.FileDrop, new string[] { _imageFiles[_currentIndex] });
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
            border.MouseEnter += (s, _) =>
            {
                var b = (Border)s;
                if ((int)b.Tag != _currentIndex) b.Background = ThumbHoverBg;
            };
            border.MouseLeave += (s, _) =>
            {
                var b = (Border)s;
                if ((int)b.Tag != _currentIndex) b.Background = ThumbNormalBg;
            };
            return border;
        }

        private static readonly SolidColorBrush ThumbActiveBrush =
            (SolidColorBrush)new SolidColorBrush(Color.FromRgb(0x90, 0xC2, 0x08)).GetAsFrozen();
        private static readonly SolidColorBrush ThumbNormalBg =
            (SolidColorBrush)new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)).GetAsFrozen();
        private static readonly SolidColorBrush ThumbHoverBg =
            (SolidColorBrush)new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)).GetAsFrozen();

        private void UpdateThumbSelection(int index)
        {
            for (int i = 0; i < ThumbPanel.Children.Count; i++)
            {
                if (ThumbPanel.Children[i] is Border b)
                {
                    b.BorderBrush = (i == index) ? ThumbActiveBrush : Brushes.Transparent;
                    if (i != index) b.Background = ThumbNormalBg; // 重置悬停背景
                }
            }
            ScrollThumbIntoView(index);
        }

        private void ScrollThumbIntoView(int index)
        {
            if (index < 0) return;
            if (ThumbScroll.ActualWidth == 0)
            {
                // 只有当栏已 Visible（布局还未完成）时才延迟；Collapsed 时直接跳过避免无限调度
                if (ThumbStrip.Visibility == Visibility.Visible)
                    Dispatcher.InvokeAsync(() => ScrollThumbIntoView(index),
                        System.Windows.Threading.DispatcherPriority.Loaded);
                return;
            }
            const double itemWidth = 78;
            double center = index * itemWidth + itemWidth / 2.0 - ThumbScroll.ActualWidth / 2.0;
            ThumbScroll.ScrollToHorizontalOffset(Math.Max(0, center));
        }

        // ─── 鸟瞰图更新 ───────────────────────────────────────────────
        private void UpdateBirdEye()
        {
            if (BirdEyePanel.Visibility != Visibility.Visible) return;
            if (MainImage.Source is not BitmapSource bmp) return;
            if (BirdEyeCanvas.ActualWidth == 0 || BirdEyeCanvas.ActualHeight == 0) return;

            bool sideways = (_currentRotation % 180 == 90);
            double imgPixW = sideways ? bmp.PixelHeight : bmp.PixelWidth;
            double imgPixH = sideways ? bmp.PixelWidth  : bmp.PixelHeight;

            // 鸟瞰图画布：图片像素 → 鸟瞰缩略图坐标
            double panelW  = BirdEyeCanvas.ActualWidth;
            double panelH  = BirdEyeCanvas.ActualHeight;
            double beScale = Math.Min(panelW / imgPixW, panelH / imgPixH);
            double beOffX  = (panelW - imgPixW * beScale) / 2;
            double beOffY  = (panelH - imgPixH * beScale) / 2;

            // ZoomBorder 状态（内容坐标系 = ZoomBorder WPF 单位）
            double zx = ZoomBorder.ZoomX;
            double zy = ZoomBorder.ZoomY;
            double ox = ZoomBorder.OffsetX;
            double oy = ZoomBorder.OffsetY;
            double vw = ZoomBorder.ActualWidth;
            double vh = ZoomBorder.ActualHeight;

            // Stretch=Uniform 导致图片在内容坐标内有上下或左右留白
            double imgAspect = imgPixW / imgPixH;
            double rendW, rendH;
            if (vw / imgAspect <= vh)
            { rendW = vw; rendH = vw / imgAspect; }
            else
            { rendH = vh; rendW = vh * imgAspect; }
            double rendX = (vw - rendW) / 2;
            double rendY = (vh - rendH) / 2;

            // 可见内容区域（ZoomBorder 内容坐标）
            double visL = Math.Max(0, -ox / zx);
            double visT = Math.Max(0, -oy / zy);
            double visR = Math.Min(vw, (vw - ox) / zx);
            double visB = Math.Min(vh, (vh - oy) / zy);

            // 裁剪到图片渲染区域，并转换为图片像素坐标
            double pixL = Math.Max(0,      (Math.Max(visL, rendX)        - rendX) / rendW * imgPixW);
            double pixT = Math.Max(0,      (Math.Max(visT, rendY)        - rendY) / rendH * imgPixH);
            double pixR = Math.Min(imgPixW, (Math.Min(visR, rendX + rendW) - rendX) / rendW * imgPixW);
            double pixB = Math.Min(imgPixH, (Math.Min(visB, rendY + rendH) - rendY) / rendH * imgPixH);

            // ZoomX <= 1.0 表示图片适配视口，无需鸟瞰图
            bool fullyVisible = zx <= 1.0;
            double targetOpacity = fullyVisible ? 0.0 : 1.0;
            if (Math.Abs(BirdEyePanel.Opacity - targetOpacity) > 0.01)
            {
                var anim = new System.Windows.Media.Animation.DoubleAnimation(
                    targetOpacity, TimeSpan.FromMilliseconds(200));
                BirdEyePanel.BeginAnimation(OpacityProperty, anim);
            }

            // 图片像素坐标 → 鸟瞰图画布坐标
            Canvas.SetLeft(BirdEyeRect, beOffX + pixL * beScale);
            Canvas.SetTop(BirdEyeRect,  beOffY + pixT * beScale);
            BirdEyeRect.Width  = Math.Max(0, (pixR - pixL) * beScale);
            BirdEyeRect.Height = Math.Max(0, (pixB - pixT) * beScale);
        }

        // ─── 窗口边缘拖拽缩放 ─────────────────────────────────────────
        private static readonly Dictionary<string, int> _resizeHitMap = new()
        {
            ["Left"] = 10, ["Right"] = 11, ["Top"] = 12,
            ["TopLeft"] = 13, ["TopRight"] = 14,
            ["Bottom"] = 15, ["BottomLeft"] = 16, ["BottomRight"] = 17
        };

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        private void ResizeEdge_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (WindowState == WindowState.Maximized) return;
            if (sender is FrameworkElement el && el.Tag is string tag
                && _resizeHitMap.TryGetValue(tag, out int ht))
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                ReleaseMouseCapture();
                SendMessage(hwnd, 0x00A1 /* WM_NCLBUTTONDOWN */, (IntPtr)ht, IntPtr.Zero);
                e.Handled = true;
            }
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
