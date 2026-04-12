using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImageBrowser.Utils;
using ImageBrowser.Services;

namespace ImageBrowser;

public partial class MainWindow
{
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
}
