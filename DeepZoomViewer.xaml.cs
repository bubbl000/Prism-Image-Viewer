using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ImageBrowser;

/// <summary>
/// Deep Zoom 图像查看器
/// 用于超大图像（>8192px）的分块加载显示
/// </summary>
public partial class DeepZoomViewer : UserControl
{
    // Tile 缓存
    private readonly TileCache _tileCache = new();

    // 当前图像信息
    private string? _currentFilePath;
    private int _imageWidth;
    private int _imageHeight;
    private int _levelCount;

    // 视口状态
    private double _viewportX = 0;
    private double _viewportY = 0;
    private double _viewportScale = 1.0;

    // Tile 加载控制
    private CancellationTokenSource? _tileLoadCts;
    private readonly SemaphoreSlim _tileLoadSemaphore = new(3, 3); // 最多3个并发加载

    // 当前显示的 tiles
    private readonly ConcurrentDictionary<TileKey, Image> _visibleTiles = new();

    // 渲染变换
    private readonly ScaleTransform _scaleTransform = new();
    private readonly TranslateTransform _translateTransform = new();

    // 防抖定时器
    private DispatcherTimer? _viewportChangeTimer;
    private const int ViewportChangeDelayMs = 100;

    // 调试模式
    public bool DebugMode { get; set; } = false;

    public DeepZoomViewer()
    {
        InitializeComponent();
        SetupTransforms();
        SetupTimer();

        // 监听尺寸变化
        SizeChanged += OnSizeChanged;
    }

    private void SetupTransforms()
    {
        var transformGroup = new TransformGroup();
        transformGroup.Children.Add(_scaleTransform);
        transformGroup.Children.Add(_translateTransform);
        TileCanvas.RenderTransform = transformGroup;
    }

    private void SetupTimer()
    {
        _viewportChangeTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(ViewportChangeDelayMs),
            DispatcherPriority.Background,
            OnViewportChangeTimerElapsed,
            Dispatcher);
        _viewportChangeTimer.Stop();
    }

    /// <summary>
    /// 加载超大图像
    /// </summary>
    public async Task LoadImageAsync(string filePath)
    {
        // 清理旧状态
        UnloadImage();

        _currentFilePath = filePath;

        // 获取图像尺寸
        var (width, height) = TileLoader.GetImageSize(filePath);
        _imageWidth = width;
        _imageHeight = height;
        _levelCount = TileLoader.CalculateLevelCount(width, height);

        // 加载低分辨率预览
        LoadingIndicator.Visibility = Visibility.Visible;
        var preview = await Task.Run(() =>
            TileLoader.LoadPreviewThumbnail(filePath, 1024));

        if (preview != null)
        {
            PreviewImage.Source = preview;
        }

        // 初始视口居中
        ResetViewport();

        // 加载初始 tiles
        await RefreshTilesAsync();

        LoadingIndicator.Visibility = Visibility.Collapsed;

        if (DebugMode)
        {
            DebugInfo.Visibility = Visibility.Visible;
            UpdateDebugInfo();
        }
    }

    /// <summary>
    /// 卸载当前图像
    /// </summary>
    public void UnloadImage()
    {
        _tileLoadCts?.Cancel();
        _tileLoadCts = null;

        // 清除所有 tiles
        foreach (var tile in _visibleTiles.Values)
        {
            TileCanvas.Children.Remove(tile);
        }
        _visibleTiles.Clear();

        PreviewImage.Source = null;
        _currentFilePath = null;
    }

    /// <summary>
    /// 设置视口位置和缩放
    /// </summary>
    public void SetViewport(double x, double y, double scale)
    {
        _viewportX = x;
        _viewportY = y;
        _viewportScale = scale;

        // 更新变换
        UpdateTransforms();

        // 防抖刷新 tiles
        _viewportChangeTimer?.Stop();
        _viewportChangeTimer?.Start();
    }

    /// <summary>
    /// 平移视口
    /// </summary>
    public void Pan(double deltaX, double deltaY)
    {
        _viewportX += deltaX / _viewportScale;
        _viewportY += deltaY / _viewportScale;

        // 限制在图像范围内
        ClampViewport();

        UpdateTransforms();
        _viewportChangeTimer?.Stop();
        _viewportChangeTimer?.Start();
    }

    /// <summary>
    /// 缩放视口
    /// </summary>
    public void Zoom(double scaleDelta, double centerX, double centerY)
    {
        double newScale = _viewportScale * scaleDelta;
        newScale = Math.Clamp(newScale, GetMinScale(), GetMaxScale());

        // 以鼠标位置为中心缩放
        double scaleChange = newScale / _viewportScale;
        _viewportX = centerX - (centerX - _viewportX) * scaleChange;
        _viewportY = centerY - (centerY - _viewportY) * scaleChange;
        _viewportScale = newScale;

        ClampViewport();
        UpdateTransforms();

        _viewportChangeTimer?.Stop();
        _viewportChangeTimer?.Start();
    }

    /// <summary>
    /// 重置视口
    /// </summary>
    public void ResetViewport()
    {
        if (_imageWidth == 0 || _imageHeight == 0) return;

        // 适应窗口
        double scaleX = ActualWidth / _imageWidth;
        double scaleY = ActualHeight / _imageHeight;
        _viewportScale = Math.Min(scaleX, scaleY);

        // 居中
        double scaledW = _imageWidth * _viewportScale;
        double scaledH = _imageHeight * _viewportScale;
        _viewportX = (ActualWidth - scaledW) / 2 / _viewportScale;
        _viewportY = (ActualHeight - scaledH) / 2 / _viewportScale;

        UpdateTransforms();
    }

    /// <summary>
    /// 获取当前视口信息
    /// </summary>
    public ViewportInfo GetViewportInfo()
    {
        return new ViewportInfo(
            _viewportX,
            _viewportY,
            ActualWidth / _viewportScale,
            ActualHeight / _viewportScale,
            _viewportScale
        );
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_currentFilePath != null)
        {
            _viewportChangeTimer?.Stop();
            _viewportChangeTimer?.Start();
        }
    }

    private void OnViewportChangeTimerElapsed(object? sender, EventArgs e)
    {
        _viewportChangeTimer?.Stop();
        _ = RefreshTilesAsync();
    }

    private void UpdateTransforms()
    {
        _scaleTransform.ScaleX = _viewportScale;
        _scaleTransform.ScaleY = _viewportScale;
        _translateTransform.X = _viewportX * _viewportScale;
        _translateTransform.Y = _viewportY * _viewportScale;

        if (DebugMode)
        {
            UpdateDebugInfo();
        }
    }

    private void ClampViewport()
    {
        // 限制视口不要移出图像太远
        double margin = 100 / _viewportScale;
        _viewportX = Math.Max(-_imageWidth - margin, Math.Min(ActualWidth / _viewportScale + margin, _viewportX));
        _viewportY = Math.Max(-_imageHeight - margin, Math.Min(ActualHeight / _viewportScale + margin, _viewportY));
    }

    private double GetMinScale()
    {
        if (_imageWidth == 0) return 0.1;
        return Math.Min(ActualWidth / _imageWidth, ActualHeight / _imageHeight) * 0.5;
    }

    private double GetMaxScale()
    {
        return 4.0; // 最大4倍放大
    }

    /// <summary>
    /// 刷新可见 tiles
    /// </summary>
    private async Task RefreshTilesAsync()
    {
        if (_currentFilePath == null || _imageWidth == 0) return;

        // 取消之前的加载任务
        _tileLoadCts?.Cancel();
        _tileLoadCts = new CancellationTokenSource();
        var ct = _tileLoadCts.Token;

        try
        {
            // 选择最佳层级
            int level = TileLoader.SelectBestLevel(_imageWidth, _imageHeight, _viewportScale);

            // 计算可见 tiles
            var viewport = GetViewportInfo();
            var tiles = TileLoader.CalculateVisibleTiles(_imageWidth, _imageHeight, viewport, level);

            // 更新加载进度显示
            int totalTiles = tiles.Count;
            int loadedTiles = 0;

            // 移除不可见的 tiles
            var visibleKeys = tiles.Select(t => new TileKey(_currentFilePath, t.Level, t.X, t.Y)).ToHashSet();
            var keysToRemove = _visibleTiles.Keys.Where(k => !visibleKeys.Contains(k)).ToList();
            foreach (var key in keysToRemove)
            {
                if (_visibleTiles.TryRemove(key, out var img))
                {
                    TileCanvas.Children.Remove(img);
                }
            }

            // 加载新的 tiles
            var tasks = new List<Task>();
            foreach (var tile in tiles)
            {
                if (ct.IsCancellationRequested) break;

                var key = new TileKey(_currentFilePath, tile.Level, tile.X, tile.Y);

                // 检查是否已显示
                if (_visibleTiles.ContainsKey(key)) continue;

                // 检查缓存
                var cached = _tileCache.TryGet(key);
                if (cached != null)
                {
                    AddTileToCanvas(key, cached, tile);
                    continue;
                }

                // 异步加载
                tasks.Add(LoadTileAsync(key, tile, ct, () =>
                {
                    loadedTiles++;
                    Dispatcher.Invoke(() =>
                    {
                        LoadingProgress.Text = $"{loadedTiles}/{totalTiles}";
                    });
                }));
            }

            if (tasks.Count > 0)
            {
                LoadingIndicator.Visibility = Visibility.Visible;
                await Task.WhenAll(tasks);
                LoadingIndicator.Visibility = Visibility.Collapsed;
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
    }

    private async Task LoadTileAsync(TileKey key, TileInfo tile, CancellationToken ct, Action onLoaded)
    {
        await _tileLoadSemaphore.WaitAsync(ct);
        try
        {
            if (ct.IsCancellationRequested) return;

            var bitmap = await Task.Run(() =>
                TileLoader.LoadTile(_currentFilePath!, tile, ct), ct);

            if (bitmap != null && !ct.IsCancellationRequested)
            {
                _tileCache.Put(key, bitmap);

                await Dispatcher.InvokeAsync(() =>
                {
                    if (!ct.IsCancellationRequested)
                    {
                        AddTileToCanvas(key, bitmap, tile);
                        onLoaded();
                    }
                });
            }
        }
        finally
        {
            _tileLoadSemaphore.Release();
        }
    }

    private void AddTileToCanvas(TileKey key, BitmapSource bitmap, TileInfo tile)
    {
        // 检查是否已添加
        if (_visibleTiles.ContainsKey(key)) return;

        var img = new Image
        {
            Source = bitmap,
            Width = tile.Width * (1 << tile.Level) * _viewportScale,
            Height = tile.Height * (1 << tile.Level) * _viewportScale,
            Stretch = Stretch.Fill
        };

        // 设置位置（考虑层级缩放）
        double levelScale = 1 << tile.Level;
        Canvas.SetLeft(img, tile.PixelX * levelScale * _viewportScale);
        Canvas.SetTop(img, tile.PixelY * levelScale * _viewportScale);

        TileCanvas.Children.Add(img);
        _visibleTiles[key] = img;
    }

    private void UpdateDebugInfo()
    {
        var (total, levels) = _tileCache.GetStats();
        DebugInfo.Text = $"Viewport: {_viewportX:F0},{_viewportY:F0} Scale: {_viewportScale:F2}\n" +
                        $"Tiles: {_visibleTiles.Count} visible, {total} cached\n" +
                        $"Levels: {levels}, Image: {_imageWidth}x{_imageHeight}";
    }

    /// <summary>
    /// 清理资源
    /// </summary>
    public void Dispose()
    {
        UnloadImage();
        _tileLoadSemaphore.Dispose();
        _tileLoadCts?.Dispose();
    }
}
