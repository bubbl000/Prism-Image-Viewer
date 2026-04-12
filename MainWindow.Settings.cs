using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageBrowser;

public partial class MainWindow
{
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
}
