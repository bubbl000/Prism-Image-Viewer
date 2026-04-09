using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ImageBrowser.Utils;

/// <summary>
/// 视口管理器
/// 智能计算图片显示区域、缩放、平移
/// 参考 ImageGlass 的视口管理实现
/// </summary>
public class ViewportManager
{
    private readonly FrameworkElement _viewport;
    private readonly Image _image;

    // 内边距
    public Thickness Padding { get; set; } = new Thickness(0);

    // 当前缩放因子
    public double ZoomFactor { get; private set; } = 1.0;

    // 原始图片尺寸
    public double SourceWidth { get; private set; }
    public double SourceHeight { get; private set; }

    // 视口尺寸
    public double ViewportWidth => _viewport.ActualWidth - Padding.Left - Padding.Right;
    public double ViewportHeight => _viewport.ActualHeight - Padding.Top - Padding.Bottom;

    // 绘图区域
    public Rect DrawingArea => new Rect(
        Padding.Left,
        Padding.Top,
        ViewportWidth,
        ViewportHeight);

    public ViewportManager(FrameworkElement viewport, Image image)
    {
        _viewport = viewport;
        _image = image;
    }

    /// <summary>
    /// 设置源图片尺寸
    /// </summary>
    public void SetSourceSize(double width, double height)
    {
        SourceWidth = width;
        SourceHeight = height;
    }

    /// <summary>
    /// 判断图片是否小于视口（适合窗口）
    /// </summary>
    public bool IsViewingSizeSmallerThanViewport
    {
        get
        {
            if (SourceWidth <= 0 || SourceHeight <= 0) return false;

            var scaledWidth = SourceWidth * ZoomFactor;
            var scaledHeight = SourceHeight * ZoomFactor;

            return scaledWidth <= ViewportWidth && scaledHeight <= ViewportHeight;
        }
    }

    /// <summary>
    /// 判断图片是否超出视口
    /// </summary>
    public bool IsImageLargerThanViewport
    {
        get
        {
            if (SourceWidth <= 0 || SourceHeight <= 0) return false;

            var scaledWidth = SourceWidth * ZoomFactor;
            var scaledHeight = SourceHeight * ZoomFactor;

            return scaledWidth > ViewportWidth || scaledHeight > ViewportHeight;
        }
    }

    /// <summary>
    /// 计算适合窗口的缩放因子
    /// </summary>
    public double CalculateFitToWindowZoom()
    {
        if (SourceWidth <= 0 || SourceHeight <= 0) return 1.0;

        var availableWidth = ViewportWidth;
        var availableHeight = ViewportHeight;

        var scaleX = availableWidth / SourceWidth;
        var scaleY = availableHeight / SourceHeight;

        // 取较小值，确保图片完整显示
        return Math.Min(scaleX, scaleY);
    }

    /// <summary>
    /// 计算适合宽度的缩放因子
    /// </summary>
    public double CalculateFitToWidthZoom()
    {
        if (SourceWidth <= 0) return 1.0;
        return ViewportWidth / SourceWidth;
    }

    /// <summary>
    /// 计算适合高度的缩放因子
    /// </summary>
    public double CalculateFitToHeightZoom()
    {
        if (SourceHeight <= 0) return 1.0;
        return ViewportHeight / SourceHeight;
    }

    /// <summary>
    /// 计算填充视口的缩放因子
    /// </summary>
    public double CalculateFillZoom()
    {
        if (SourceWidth <= 0 || SourceHeight <= 0) return 1.0;

        var scaleX = ViewportWidth / SourceWidth;
        var scaleY = ViewportHeight / SourceHeight;

        // 取较大值，填满视口
        return Math.Max(scaleX, scaleY);
    }

    /// <summary>
    /// 计算实际显示尺寸
    /// </summary>
    public (double width, double height) GetDisplayedSize()
    {
        return (SourceWidth * ZoomFactor, SourceHeight * ZoomFactor);
    }

    /// <summary>
    /// 计算图片在视口中的位置（居中）
    /// </summary>
    public Point CalculateCenteredPosition()
    {
        var (displayedWidth, displayedHeight) = GetDisplayedSize();

        var x = Padding.Left + (ViewportWidth - displayedWidth) / 2;
        var y = Padding.Top + (ViewportHeight - displayedHeight) / 2;

        return new Point(x, y);
    }

    /// <summary>
    /// 计算限制在视口内的偏移量
    /// </summary>
    public Vector ClampOffset(double offsetX, double offsetY)
    {
        var (displayedWidth, displayedHeight) = GetDisplayedSize();

        // 如果图片小于视口，居中显示
        if (displayedWidth <= ViewportWidth)
        {
            offsetX = (ViewportWidth - displayedWidth) / 2;
        }
        else
        {
            // 限制平移范围
            var minX = ViewportWidth - displayedWidth - Padding.Right;
            var maxX = Padding.Left;
            offsetX = Math.Clamp(offsetX, minX, maxX);
        }

        if (displayedHeight <= ViewportHeight)
        {
            offsetY = (ViewportHeight - displayedHeight) / 2;
        }
        else
        {
            var minY = ViewportHeight - displayedHeight - Padding.Bottom;
            var maxY = Padding.Top;
            offsetY = Math.Clamp(offsetY, minY, maxY);
        }

        return new Vector(offsetX, offsetY);
    }

    /// <summary>
    /// 计算缩放后的偏移量（保持指定点在视口中的位置）
    /// </summary>
    public Vector CalculateZoomOffset(double newZoom, Point zoomCenter, double currentOffsetX, double currentOffsetY)
    {
        var scaleRatio = newZoom / ZoomFactor;

        // 计算新的偏移，使 zoomCenter 保持在同一位置
        var newOffsetX = zoomCenter.X - (zoomCenter.X - currentOffsetX) * scaleRatio;
        var newOffsetY = zoomCenter.Y - (zoomCenter.Y - currentOffsetY) * scaleRatio;

        // 限制在视口内
        return ClampOffset(newOffsetX, newOffsetY);
    }

    /// <summary>
    /// 设置缩放因子
    /// </summary>
    public void SetZoom(double zoom)
    {
        ZoomFactor = Math.Max(0.01, zoom);
    }

    /// <summary>
    /// 应用适合窗口
    /// </summary>
    public void FitToWindow()
    {
        ZoomFactor = CalculateFitToWindowZoom();
    }

    /// <summary>
    /// 应用适合宽度
    /// </summary>
    public void FitToWidth()
    {
        ZoomFactor = CalculateFitToWidthZoom();
    }

    /// <summary>
    /// 应用适合高度
    /// </summary>
    public void FitToHeight()
    {
        ZoomFactor = CalculateFitToHeightZoom();
    }

    /// <summary>
    /// 应用填充视口
    /// </summary>
    public void FillViewport()
    {
        ZoomFactor = CalculateFillZoom();
    }

    /// <summary>
    /// 应用实际像素（100%）
    /// </summary>
    public void ActualSize()
    {
        ZoomFactor = 1.0;
    }

    /// <summary>
    /// 将视口坐标转换为图片坐标
    /// </summary>
    public Point ViewportToImage(Point viewportPoint, double offsetX, double offsetY)
    {
        var imageX = (viewportPoint.X - offsetX) / ZoomFactor;
        var imageY = (viewportPoint.Y - offsetY) / ZoomFactor;
        return new Point(imageX, imageY);
    }

    /// <summary>
    /// 将图片坐标转换为视口坐标
    /// </summary>
    public Point ImageToViewport(Point imagePoint, double offsetX, double offsetY)
    {
        var viewportX = imagePoint.X * ZoomFactor + offsetX;
        var viewportY = imagePoint.Y * ZoomFactor + offsetY;
        return new Point(viewportX, viewportY);
    }

    /// <summary>
    /// 检查点是否在图片区域内
    /// </summary>
    public bool IsPointInImage(Point viewportPoint, double offsetX, double offsetY)
    {
        var imagePoint = ViewportToImage(viewportPoint, offsetX, offsetY);
        return imagePoint.X >= 0 && imagePoint.X <= SourceWidth &&
               imagePoint.Y >= 0 && imagePoint.Y <= SourceHeight;
    }

    /// <summary>
    /// 获取图片边界矩形（在视口坐标系中）
    /// </summary>
    public Rect GetImageBounds(double offsetX, double offsetY)
    {
        var (displayedWidth, displayedHeight) = GetDisplayedSize();
        return new Rect(offsetX, offsetY, displayedWidth, displayedHeight);
    }

    /// <summary>
    /// 获取可见区域（在图片坐标系中）
    /// </summary>
    public Rect GetVisibleRegion(double offsetX, double offsetY)
    {
        var topLeft = ViewportToImage(new Point(Padding.Left, Padding.Top), offsetX, offsetY);
        var bottomRight = ViewportToImage(
            new Point(Padding.Left + ViewportWidth, Padding.Top + ViewportHeight),
            offsetX, offsetY);

        return new Rect(
            Math.Max(0, topLeft.X),
            Math.Max(0, topLeft.Y),
            Math.Min(SourceWidth, bottomRight.X) - Math.Max(0, topLeft.X),
            Math.Min(SourceHeight, bottomRight.Y) - Math.Max(0, topLeft.Y));
    }

    /// <summary>
    /// 计算最佳初始缩放（根据设置）
    /// </summary>
    public double CalculateInitialZoom(ZoomMode mode)
    {
        return mode switch
        {
            ZoomMode.FitToWindow => CalculateFitToWindowZoom(),
            ZoomMode.FitToWidth => CalculateFitToWidthZoom(),
            ZoomMode.FitToHeight => CalculateFitToHeightZoom(),
            ZoomMode.Fill => CalculateFillZoom(),
            ZoomMode.ActualSize => 1.0,
            _ => CalculateFitToWindowZoom()
        };
    }
}

/// <summary>
/// 缩放模式
/// </summary>
public enum ZoomMode
{
    /// <summary>
    /// 适合窗口
    /// </summary>
    FitToWindow,

    /// <summary>
    /// 适合宽度
    /// </summary>
    FitToWidth,

    /// <summary>
    /// 适合高度
    /// </summary>
    FitToHeight,

    /// <summary>
    /// 填充视口
    /// </summary>
    Fill,

    /// <summary>
    /// 实际像素（100%）
    /// </summary>
    ActualSize,

    /// <summary>
    /// 自动（根据图片大小智能选择）
    /// </summary>
    Auto
}
