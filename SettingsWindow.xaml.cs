using System.Windows;
using System.Windows.Input;

namespace ImageViewer
{
    public partial class SettingsWindow : Window
    {
        private MainWindow? _mainWindow;

        public SettingsWindow()
        {
            InitializeComponent();
        }

        public SettingsWindow(MainWindow mainWindow) : this()
        {
            _mainWindow = mainWindow;
            LoadSettings();
        }

        private void LoadSettings()
        {
            if (_mainWindow == null) return;

            ChkThumbAlwaysOn.IsChecked = _mainWindow.ThumbAlwaysOn;
            ChkSmartThumb.IsChecked = _mainWindow.SmartThumb;
            ChkBirdEye.IsChecked = _mainWindow.ShowBirdEye;
            ChkFolderTraverse.IsChecked = _mainWindow.FolderTraverse;

            // 互斥检查
            UpdateToggleStates();
        }

        private void UpdateToggleStates()
        {
            // 缩略图栏和智能展示互斥
            if (ChkThumbAlwaysOn.IsChecked == true)
            {
                ChkSmartThumb.IsEnabled = false;
                ChkSmartThumb.Opacity = 0.5;
            }
            else
            {
                ChkSmartThumb.IsEnabled = true;
                ChkSmartThumb.Opacity = 1.0;
            }

            if (ChkSmartThumb.IsChecked == true)
            {
                ChkThumbAlwaysOn.IsEnabled = false;
                ChkThumbAlwaysOn.Opacity = 0.5;
            }
            else
            {
                ChkThumbAlwaysOn.IsEnabled = true;
                ChkThumbAlwaysOn.Opacity = 1.0;
            }
        }

        // ─── 标题栏拖拽 ───────────────────────────────────────────────
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                // 设置窗口不允许最大化，忽略双击
                return;
            }
            DragMove();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // ─── 设置项事件 ───────────────────────────────────────────────
        private void ChkThumbAlwaysOn_Checked(object sender, RoutedEventArgs e)
        {
            _mainWindow?.SetThumbAlwaysOn(true);
            UpdateToggleStates();
        }

        private void ChkThumbAlwaysOn_Unchecked(object sender, RoutedEventArgs e)
        {
            _mainWindow?.SetThumbAlwaysOn(false);
            UpdateToggleStates();
        }

        private void ChkSmartThumb_Checked(object sender, RoutedEventArgs e)
        {
            _mainWindow?.SetSmartThumb(true);
            UpdateToggleStates();
        }

        private void ChkSmartThumb_Unchecked(object sender, RoutedEventArgs e)
        {
            _mainWindow?.SetSmartThumb(false);
            UpdateToggleStates();
        }

        private void ChkBirdEye_Checked(object sender, RoutedEventArgs e)
        {
            _mainWindow?.SetBirdEye(true);
        }

        private void ChkBirdEye_Unchecked(object sender, RoutedEventArgs e)
        {
            _mainWindow?.SetBirdEye(false);
        }

        private void ChkFolderTraverse_Checked(object sender, RoutedEventArgs e)
        {
            _mainWindow?.SetFolderTraverse(true);
        }

        private void ChkFolderTraverse_Unchecked(object sender, RoutedEventArgs e)
        {
            _mainWindow?.SetFolderTraverse(false);
        }
    }
}
