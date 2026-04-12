using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ImageBrowser;

public partial class MainWindow
{
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
}
