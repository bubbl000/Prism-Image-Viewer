using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Xps.Packaging;
using System.Printing;
using System.Windows.Documents;
using Wpf.Controls.PanAndZoom;

namespace ImageBrowser;

public partial class MainWindow
{
    // ─── 右键菜单 ─────────────────────────────────────────────────
    private void ZoomBorder_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_currentIndex < 0 || _currentIndex >= _imageFiles.Count)
            e.Handled = true;
    }

    // ─── 打开所在文件夹 ───────────────────────────────────────────
    private void MenuOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0 || _currentIndex >= _imageFiles.Count) return;
        var folder = Path.GetDirectoryName(_imageFiles[_currentIndex]);
        if (folder != null)
            Process.Start("explorer.exe", $"/select,\"{_imageFiles[_currentIndex]}\"");
    }

    // ─── 复制图片 ─────────────────────────────────────────────────
    private void MenuCopyImage_Click(object sender, RoutedEventArgs e)
    {
        if (MainImageViewer.Source is BitmapSource bmp)
        {
            Clipboard.SetImage(bmp);
        }
    }

    // ─── 复制文件 ─────────────────────────────────────────────────
    private void MenuCopyFile_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0 || _currentIndex >= _imageFiles.Count) return;
        var files = new System.Collections.Specialized.StringCollection();
        files.Add(_imageFiles[_currentIndex]);
        Clipboard.SetFileDropList(files);
    }

    // ─── 删除文件 ─────────────────────────────────────────────────
    private void MenuDeleteFile_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0 || _currentIndex >= _imageFiles.Count) return;
        var file = _imageFiles[_currentIndex];

        var result = MessageBox.Show($"确定要删除文件吗?\n{Path.GetFileName(file)}",
            "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                File.Delete(file);
                _imageFiles.RemoveAt(_currentIndex);
                if (_imageFiles.Count == 0)
                {
                    ClearImageState();
                }
                else
                {
                    ShowImage(Math.Min(_currentIndex, _imageFiles.Count - 1));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"删除失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    // ─── 顺时针旋转 90° ───────────────────────────────────────────
    private void MenuRotateClockwise_Click(object sender, RoutedEventArgs e)
    {
        _currentRotation = (_currentRotation + 90) % 360;
        ApplyRotation();
    }

    // ─── 逆时针旋转 90° ───────────────────────────────────────────
    private void MenuRotateCounterClockwise_Click(object sender, RoutedEventArgs e)
    {
        _currentRotation = (_currentRotation - 90 + 360) % 360;
        ApplyRotation();
    }

    // ─── 顺时针旋转 90°（带动画）──────────────────────────────────
    private void MenuRotateClockwiseAnim_Click(object sender, RoutedEventArgs e)
    {
        AnimateRotation(90);
    }

    // ─── 逆时针旋转 90°（带动画）──────────────────────────────────
    private void MenuRotateCounterClockwiseAnim_Click(object sender, RoutedEventArgs e)
    {
        AnimateRotation(-90);
    }

    // ─── 实际应用旋转（供动画完成回调使用）────────────────────────
    private void ApplyRotation()
    {
        // 设置变换原点为中心
        MainImageViewer.RenderTransformOrigin = new Point(0.5, 0.5);

        // 创建旋转变换
        var rotateTransform = new RotateTransform(_currentRotation);
        MainImageViewer.RenderTransform = rotateTransform;

        // 根据旋转角度调整布局
        UpdateLayoutForRotation();
    }

    // ─── 更新布局以适应旋转 ───────────────────────────────────────
    private void UpdateLayoutForRotation()
    {
        // 获取当前图片的实际尺寸
        if (MainImageViewer.Source is not BitmapSource bmp) return;

        var imgWidth = bmp.PixelWidth;
        var imgHeight = bmp.PixelHeight;

        // 根据旋转角度交换宽高
        bool isRotated90Or270 = _currentRotation % 180 != 0;
        var displayWidth = isRotated90Or270 ? imgHeight : imgWidth;
        var displayHeight = isRotated90Or270 ? imgWidth : imgHeight;

        // 更新鸟眼图
        UpdateBirdEye();
    }

    // ─── 带动画的旋转 ─────────────────────────────────────────────
    private void AnimateRotation(double angleDelta)
    {
        var targetRotation = (_currentRotation + angleDelta) % 360;

        // 创建动画
        var anim = new DoubleAnimation(_currentRotation, targetRotation,
            TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };

        // 设置变换
        MainImageViewer.RenderTransformOrigin = new Point(0.5, 0.5);
        var rotateTransform = new RotateTransform(_currentRotation);
        MainImageViewer.RenderTransform = rotateTransform;

        // 启动动画
        rotateTransform.BeginAnimation(RotateTransform.AngleProperty, anim);

        // 更新当前旋转角度
        _currentRotation = targetRotation;

        // 动画完成后更新布局
        anim.Completed += (_, _) => UpdateLayoutForRotation();
    }

    // ─── 重置旋转 ─────────────────────────────────────────────────
    private void MenuResetRotation_Click(object sender, RoutedEventArgs e)
    {
        _currentRotation = 0;
        ApplyRotation();
    }

    // ─── 实际大小 ─────────────────────────────────────────────────
    private void MenuActualSize_Click(object sender, RoutedEventArgs e)
    {
        ZoomBorder.Reset();
    }

    // ─── 适应窗口 ─────────────────────────────────────────────────
    private void MenuFitToWindow_Click(object sender, RoutedEventArgs e)
    {
        ZoomBorder.Reset();
        ZoomBorder.Uniform();
    }

    // ─── 填充窗口 ─────────────────────────────────────────────────
    private void MenuFillWindow_Click(object sender, RoutedEventArgs e)
    {
        ZoomBorder.Reset();
        ZoomBorder.Fill();
    }

    // ─── 全屏切换 ─────────────────────────────────────────────────
    private void MenuToggleFullscreen_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    // ─── 置顶切换 ─────────────────────────────────────────────────
    private void MenuToggleTopmost_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
    }

    // ─── 图片信息 ─────────────────────────────────────────────────
    private void MenuImageInfo_Click(object sender, RoutedEventArgs e)
    {
        BtnInfo_Click(sender, e);
    }

    // ─── 打印 ─────────────────────────────────────────────────────
    private void MenuPrint_Click(object sender, RoutedEventArgs e)
    {
        if (MainImageViewer.Source is not BitmapSource bmp) return;

        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return;

        // 创建打印文档
        var printDoc = new FixedDocument();
        var pageContent = new PageContent();
        var fixedPage = new FixedPage();

        // 创建图像元素
        var image = new System.Windows.Controls.Image
        {
            Source = bmp,
            Stretch = Stretch.Uniform,
            Width = dialog.PrintableAreaWidth,
            Height = dialog.PrintableAreaHeight
        };

        fixedPage.Children.Add(image);
        pageContent.Child = fixedPage;
        printDoc.Pages.Add(pageContent);

        dialog.PrintDocument(printDoc.DocumentPaginator, "打印图片");
    }

    // ─── 重命名 ───────────────────────────────────────────────────
    private void MenuRename_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0 || _currentIndex >= _imageFiles.Count) return;
        var oldPath = _imageFiles[_currentIndex];
        var oldName = Path.GetFileNameWithoutExtension(oldPath);
        var ext = Path.GetExtension(oldPath);

        // 简单的重命名对话框
        var dialog = new InputDialog("重命名", "请输入新文件名:", oldName);
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
        {
            var newName = dialog.InputText + ext;
            var newPath = Path.Combine(Path.GetDirectoryName(oldPath)!, newName);

            try
            {
                File.Move(oldPath, newPath);
                _imageFiles[_currentIndex] = newPath;
                UpdateInfoWindow();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"重命名失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    // ─── 另存为 ───────────────────────────────────────────────────
    private void MenuSaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0 || _currentIndex >= _imageFiles.Count) return;
        var currentFile = _imageFiles[_currentIndex];

        var dialog = new SaveFileDialog
        {
            FileName = Path.GetFileName(currentFile),
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tiff|所有文件|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                File.Copy(currentFile, dialog.FileName, true);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    // ─── 复制 ─────────────────────────────────────────────────────
    private void MenuCopy_Click(object sender, RoutedEventArgs e)
    {
        MenuCopyFile_Click(sender, e);
    }

    // ─── 设为壁纸 ─────────────────────────────────────────────────
    private void MenuSetWallpaper_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0 || _currentIndex >= _imageFiles.Count) return;
        var filePath = _imageFiles[_currentIndex];

        try
        {
            // 使用 Windows API 设置壁纸
            SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, filePath, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"设置壁纸失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ─── 删除 ─────────────────────────────────────────────────────
    private void MenuDelete_Click(object sender, RoutedEventArgs e)
    {
        MenuDeleteFile_Click(sender, e);
    }

    // Windows API 用于设置壁纸
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);

    private const int SPI_SETDESKWALLPAPER = 20;
    private const int SPIF_UPDATEINIFILE = 0x01;
    private const int SPIF_SENDCHANGE = 0x02;
}
