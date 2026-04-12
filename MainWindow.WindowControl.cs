using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ImageBrowser;

public partial class MainWindow
{
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

    public void ApplyFullscreenBgSettings()
    {
        if (!_isFullScreen) return;
        var s = AppSettings.Current;
        byte alpha = (byte)(Math.Clamp(s.FullscreenBgOpacity, 0, 1) * 255);
        byte rgb   = s.FullscreenBgType == "black" ? (byte)0 : (byte)0x1A;
        MainBorder.Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb(alpha, rgb, rgb, rgb));
    }
}
