using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Printing;
using System.Windows.Documents;
using System.Windows.Xps.Packaging;
using ImageBrowser.Utils;
using ImageBrowser.Services;

namespace ImageBrowser
{
    public partial class MainWindow : Window
    {
        // ─── 图片列表与当前索引 ────────────────────────────────────────
        private List<string> _imageFiles = new();
        private int _currentIndex = -1;
        private double _currentRotation = 0;

        // 异步加载
        private CancellationTokenSource? _loadCts;

        // 原图 LRU 内存缓存 + 预读取
        private readonly ImageCache _imageCache = new();
        private CancellationTokenSource? _prefetchCts;

        // GIF 动画
        private GifAnimator? _gifAnimator;
        private bool _suppressSliderEvent = false;

        // Ctrl+拖拽外部拖出
        private Point _dragStartPoint;
        private bool _isDragging = false;
        private bool _isExternalDrag = false;  // 是否正在跨软件拖拽
        private Point _dragCurrentPoint;       // 当前拖拽位置

        // 缩略图异步加载
        private CancellationTokenSource? _thumbCts;

        // 缩略图显示模式
        private bool _thumbAlwaysOn = false;  // 常驻显示
        private bool _smartThumb = false;      // 智能展示（悬停渐显）
        private DispatcherTimer? _thumbHideTimer;

        // 鸟瞰图
        private bool _showBirdEye = false;
        private DispatcherTimer? _birdEyeTimer;

        // 文件夹穿透
        private bool _folderTraverse = false;

        // 文件监视（使用增强型 SmartFileWatcher 带防抖）
        private SmartFileWatcher? _fileWatcher;

        // 导航队列（连续按键优化）
        private readonly System.Collections.Concurrent.ConcurrentQueue<int> _navigationQueue = new();
        private CancellationTokenSource? _navCts;

        private static readonly string[] SupportedExts =
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp", ".psd", ".psb",
            // RAW 格式
            ".arw", ".cr2", ".cr3", ".nef", ".orf", ".raf", ".rw2",
            ".dng", ".pef", ".sr2", ".srf", ".x3f", ".kdc", ".mos",
            ".raw", ".erf", ".mrw", ".nrw", ".ptx", ".r3d", ".3fr"
        };

        public MainWindow()
        {
            InitializeComponent();
            MouseMove += Window_MouseMove;
            MouseLeftButtonUp += Window_MouseLeftButtonUp;
            Loaded += Window_Loaded;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var s = AppSettings.Current;

            // 窗口位置
            if (s.RememberPosition &&
                s.WindowLeft.HasValue && s.WindowTop.HasValue &&
                s.WindowWidth.HasValue && s.WindowHeight.HasValue)
            {
                Left   = s.WindowLeft.Value;
                Top    = s.WindowTop.Value;
                Width  = s.WindowWidth.Value;
                Height = s.WindowHeight.Value;
            }

            // 即时生效设置
            if (s.AlwaysOnTop) Topmost = true;
            if (s.OriginalPixelMode)
                RenderOptions.SetBitmapScalingMode(MainImageViewer,
                    System.Windows.Media.BitmapScalingMode.NearestNeighbor);
            ApplyHardwareAcceleration(s.HardwareAcceleration);

            // 同步显示状态
            _thumbAlwaysOn  = s.ShowThumbnails;
            _smartThumb     = s.SmartThumbnails;
            _showBirdEye    = s.ShowBirdEye;
            _folderTraverse = s.FolderTraverse;

            // 应用显示设置
            ApplyThumbStripVisibility(_thumbAlwaysOn);
            ApplyBirdEyeVisibility(_showBirdEye);
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

        // ─── 全屏 ─────────────────────────────────────────────────────
        private bool _isFullScreen = false;
        private WindowState _preFullScreenState = WindowState.Normal;

        private void BtnFullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

        private void ToggleFullScreen()
        {
            if (!_isFullScreen)
            {
                _preFullScreenState = WindowState;
                _isFullScreen = true;
                TitleBar.Visibility = Visibility.Collapsed;
                MainBorder.CornerRadius = new CornerRadius(0);
                FullscreenExitOverlay.Visibility = Visibility.Visible;
                WindowState = WindowState.Maximized;
                BtnFullscreen.Content = "⊡";
                ApplyFullscreenBgSettings();
            }
            else
            {
                ExitFullScreen();
            }
        }

        private void ExitFullScreen()
        {
            _isFullScreen = false;
            TitleBar.Visibility = Visibility.Visible;
            MainBorder.CornerRadius = new CornerRadius(10);
            MainBorder.Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
            FullscreenExitOverlay.Visibility = Visibility.Collapsed;
            WindowState = _preFullScreenState == WindowState.Maximized ? WindowState.Normal : _preFullScreenState;
            BtnFullscreen.Content = "⛶";
        }

        private void BtnExitFullscreen_Click(object sender, RoutedEventArgs e) => ExitFullScreen();

        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            StopFileWatcher();

            var s = AppSettings.Current;
            // 同步显示状态回 AppSettings
            s.ShowThumbnails  = _thumbAlwaysOn;
            s.SmartThumbnails = _smartThumb;
            s.ShowBirdEye     = _showBirdEye;
            s.FolderTraverse  = _folderTraverse;

            if (s.RememberPosition && WindowState == WindowState.Normal)
            {
                s.WindowLeft   = Left;
                s.WindowTop    = Top;
                s.WindowWidth  = Width;
                s.WindowHeight = Height;
            }
            s.Save();

            // 清除专业格式缓存
            ClearProfessionalCache();
        }

        /// <summary>
        /// 清除专业格式缓存目录
        /// </summary>
        private void ClearProfessionalCache()
        {
            try
            {
                if (Directory.Exists(_professionalCacheDir))
                {
                    Directory.Delete(_professionalCacheDir, true);
                }
            }
            catch
            {
                // 静默忽略清理失败
            }
        }

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
                
                // 尝试获取资源管理器排序
                if (AppSettings.Current.UseExplorerSort)
                {
                    var explorerFiles = ExplorerSortService.GetExplorerSortedFiles(path);
                    if (explorerFiles != null && explorerFiles.Count > 0)
                    {
                        files = explorerFiles
                            .Where(f => SupportedExts.Contains(
                                Path.GetExtension(f).ToLowerInvariant()))
                            .ToList();
                    }
                    else
                    {
                        // 回退到自然排序
                        files = Directory.GetFiles(path)
                            .Where(f => SupportedExts.Contains(
                                Path.GetExtension(f).ToLowerInvariant()))
                            .OrderBy(f => f, NaturalStringComparer.Instance)
                            .ToList();
                    }
                }
                else
                {
                    files = Directory.GetFiles(path)
                        .Where(f => SupportedExts.Contains(
                            Path.GetExtension(f).ToLowerInvariant()))
                        .OrderBy(f => f, NaturalStringComparer.Instance)
                        .ToList();
                }

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
                    List<string> files;
                    
                    // 尝试获取资源管理器排序
                    if (AppSettings.Current.UseExplorerSort)
                    {
                        var explorerFiles = ExplorerSortService.GetExplorerSortedFiles(dir);
                        if (explorerFiles != null && explorerFiles.Count > 0)
                        {
                            files = explorerFiles
                                .Where(f => SupportedExts.Contains(
                                    Path.GetExtension(f).ToLowerInvariant()))
                                .ToList();
                        }
                        else
                        {
                            // 回退到自然排序
                            files = Directory.GetFiles(dir)
                                .Where(f => SupportedExts.Contains(
                                    Path.GetExtension(f).ToLowerInvariant()))
                                .OrderBy(f => f, NaturalStringComparer.Instance)
                                .ToList();
                        }
                    }
                    else
                    {
                        files = Directory.GetFiles(dir)
                            .Where(f => SupportedExts.Contains(
                                Path.GetExtension(f).ToLowerInvariant()))
                            .OrderBy(f => f, NaturalStringComparer.Instance)
                            .ToList();
                    }

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
            foreach (var deletedFile in e.FilePaths)
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
            if (e.FilePaths.Count > 0 && _imageFiles.Count > 0)
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

        // ─── 显示指定索引的图片（异步，支持 GIF 动画） ───────────────
        private async void ShowImage(int index)
        {
            if (index < 0 || index >= _imageFiles.Count) return;

            // 记录上一个图片的文件大小，用于内存管理
            long previousFileSize = 0;
            if (_currentIndex >= 0 && _currentIndex < _imageFiles.Count)
            {
                try
                {
                    previousFileSize = new FileInfo(_imageFiles[_currentIndex]).Length;
                }
                catch { }
            }

            // 修复竞态：先保存旧的 CTS，创建新的，然后再取消旧的
            var oldCts = Interlocked.Exchange(ref _loadCts, new CancellationTokenSource());
            oldCts?.Cancel();
            
            var cts = _loadCts;

            string filePath = _imageFiles[index];
            _currentIndex = index;
            _currentRotation = 0;
            MainImageViewer.Rotation = 0;

            // 停止并清理上一个 GIF
            StopGifAnimator();
            GifPanel.Visibility = Visibility.Collapsed;

            // 重置显示模式（从 Tile 模式切回普通模式）
            ResetToNormalMode();

            // 获取当前图片文件大小
            long currentFileSize = 0;
            try
            {
                currentFileSize = new FileInfo(filePath).Length;
            }
            catch { }

            // 检查是否为专业格式（PSD/PSB/RAW 等需要特殊处理的格式）
            bool isProfessionalFormat = filePath.EndsWith(".psd", StringComparison.OrdinalIgnoreCase) ||
                                       filePath.EndsWith(".psb", StringComparison.OrdinalIgnoreCase) ||
                                       RawLoader.IsRawFile(filePath);

            // 400ms 后才显示加载提示
            var overlayCts = new CancellationTokenSource();
            _ = ShowOverlayAfterDelay(overlayCts.Token, isProfessionalFormat);

            try
            {
                bool isGif = filePath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
                BitmapSource? bitmap = null;

                if (isGif)
                {
                    // 在后台线程加载帧数据（冻结 BitmapSource）
                    var frameData = await Task.Run(() =>
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        return GifAnimator.LoadFrames(filePath);
                    }, cts.Token);

                    if (cts.IsCancellationRequested) return;

                    if (frameData.Count > 1)
                    {
                        // DispatcherTimer 必须在 UI 线程创建
                        var animator = new GifAnimator(frameData);
                        _gifAnimator = animator;
                        _gifAnimator.TargetViewer = MainImageViewer;
                        _gifAnimator.OnFrameChanged = OnGifFrameChanged;
                        bitmap = _gifAnimator.GetFrame(0);

                        _suppressSliderEvent = true;
                        GifSlider.Maximum = frameData.Count - 1;
                        GifSlider.Value = 0;
                        _suppressSliderEvent = false;
                        TxtGifFrame.Text = $"1 / {frameData.Count}";
                        BtnGifPlayPause.Content = "⏸";
                        GifPanel.Visibility = Visibility.Visible;
                        _gifAnimator.Play();
                    }
                    else
                    {
                        bitmap = frameData.Count > 0 ? frameData[0].Frame : null;
                    }
                }
                else
                {
                    bool isRaw = RawLoader.IsRawFile(filePath);
                    bool isPsd = filePath.EndsWith(".psd", StringComparison.OrdinalIgnoreCase);
                    bool isPsb = filePath.EndsWith(".psb", StringComparison.OrdinalIgnoreCase);

                    // ── PSD/PSB：使用 Aspose.PSD 加载，优先显示内置缩略图 ──
                    if (isPsd || isPsb)
                    {
                        // 检查是否需要启用 Tile 模式（超大图像）
                        bool needsTileMode = TileLoader.NeedsTileMode(filePath);

                        if (needsTileMode && isPsb)
                        {
                            // 超大 PSB 使用 Tile 模式
                            await LoadTileModeImageAsync(filePath, cts.Token);
                            return; // Tile 模式下直接返回，不执行后续逻辑
                        }

                        // 1. 毫秒级显示内置缩略图
                        var psdThumbnail = await Task.Run(() =>
                        {
                            cts.Token.ThrowIfCancellationRequested();
                            return PsdLoader.LoadThumbnail(filePath);
                        }, cts.Token);

                        if (psdThumbnail != null && !cts.IsCancellationRequested)
                        {
                            MainImageViewer.Source = psdThumbnail;
                            ZoomBorder.Uniform();
                        }

                        // 2. 原图内存缓存命中 → 零解码开销
                        bitmap = _imageCache.TryGet(filePath);
                        if (bitmap == null)
                        {
                            bitmap = await Task.Run(() =>
                            {
                                cts.Token.ThrowIfCancellationRequested();
                                return PsdLoader.LoadImage(filePath, cts.Token);
                            }, cts.Token);

                            if (bitmap != null && !cts.IsCancellationRequested)
                                _imageCache.Put(filePath, bitmap);
                        }
                    }
                    // ── RAW：使用 Magick.NET 解码（移除 LibRaw）─────────────
                    else
                    {
                        // ── 渐进式加载：立即展示缩略图预览（毫秒级反馈） ──────
                        var preview = ThumbnailCache.TryLoad(filePath);
                        if (preview != null)
                        {
                            MainImageViewer.Source = preview;
                            ZoomBorder.Uniform();
                        }

                        // ── 原图内存缓存命中 → 零解码开销 ────────────────────
                        bitmap = _imageCache.TryGet(filePath);
                        if (bitmap == null)
                        {
                            bitmap = await Task.Run(() =>
                            {
                                cts.Token.ThrowIfCancellationRequested();
                                return LoadBitmapSource(filePath, isRaw);
                            }, cts.Token);

                            if (bitmap != null && !cts.IsCancellationRequested)
                                _imageCache.Put(filePath, bitmap);
                        }
                    }
                }

                if (cts.IsCancellationRequested) return;

                if (bitmap == null)
                {
                    throw new InvalidOperationException("图像解码失败，返回空bitmap");
                }

                // 先预取下一张，再显示当前图（优化连续浏览体验）
                StartPrefetch(index);

                MainImageViewer.Source = bitmap;
                ZoomBorder.Uniform();

                UpdateTitleInfo(filePath, bitmap);
                UpdateNavButtons();
                UpdateThumbSelection(index);

                if (_thumbAlwaysOn)
                    ShowThumbImmediate();
                else if (_smartThumb && ThumbStrip.Visibility == Visibility.Collapsed)
                    PrepareSmartThumb();

                if (_showBirdEye)
                {
                    BirdEyeImage.Source = bitmap;
                    BirdEyePanel.Visibility = Visibility.Visible;
                }

                UpdateInfoWindow();
                EmptyState.Visibility = Visibility.Collapsed;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!cts.IsCancellationRequested)
                {
                    var errorMsg = $"无法加载图片：{ex.Message}";
                    if (ex.InnerException != null)
                    {
                        errorMsg += $"\n内部错误：{ex.InnerException.Message}";
                    }
                    // 添加堆栈跟踪信息用于调试
                    errorMsg += $"\n\n堆栈跟踪：\n{ex.StackTrace}";
                    System.Diagnostics.Debug.WriteLine($"[MainWindow] 加载图片失败: {errorMsg}");
                    MessageBox.Show(errorMsg, "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                overlayCts.Cancel();
                LoadingOverlay.Visibility = Visibility.Collapsed;

                // 大图片内存优化：如果加载了大图片，尝试释放内存
                try
                {
                    long maxFileSize = Math.Max(previousFileSize, currentFileSize);
                    MemoryManager.TryReleaseMemoryAfterLoad(maxFileSize);
                }
                catch { }
            }
        }

        private async Task ShowOverlayAfterDelay(CancellationToken ct, bool isProfessionalFormat = false)
        {
            try
            {
                await Task.Delay(200, ct);  // 200ms足够过滤掉快速切换
                
                // 更新加载提示文本
                if (isProfessionalFormat)
                {
                    LoadingText.Text = "正在处理专业格式文件...";
                    LoadingSubText.Text = "首次加载会比较慢，后续会更快";
                }
                else
                {
                    LoadingText.Text = "正在加载...";
                    LoadingSubText.Text = "";
                }
                
                LoadingOverlay.Visibility = Visibility.Visible;
            }
            catch (TaskCanceledException) { }
        }

        // ─── Tile 模式加载超大图像 ────────────────────────────────────
        private async Task LoadTileModeImageAsync(string filePath, CancellationToken ct)
        {
            try
            {
                if (DeepZoomViewer == null)
                {
                    throw new InvalidOperationException("DeepZoomViewer 未初始化");
                }

                // 切换到 Tile 模式显示
                ZoomBorder.Visibility = Visibility.Collapsed;
                DeepZoomViewer.Visibility = Visibility.Visible;
                MainImageViewer.Source = null;

                // 加载超大图像
                await DeepZoomViewer.LoadImageAsync(filePath);

                if (ct.IsCancellationRequested) return;

                // 更新标题信息
                var (width, height) = TileLoader.GetImageSize(filePath);
                var info = new FileInfo(filePath);
                TxtFileName.Text = info.Name;
                TxtDimensions.Text = $"{width}×{height} (Tile模式)";
                TxtFileSize.Text = FormatSize(info.Length);

                TxtSep1.Visibility = Visibility.Visible;
                TxtDimensions.Visibility = Visibility.Visible;
                TxtSep2.Visibility = Visibility.Visible;
                TxtFileSize.Visibility = Visibility.Visible;

                if (_imageFiles.Count > 1)
                    TxtIndex.Text = $"{_currentIndex + 1} / {_imageFiles.Count}";
                else
                    TxtIndex.Text = "";

                UpdateNavButtons();
                UpdateThumbSelection(_currentIndex);
                EmptyState.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    MessageBox.Show($"无法加载超大图像：{ex.Message}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // ─── 重置为普通显示模式 ───────────────────────────────────────
        private void ResetToNormalMode()
        {
            ZoomBorder.Visibility = Visibility.Visible;
            if (DeepZoomViewer != null)
            {
                DeepZoomViewer.Visibility = Visibility.Collapsed;
                DeepZoomViewer.UnloadImage();
            }
        }

        // ─── 更新标题栏信息 ───────────────────────────────────────────
        private void UpdateTitleInfo(string filePath, BitmapSource bitmap)
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
            if (MainImageViewer.Source is not BitmapSource bmp) return;
            double zoom = ZoomBorder.ZoomX * GetFitScale(bmp) * 100.0;
            TxtZoom.Text = $"{zoom:0}%";
            ScheduleBirdEyeUpdate();
        }

        // 防抖：高频 LayoutUpdated 触发时，合并为最后一次 10ms 后执行
        private void ScheduleBirdEyeUpdate()
        {
            if (_birdEyeTimer == null)
            {
                _birdEyeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                _birdEyeTimer.Tick += (_, _) => { _birdEyeTimer.Stop(); UpdateBirdEye(); };
            }
            _birdEyeTimer.Stop();
            _birdEyeTimer.Start();
        }


        // ─── 适应窗口 / 原始大小 ──────────────────────────────────────
        private void BtnFitWidth_Click(object sender, RoutedEventArgs e)
        {
            ZoomBorder.Uniform(); // 宽/高自动约束适配窗口
        }

        private void BtnActualSize_Click(object sender, RoutedEventArgs e)
        {
            if (MainImageViewer.Source is not BitmapSource bmp || ZoomBorder.ZoomX == 0) return;
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
            MainImageViewer.Rotation = _currentRotation;
            ZoomBorder.Uniform();
        }

        private void BtnRotateRight_Click(object sender, RoutedEventArgs e)
        {
            _currentRotation = (_currentRotation + 90) % 360;
            MainImageViewer.Rotation = _currentRotation;
            ZoomBorder.Uniform();
        }

        // ─── 信息按钮 ─────────────────────────────────────────────────
        private InfoWindow? _infoWindow;

        private void BtnInfo_Click(object sender, RoutedEventArgs e)
        {
            if (_infoWindow == null || !_infoWindow.IsLoaded)
            {
                _infoWindow = new InfoWindow();
                _infoWindow.Owner = this;
                // 初始位置在主窗口右侧
                _infoWindow.Left = Left + ActualWidth + 10;
                _infoWindow.Top = Top;
            }

            _infoWindow.Show();
            _infoWindow.Activate();

            // 更新信息
            if (_currentIndex >= 0 && _currentIndex < _imageFiles.Count)
            {
                _infoWindow.UpdateInfo(_imageFiles[_currentIndex], MainImageViewer.Source as BitmapSource);
            }
            else
            {
                _infoWindow.ClearInfo();
            }
        }

        // 更新信息窗口
        private void UpdateInfoWindow()
        {
            if (_infoWindow != null && _infoWindow.IsLoaded && _infoWindow.Visibility == Visibility.Visible)
            {
                if (_currentIndex >= 0 && _currentIndex < _imageFiles.Count)
                {
                    _infoWindow.UpdateInfo(_imageFiles[_currentIndex], MainImageViewer.Source as BitmapSource);
                }
                else
                {
                    _infoWindow.ClearInfo();
                }
            }
        }

        // ─── 清空图片状态 ─────────────────────────────────────────────
        private void ClearImageState()
        {
            _loadCts?.Cancel();
            _prefetchCts?.Cancel();
            _imageCache.Clear();
            StopGifAnimator();
            GifPanel.Visibility = Visibility.Collapsed;
            LoadingOverlay.Visibility = Visibility.Collapsed;
            _thumbCts?.Cancel();
            ThumbPanel.Children.Clear();
            StopHideTimer();
            ThumbStrip.Visibility = Visibility.Collapsed;
            BottomPanel.BeginAnimation(OpacityProperty, null);
            BottomPanel.Opacity = 1;
            BottomPanel.Visibility = Visibility.Visible;
            BirdEyePanel.Visibility = Visibility.Collapsed;
            BirdEyeImage.Source = null;
            MainImageViewer.Source = null;
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

            // 并发加载（最多 3 个后台线程同时解码），按优先顺序提交任务
            using var sem = new System.Threading.SemaphoreSlim(3);
            var tasks = new List<Task>();

            foreach (int i in BuildThumbLoadOrder(files.Count, _currentIndex))
            {
                if (ct.IsCancellationRequested) break;

                int idx = i;
                string file = files[idx];

                await sem.WaitAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) { sem.Release(); break; }

                var t = Task.Run(async () =>
                {
                    BitmapSource? bmp = null;
                    try
                    {
                        // 1. 磁盘缓存命中 → 直接返回，极快
                        bmp = ThumbnailCache.TryLoad(file);
                        if (bmp == null)
                        {
                            // 2. 缓存未命中 → 从源文件解码（降采样到 60px 高）
                            BitmapSource? b = null;
                            bool isProfessionalFormat = file.EndsWith(".psd", StringComparison.OrdinalIgnoreCase) ||
                                                       file.EndsWith(".psb", StringComparison.OrdinalIgnoreCase) ||
                                                       file.EndsWith(".cr3", StringComparison.OrdinalIgnoreCase);

                            if (isProfessionalFormat)
                            {
                                // PSD/PSB 使用 Aspose.PSD 加载内置缩略图（最快）
                                if (file.EndsWith(".psd", StringComparison.OrdinalIgnoreCase) ||
                                    file.EndsWith(".psb", StringComparison.OrdinalIgnoreCase))
                                {
                                    b = PsdLoader.LoadThumbnail(file);
                                }
                                // RAW 文件使用 LibRaw 加载内嵌缩略图
                                else if (RawLoader.IsRawFile(file))
                                {
                                    b = RawLoader.LoadThumbnail(file);
                                }
                                else
                                {
                                    // 其他专业格式使用 Magick.NET
                                    b = LoadProfessionalThumbnail(file);
                                }
                            }
                            else
                            {
                                // 标准格式使用 WIC 解码
                                using var stream = new FileStream(file, FileMode.Open,
                                    FileAccess.Read, FileShare.Read);
                                var bi = new BitmapImage();
                                bi.BeginInit();
                                bi.CacheOption      = BitmapCacheOption.OnLoad;
                                bi.DecodePixelHeight = 60;
                                bi.StreamSource     = stream;
                                bi.EndInit();
                                bi.Freeze();
                                b = bi;
                            }

                            // 3. 写入磁盘缓存
                            if (b != null)
                            {
                                ThumbnailCache.TrySave(file, b);
                                bmp = b;
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch { /* 跳过加载失败的缩略图 */ }
                    finally { sem.Release(); }

                    if (bmp != null && !ct.IsCancellationRequested)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (ct.IsCancellationRequested) return;
                            if (idx < ThumbPanel.Children.Count &&
                                ThumbPanel.Children[idx] is Border border &&
                                border.Child is Image img)
                            {
                                img.Source = bmp;
                            }
                        });
                    }
                }, ct);

                tasks.Add(t);
            }

            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) { }
        }

        /// <summary>从 center 索引出发，向两侧交替展开的加载顺序。</summary>
        private static IEnumerable<int> BuildThumbLoadOrder(int count, int center)
        {
            if (count == 0) yield break;
            center = Math.Clamp(center, 0, count - 1);
            yield return center;
            for (int d = 1; d < count; d++)
            {
                int r = center + d;
                int l = center - d;
                if (r < count) yield return r;
                if (l >= 0)    yield return l;
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
                if ((int)b.Tag != _currentIndex)
                {
                    b.Background  = ThumbHoverBg;
                    b.BorderBrush = ThumbHoverBorder;
                }
            };
            border.MouseLeave += (s, _) =>
            {
                var b = (Border)s;
                if ((int)b.Tag != _currentIndex)
                {
                    b.Background  = ThumbNormalBg;
                    b.BorderBrush = Brushes.Transparent;
                }
            };
            return border;
        }

        private static readonly SolidColorBrush ThumbActiveBrush =
            (SolidColorBrush)new SolidColorBrush(Color.FromRgb(0x90, 0xC2, 0x08)).GetAsFrozen();
        private static readonly SolidColorBrush ThumbNormalBg =
            (SolidColorBrush)new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)).GetAsFrozen();
        private static readonly SolidColorBrush ThumbHoverBg =
            (SolidColorBrush)new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)).GetAsFrozen();
        private static readonly SolidColorBrush ThumbHoverBorder =
            (SolidColorBrush)new SolidColorBrush(Color.FromArgb(0x88, 0x90, 0xC2, 0x08)).GetAsFrozen();

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
                if (ThumbStrip.Visibility == Visibility.Visible)
                    Dispatcher.InvokeAsync(() => ScrollThumbIntoView(index),
                        System.Windows.Threading.DispatcherPriority.Loaded);
                return;
            }

            // 用 TransformToAncestor 精确计算缩略图中心偏移
            try
            {
                if (index < ThumbPanel.Children.Count)
                {
                    var item = ThumbPanel.Children[index] as FrameworkElement;
                    if (item != null && item.ActualWidth > 0)
                    {
                        var transform = item.TransformToAncestor(ThumbPanel);
                        var pos = transform.Transform(new Point(0, 0));
                        double offset = pos.X + item.ActualWidth / 2.0 - ThumbScroll.ActualWidth / 2.0;
                        ThumbScroll.ScrollToHorizontalOffset(Math.Max(0, offset));
                        return;
                    }
                }
            }
            catch { /* 回退到固定宽度计算 */ }

            const double itemWidth = 78;
            double center = index * itemWidth + itemWidth / 2.0 - ThumbScroll.ActualWidth / 2.0;
            ThumbScroll.ScrollToHorizontalOffset(Math.Max(0, center));
        }

        // ─── 鸟瞰图更新 ───────────────────────────────────────────────
        private void UpdateBirdEye()
        {
            if (BirdEyePanel.Visibility != Visibility.Visible) return;
            if (MainImageViewer.Source is not BitmapSource bmp) return;
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

        // ─── 右键菜单 ─────────────────────────────────────────────────
        private void ZoomBorder_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (_currentIndex < 0 || _currentIndex >= _imageFiles.Count)
                e.Handled = true;
        }

        // 重命名
        private void MenuRename_Click(object sender, RoutedEventArgs e)
        {
            if (_currentIndex < 0 || _currentIndex >= _imageFiles.Count) return;
            string oldPath = _imageFiles[_currentIndex];
            string? dir = Path.GetDirectoryName(oldPath);
            string oldName = Path.GetFileName(oldPath);
            string ext = Path.GetExtension(oldName);
            string nameWithoutExt = Path.GetFileNameWithoutExtension(oldName);

            // 使用简单的输入对话框
            var dialog = new RenameDialog(nameWithoutExt) { Owner = this };
            if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.NewName)) return;

            string newName = dialog.NewName + ext;
            string newPath = Path.Combine(dir ?? "", newName);

            if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) return;

            if (File.Exists(newPath))
            {
                MessageBox.Show("该文件名已存在。", "重命名", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                File.Move(oldPath, newPath);
                _imageFiles[_currentIndex] = newPath;
                if (MainImageViewer.Source is BitmapImage bmp)
                {
                    UpdateTitleInfo(newPath, bmp);
                }
                ReloadThumbnails();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"重命名失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // 另存为
        private void MenuSaveAs_Click(object sender, RoutedEventArgs e)
        {
            if (_currentIndex < 0 || _currentIndex >= _imageFiles.Count) return;
            if (MainImageViewer.Source is not BitmapSource bmp) return;

            string srcPath = _imageFiles[_currentIndex];
            string srcExt  = Path.GetExtension(srcPath).ToLowerInvariant();

            // 第一步：选格式 + JPG 质量
            var fmtDlg = new SaveAsDialog(bmp, srcPath) { Owner = this };
            if (fmtDlg.ShowDialog() != true) return;

            string destExt = fmtDlg.ChosenFormat == "jpg" ? ".jpg" : ".png";
            int    quality = fmtDlg.JpegQuality;

            // 第二步：系统框选路径和文件名（自动填入 原名_副本）
            var sysDlg = new Microsoft.Win32.SaveFileDialog
            {
                Title      = "另存为 — 选择保存位置",
                FileName   = Path.GetFileNameWithoutExtension(srcPath) + "_副本",
                Filter     = destExt == ".jpg"
                    ? "JPEG 图片|*.jpg;*.jpeg|所有文件|*.*"
                    : "PNG 图片|*.png|所有文件|*.*",
                DefaultExt = destExt.TrimStart('.')
            };
            if (sysDlg.ShowDialog(this) != true) return;

            // 第三步：保存
            try
            {
                if (fmtDlg.ShouldCopyOriginal)
                {
                    File.Copy(srcPath, sysDlg.FileName, overwrite: true);
                }
                else if (destExt == ".jpg")
                {
                    using var fs = new FileStream(sysDlg.FileName, FileMode.Create, FileAccess.Write);
                    ImageSharpHelper.EncodeJpeg(bmp, fs, quality);
                }
                else
                {
                    using var fs = new FileStream(sysDlg.FileName, FileMode.Create, FileAccess.Write);
                    ImageSharpHelper.EncodePng(bmp, fs);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // 复制文件到剪贴板
        private void MenuCopy_Click(object sender, RoutedEventArgs e)
        {
            if (_currentIndex < 0 || _currentIndex >= _imageFiles.Count) return;
            var data = new DataObject(DataFormats.FileDrop, new[] { _imageFiles[_currentIndex] });
            Clipboard.SetDataObject(data);
        }

        // 设置壁纸
        private void MenuSetWallpaper_Click(object sender, RoutedEventArgs e)
        {
            if (_currentIndex < 0 || _currentIndex >= _imageFiles.Count) return;
            try
            {
                SetWallpaper(_imageFiles[_currentIndex]);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"设置壁纸失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);

        private const int SPI_SETDESKWALLPAPER = 20;
        private const int SPIF_UPDATEINIFILE = 0x01;
        private const int SPIF_SENDCHANGE = 0x02;

        private void SetWallpaper(string path)
        {
            SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, path, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
        }

        // 删除
        private void MenuDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_currentIndex < 0 || _currentIndex >= _imageFiles.Count) return;
            string filePath = _imageFiles[_currentIndex];

            var dlg = new ConfirmDialog("确认删除", $"确定要删除 \"{Path.GetFileName(filePath)}\" 吗？", okOnly: false) { Owner = this };
            dlg.ShowDialog();
            if (!dlg.Confirmed) return;

            try
            {
                File.Delete(filePath);
                _imageFiles.RemoveAt(_currentIndex);
                ReloadThumbnails();

                if (_imageFiles.Count == 0)
                {
                    ClearImageState();
                }
                else
                {
                    int newIndex = Math.Min(_currentIndex, _imageFiles.Count - 1);
                    ShowImage(newIndex);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"删除失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // 打印
        private void MenuPrint_Click(object sender, RoutedEventArgs e)
        {
            if (MainImageViewer.Source == null) return;

            var dialog = new PrintDialog();
            if (dialog.ShowDialog() != true) return;

            var printDoc = new FixedDocument();
            var page = new FixedPage();
            var img = new Image
            {
                Source = MainImageViewer.Source,
                Stretch = Stretch.Uniform,
                Width = dialog.PrintableAreaWidth,
                Height = dialog.PrintableAreaHeight
            };
            FixedPage.SetLeft(img, 0);
            FixedPage.SetTop(img, 0);
            page.Children.Add(img);
            page.Width = dialog.PrintableAreaWidth;
            page.Height = dialog.PrintableAreaHeight;

            var pageContent = new PageContent();
            pageContent.Child = page;
            printDoc.Pages.Add(pageContent);

            dialog.PrintDocument(printDoc.DocumentPaginator, "打印图片");
        }

        // ─── 设置窗口公共接口 ─────────────────────────────────────────
        public bool ThumbAlwaysOn => _thumbAlwaysOn;
        public bool SmartThumb => _smartThumb;
        public bool ShowBirdEye => _showBirdEye;
        public bool FolderTraverse => _folderTraverse;

        public void SetThumbAlwaysOn(bool value)
        {
            if (_thumbAlwaysOn == value) return;
            _thumbAlwaysOn = value;
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
        }

        public void SetSmartThumb(bool value)
        {
            if (_smartThumb == value) return;
            _smartThumb = value;
            if (_smartThumb)
            {
                _thumbAlwaysOn = false;
                if (_currentIndex >= 0)
                    PrepareSmartThumb();
                else
                    HideThumbImmediate();
            }
            else
            {
                StopHideTimer();
                HideThumbImmediate();
            }
        }

        public void SetBirdEye(bool value)
        {
            if (_showBirdEye == value) return;
            _showBirdEye = value;
            if (_showBirdEye && _currentIndex >= 0)
            {
                BirdEyeImage.Source = MainImageViewer.Source;
                BirdEyePanel.Visibility = Visibility.Visible;
                UpdateBirdEye();
            }
            else
            {
                BirdEyePanel.Visibility = Visibility.Collapsed;
            }
        }

        public void SetFolderTraverse(bool value)
        {
            if (_folderTraverse == value) return;
            _folderTraverse = value;
        }

        // ─── 设置直接生效接口 ─────────────────────────────────────────
        public void ApplyAlwaysOnTop(bool value)
        {
            Topmost = value;
        }

        public void ApplyPixelMode(bool value)
        {
            RenderOptions.SetBitmapScalingMode(MainImageViewer,
                value ? System.Windows.Media.BitmapScalingMode.NearestNeighbor
                      : System.Windows.Media.BitmapScalingMode.HighQuality);
        }

        public void ApplyHardwareAcceleration(bool value)
        {
            // HwndSource.CompositionTarget.RenderMode 是 .NET 8 WPF 中控制
            // 硬件/软件渲染的正确 API（逐窗口生效）
            var src = System.Windows.Interop.HwndSource.FromVisual(this)
                      as System.Windows.Interop.HwndSource;
            if (src?.CompositionTarget != null)
                src.CompositionTarget.RenderMode = value
                    ? System.Windows.Interop.RenderMode.Default
                    : System.Windows.Interop.RenderMode.SoftwareOnly;
        }

        public void ApplyThumbStripVisibility(bool value)
        {
            if (value)
            {
                ThumbStrip.Visibility = Visibility.Visible;
                FadeInThumbStrip();
            }
            else
            {
                FadeOutThumbStrip();
            }
        }

        public void ApplyBirdEyeVisibility(bool value)
        {
            BirdEyeGrid.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
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

        // ── WIC 解码（UI 线程外调用，可复用于 ShowImage 和 StartPrefetch）─
        private static BitmapSource LoadBitmapSource(string filePath, bool isRaw)
        {
            // RAW 文件使用 Magick.NET 解码
            if (isRaw || filePath.EndsWith(".cr3", StringComparison.OrdinalIgnoreCase))
            {
                return LoadProfessionalImage(filePath);
            }

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource   = new Uri(filePath);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        public void ApplyFullscreenBgSettings()
        {
            if (!_isFullScreen) return;
            var s = AppSettings.Current;
            byte alpha = (byte)(Math.Clamp(s.FullscreenBgOpacity, 0, 1) * 255);
            byte rgb   = s.FullscreenBgType == "black" ? (byte)0 : (byte)0x1A;
            MainBorder.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(alpha, rgb, rgb, rgb));
        }

        // ─── GIF 控制 ─────────────────────────────────────────────────
        private void StopGifAnimator()
        {
            _gifAnimator?.Stop();
            _gifAnimator = null;
        }

        // ─── GIF 面板拖动功能 ──────────────────────────────────────────
        private bool _isGifPanelDragging = false;
        private Point _gifPanelDragStartPoint;
        private Point _gifPanelStartPosition;

        private void GifPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                _isGifPanelDragging = true;
                _gifPanelDragStartPoint = e.GetPosition(this);
                
                // 记录面板当前的位置
                if (GifPanel.HorizontalAlignment == HorizontalAlignment.Left && 
                    GifPanel.VerticalAlignment == VerticalAlignment.Top)
                {
                    _gifPanelStartPosition = new Point(GifPanel.Margin.Left, GifPanel.Margin.Top);
                }
                else
                {
                    // 如果是初始位置，计算相对位置（居中顶部）
                    double left = (ActualWidth - GifPanel.ActualWidth) / 2; // 水平居中
                    double top = 60; // 顶部60px
                    _gifPanelStartPosition = new Point(left, top);
                }
                
                GifPanel.CaptureMouse();
                e.Handled = true;
            }
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

        private void OnGifFrameChanged(int frameIndex)
        {
            _suppressSliderEvent = true;
            GifSlider.Value = frameIndex;
            _suppressSliderEvent = false;
            TxtGifFrame.Text = $"{frameIndex + 1} / {(int)GifSlider.Maximum + 1}";
        }

        private void BtnGifFirst_Click(object sender, RoutedEventArgs e)
        {
            if (_gifAnimator == null) return;
            _gifAnimator.Pause();
            int prev = (_gifAnimator.CurrentFrame - 1 + _gifAnimator.TotalFrames) % _gifAnimator.TotalFrames;
            _gifAnimator.GoToFrame(prev);
            BtnGifPlayPause.Content = "▶";
        }

        private void BtnGifPlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_gifAnimator == null) return;
            if (_gifAnimator.IsPlaying)
            {
                _gifAnimator.Pause();
                BtnGifPlayPause.Content = "▶";
            }
            else
            {
                _gifAnimator.Play();
                BtnGifPlayPause.Content = "⏸";
            }
        }

        private void BtnGifLast_Click(object sender, RoutedEventArgs e)
        {
            if (_gifAnimator == null) return;
            _gifAnimator.Pause();
            int next = (_gifAnimator.CurrentFrame + 1) % _gifAnimator.TotalFrames;
            _gifAnimator.GoToFrame(next);
            BtnGifPlayPause.Content = "▶";
        }

        private void GifSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSliderEvent || _gifAnimator == null) return;
            _gifAnimator.Pause();
            _gifAnimator.GoToFrame((int)e.NewValue);
            BtnGifPlayPause.Content = "▶";
        }

        // ─── 设置窗口 ─────────────────────────────────────────────────
        private SettingsWindow? _settingsWindow;

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            if (_settingsWindow == null || !_settingsWindow.IsLoaded)
            {
                _settingsWindow = new SettingsWindow(this) { Owner = this };
                _settingsWindow.Show();
            }
            else
            {
                _settingsWindow.Activate();
            }
        }

        // ─── GIF 动画管理器 ───────────────────────────────────────────
        private class GifAnimator
        {
            // 帧数据结构（可在后台线程加载）
            public record FrameData(BitmapSource Frame, int DelayMs);

            // 静态方法：在后台线程加载所有帧，返回冻结数据
            public static List<FrameData> LoadFrames(string filePath)
            {
                var result = new List<FrameData>();
                using var stream = new FileStream(filePath, FileMode.Open,
                    FileAccess.Read, FileShare.Read);
                var decoder = new GifBitmapDecoder(stream,
                    BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

                foreach (var frame in decoder.Frames)
                {
                    int delayMs = 100;
                    if (frame.Metadata is BitmapMetadata meta)
                    {
                        try
                        {
                            if (meta.ContainsQuery("/grctlext/Delay"))
                            {
                                var raw = meta.GetQuery("/grctlext/Delay");
                                if (raw != null && ushort.TryParse(raw.ToString(), out ushort cs))
                                    delayMs = Math.Max(cs * 10, 20);
                            }
                        }
                        catch { }
                    }
                    var frozen = frame.Clone();
                    frozen.Freeze();
                    result.Add(new FrameData(frozen, delayMs));
                }
                return result;
            }

            private readonly List<FrameData> _frames;
            // DispatcherTimer 必须在 UI 线程构造，此构造函数必须在 UI 线程调用
            private readonly DispatcherTimer _timer;
            private int _currentIndex = 0;
            private bool _isPlaying = false;

            public int TotalFrames => _frames.Count;
            public int CurrentFrame => _currentIndex;
            public Action<int>? OnFrameChanged { get; set; }
            public Image? TargetImage { get; set; }
            public Controls.Direct2DImageViewer? TargetViewer { get; set; }
            public bool IsPlaying => _isPlaying;

            public GifAnimator(List<FrameData> frames)
            {
                _frames = frames;
                _timer = new DispatcherTimer(); // 在 UI 线程创建，Dispatcher 正确
                _timer.Tick += OnTick;
            }

            public BitmapSource GetFrame(int index)
            {
                if (_frames.Count == 0) throw new InvalidOperationException("No frames");
                return _frames[Math.Clamp(index, 0, _frames.Count - 1)].Frame;
            }

            private void OnTick(object? sender, EventArgs e)
            {
                _currentIndex = (_currentIndex + 1) % _frames.Count;
                if (TargetImage != null)
                    TargetImage.Source = _frames[_currentIndex].Frame;
                if (TargetViewer != null)
                    TargetViewer.Source = _frames[_currentIndex].Frame;
                _timer.Interval = TimeSpan.FromMilliseconds(_frames[_currentIndex].DelayMs);
                OnFrameChanged?.Invoke(_currentIndex);
            }

            public void Play()
            {
                if (_frames.Count <= 1) return;
                _isPlaying = true;
                _timer.Interval = TimeSpan.FromMilliseconds(_frames[_currentIndex].DelayMs);
                _timer.Start();
            }

            public void Pause()
            {
                _isPlaying = false;
                _timer.Stop();
            }

            public void Stop()
            {
                _isPlaying = false;
                _timer.Stop();
            }

            public void GoToFrame(int index)
            {
                if (index < 0 || index >= _frames.Count) return;
                _currentIndex = index;
                if (TargetImage != null)
                    TargetImage.Source = _frames[_currentIndex].Frame;
                if (TargetViewer != null)
                    TargetViewer.Source = _frames[_currentIndex].Frame;
                OnFrameChanged?.Invoke(_currentIndex);
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

        // ─── 专业格式缓存目录 ──────────────────────────────────────
        private static readonly string _professionalCacheDir = 
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "棱镜图片浏览器", "专业格式缓存");

        // ─── 专业格式加载（使用 Magick.NET + 缓存优化）─────────────────
        private static BitmapSource LoadProfessionalImage(string filePath)
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

                // 使用超时机制防止无限阻塞（10秒超时）
                var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                
                // 在独立任务中执行 Magick.NET 操作
                var loadTask = Task.Run(() =>
                {
                    using (var image = new ImageMagick.MagickImage())
                    {
                        // 设置图像处理配置
                        image.Settings.SetDefine("psd:maximum-size", "8192x8192"); // 限制最大尺寸
                        image.Settings.Compression = ImageMagick.CompressionMethod.Zip; // 优化压缩
                        
                        // 检查取消令牌
                        timeoutCts.Token.ThrowIfCancellationRequested();
                        
                        // 读取文件
                        image.Read(filePath);
                        
                        // ✨ 关键优化：大图像自动缩小到合理尺寸（4K以内）
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

                        // 创建缓存目录
                        Directory.CreateDirectory(_professionalCacheDir);
                        
                        // ✨ 关键优化：使用 JPEG 作为中间格式（速度快 3-5 倍）
                        image.Format = ImageMagick.MagickFormat.Jpeg;
                        image.Quality = 95;  // 高质量
                        
                        // 生成缓存文件
                        image.Write(cachePath);
                        
                        // 从缓存文件加载（确保一致性）
                        return LoadStandardImage(cachePath);
                    }
                }, timeoutCts.Token);

                // 等待任务完成，支持超时
                if (loadTask.Wait(TimeSpan.FromSeconds(10)))
                {
                    return loadTask.Result;
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
                        
                        // ✨ 关键优化：使用 JPEG 作为中间格式
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
    }
}
