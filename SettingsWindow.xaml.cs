using Microsoft.Win32;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace ImageViewer
{
    public partial class SettingsWindow : Window
    {
        private readonly MainWindow _mainWindow;
        private bool _loading = true; // 阻止初始化时触发保存

        public SettingsWindow(MainWindow mainWindow)
        {
            _mainWindow = mainWindow;
            InitializeComponent();
            LoadSettings();
            _loading = false;
        }

        // ─── 标题栏 ────────────────────────────────────────────────────
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) return;
            DragMove();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        // ─── 加载设置到 UI ────────────────────────────────────────────
        private void LoadSettings()
        {
            var s = AppSettings.Current;

            // 常规
            ChkHwAccel.IsChecked        = s.HardwareAcceleration;
            ChkRememberPos.IsChecked    = s.RememberPosition;
            ChkPixelMode.IsChecked      = s.OriginalPixelMode;
            ChkRawOriginal.IsChecked    = s.RawOriginalView;

            // 习惯
            ChkMultiWindow.IsChecked  = s.MultiWindow;

            RbWheelZoom.IsChecked   = s.WheelMode == "zoom";
            RbWheelPage.IsChecked   = s.WheelMode == "page";
            RbWheelScroll.IsChecked = s.WheelMode == "scroll";

            // 语言
            foreach (System.Windows.Controls.ComboBoxItem item in CboLanguage.Items)
            {
                if (item.Tag as string == s.Language)
                {
                    CboLanguage.SelectedItem = item;
                    break;
                }
            }
        }

        // ─── 左侧导航 ─────────────────────────────────────────────────
        private void NavFileAssoc_Click(object sender, MouseButtonEventArgs e) => ShowSection(0);
        private void NavGeneral_Click(object sender, MouseButtonEventArgs e)   => ShowSection(1);
        private void NavHabits_Click(object sender, MouseButtonEventArgs e)    => ShowSection(2);

        private void ShowSection(int index)
        {
            ScrollFileAssoc.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
            ScrollGeneral.Visibility   = index == 1 ? Visibility.Visible : Visibility.Collapsed;
            ScrollHabits.Visibility    = index == 2 ? Visibility.Visible : Visibility.Collapsed;

            SetNavActive(NavFileAssoc, index == 0);
            SetNavActive(NavGeneral,   index == 1);
            SetNavActive(NavHabits,    index == 2);
        }

        private static void SetNavActive(System.Windows.Controls.Border nav, bool active)
        {
            nav.Background = active
                ? new System.Windows.Media.SolidColorBrush(
                      System.Windows.Media.Color.FromArgb(0x1A, 0x90, 0xC2, 0x08))
                : System.Windows.Media.Brushes.Transparent;

            var tb = nav.Child as System.Windows.Controls.TextBlock;
            if (tb != null) tb.Foreground = active
                ? System.Windows.Media.Brushes.White
                : new System.Windows.Media.SolidColorBrush(
                      System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88));
        }

        // ─── 常规设置 ─────────────────────────────────────────────────
        private void ChkHwAccel_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            AppSettings.Current.HardwareAcceleration = ChkHwAccel.IsChecked == true;
            AppSettings.Current.Save();
            _mainWindow.ApplyHardwareAcceleration(AppSettings.Current.HardwareAcceleration);
        }

        private void ChkRememberPos_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            AppSettings.Current.RememberPosition = ChkRememberPos.IsChecked == true;
            AppSettings.Current.Save();
        }

        private void ChkPixelMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            AppSettings.Current.OriginalPixelMode = ChkPixelMode.IsChecked == true;
            AppSettings.Current.Save();
            _mainWindow.ApplyPixelMode(AppSettings.Current.OriginalPixelMode);
        }

        private void ChkRawOriginal_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            AppSettings.Current.RawOriginalView = ChkRawOriginal.IsChecked == true;
            AppSettings.Current.Save();
            _mainWindow.ApplyRawOriginalView();
        }

        // ─── 习惯设置 ─────────────────────────────────────────────────
        private void ChkMultiWindow_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            AppSettings.Current.MultiWindow = ChkMultiWindow.IsChecked == true;
            AppSettings.Current.Save();
        }

        private void WheelMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            AppSettings.Current.WheelMode = RbWheelPage.IsChecked == true ? "page"
                : RbWheelScroll.IsChecked == true ? "scroll" : "zoom";
            AppSettings.Current.Save();
        }

        // ─── 语言 ─────────────────────────────────────────────────────
        private void CboLanguage_SelectionChanged(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (CboLanguage.SelectedItem is not System.Windows.Controls.ComboBoxItem item) return;
            string lang = item.Tag as string ?? "zh-CN";

            LocalizationManager.Apply(lang);
            AppSettings.Current.Language = lang;
            AppSettings.Current.Save();
        }

        // ─── 文件关联 ─────────────────────────────────────────────────
        private void BtnAssocAll_Click(object sender, RoutedEventArgs e)
        {
            ChkJpg.IsChecked = ChkPng.IsChecked = ChkBmp.IsChecked = ChkGif.IsChecked =
            ChkTiff.IsChecked = ChkWebp.IsChecked = ChkIco.IsChecked = ChkHeic.IsChecked =
            ChkHeif.IsChecked = ChkAvif.IsChecked = ChkSvg.IsChecked = ChkRaw.IsChecked =
            ChkPsd.IsChecked = ChkPsb.IsChecked = true;
        }

        private void BtnAssocNone_Click(object sender, RoutedEventArgs e)
        {
            ChkJpg.IsChecked = ChkPng.IsChecked = ChkBmp.IsChecked = ChkGif.IsChecked =
            ChkTiff.IsChecked = ChkWebp.IsChecked = ChkIco.IsChecked = ChkHeic.IsChecked =
            ChkHeif.IsChecked = ChkAvif.IsChecked = ChkSvg.IsChecked = ChkRaw.IsChecked =
            ChkPsd.IsChecked = ChkPsb.IsChecked = false;
        }

        private void BtnAssociate_Click(object sender, RoutedEventArgs e)
        {
            var exts = new List<string>();
            if (ChkJpg.IsChecked  == true) { exts.Add(".jpg"); exts.Add(".jpeg"); }
            if (ChkPng.IsChecked  == true) exts.Add(".png");
            if (ChkBmp.IsChecked  == true) exts.Add(".bmp");
            if (ChkGif.IsChecked  == true) exts.Add(".gif");
            if (ChkTiff.IsChecked == true) { exts.Add(".tiff"); exts.Add(".tif"); }
            if (ChkWebp.IsChecked == true) exts.Add(".webp");
            if (ChkIco.IsChecked  == true) exts.Add(".ico");
            if (ChkHeic.IsChecked == true) exts.Add(".heic");
            if (ChkHeif.IsChecked == true) exts.Add(".heif");
            if (ChkAvif.IsChecked == true) exts.Add(".avif");
            if (ChkSvg.IsChecked  == true) exts.Add(".svg");
            if (ChkRaw.IsChecked  == true) exts.Add(".raw");
            if (ChkPsd.IsChecked  == true) exts.Add(".psd");
            if (ChkPsb.IsChecked  == true) exts.Add(".psb");

            if (exts.Count == 0)
            {
                MessageBox.Show(LocalizationManager.Get("FileAssoc.NoSelection"),
                    LocalizationManager.Get("Str.Tip"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                string exePath = Assembly.GetExecutingAssembly().Location;
                string exeDir  = System.IO.Path.GetDirectoryName(exePath) ?? "";
                string exeName = System.IO.Path.GetFileNameWithoutExtension(exePath) + ".exe";
                string fullExe = System.IO.Path.Combine(exeDir, exeName);
                if (!System.IO.File.Exists(fullExe)) fullExe = exePath;

                foreach (string ext in exts) RegisterExtension(ext, fullExe);
                SHChangeNotify(0x8000000, 0x1000, IntPtr.Zero, IntPtr.Zero);

                MessageBox.Show($"已关联 {exts.Count} 种格式。",
                    LocalizationManager.Get("FileAssoc.Title"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"关联失败：{ex.Message}",
                    LocalizationManager.Get("Str.Error"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // IThumbnailProvider IID（Shell 扩展固定值）
        private const string ThumbnailProviderIid = "{E357FCCD-A995-4576-B01F-234630154E96}";
        // WICThumbnailProvider CLSID（Windows 内置，用 WIC 解码生成缩略图）
        private const string WicThumbnailClsid    = "{E9E4E3DC-3F3A-4056-9988-5F8A61D6D7E6}";

        private static void RegisterExtension(string ext, string exePath)
        {
            string progId = "ImageViewer" + ext.TrimStart('.').ToUpper();

            using (var extKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ext))
            {
                extKey.SetValue("", progId);
                // 告知 Explorer 这是图片文件（影响搜索分类、预览等）
                extKey.SetValue("PerceivedType", "image");
                // 注册 WIC 缩略图生成器（.psd/.psb 安装 WIC 解码器后即可显示缩略图）
                using var shellexKey = extKey.CreateSubKey(@"ShellEx\" + ThumbnailProviderIid);
                shellexKey.SetValue("", WicThumbnailClsid);
            }

            using (var progKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + progId))
            {
                progKey.SetValue("", $"ImageViewer {ext.ToUpper()} File");
                using var iconKey = progKey.CreateSubKey("DefaultIcon");
                iconKey.SetValue("", $"\"{exePath}\",0");
                using var cmdKey = progKey.CreateSubKey(@"shell\open\command");
                cmdKey.SetValue("", $"\"{exePath}\" \"%1\"");
            }
        }

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int wEventId, int uFlags,
            IntPtr dwItem1, IntPtr dwItem2);
    }
}
