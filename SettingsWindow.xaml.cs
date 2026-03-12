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
            ChkAlwaysOnTop.IsChecked  = s.AlwaysOnTop;
            ChkSmartToolbar.IsChecked = s.SmartToolbar;
            ChkRememberPos.IsChecked  = s.RememberPosition;
            ChkPixelMode.IsChecked    = s.OriginalPixelMode;

            RbBgDark.IsChecked  = s.FullscreenBgType == "dark";
            RbBgBlack.IsChecked = s.FullscreenBgType == "black";
            SliderOpacity.Value = s.FullscreenBgOpacity;
            TxtOpacity.Text     = $"{(int)(s.FullscreenBgOpacity * 100)}%";

            // 习惯
            ChkMultiWindow.IsChecked  = s.MultiWindow;
            RbRotManual.IsChecked = s.RotationMode == "manual";
            RbRotKeep.IsChecked   = s.RotationMode == "keep";
            ChkLoopWithin.IsChecked   = s.LoopWithinFolder;
            ChkShowLoading.IsChecked  = s.ShowLoadingIndicator;
            ChkAutoNaming.IsChecked   = s.AutoNaming;

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
        private void ChkAlwaysOnTop_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            AppSettings.Current.AlwaysOnTop = ChkAlwaysOnTop.IsChecked == true;
            AppSettings.Current.Save();
            _mainWindow.ApplyAlwaysOnTop(AppSettings.Current.AlwaysOnTop);
        }

        private void ChkSmartToolbar_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            AppSettings.Current.SmartToolbar = ChkSmartToolbar.IsChecked == true;
            AppSettings.Current.Save();
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

        private void BgType_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            AppSettings.Current.FullscreenBgType = RbBgBlack.IsChecked == true ? "black" : "dark";
            AppSettings.Current.Save();
            _mainWindow.ApplyFullscreenBgSettings();
        }

        private void SliderOpacity_ValueChanged(object sender,
            System.Windows.RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtOpacity == null) return;
            TxtOpacity.Text = $"{(int)(SliderOpacity.Value * 100)}%";
            if (_loading) return;
            AppSettings.Current.FullscreenBgOpacity = SliderOpacity.Value;
            AppSettings.Current.Save();
            _mainWindow.ApplyFullscreenBgSettings();
        }

        // ─── 习惯设置 ─────────────────────────────────────────────────
        private void ChkMultiWindow_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            AppSettings.Current.MultiWindow = ChkMultiWindow.IsChecked == true;
            AppSettings.Current.Save();
        }

        private void RotMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            AppSettings.Current.RotationMode = RbRotKeep.IsChecked == true ? "keep" : "manual";
            AppSettings.Current.Save();
        }

        private void ChkLoopWithin_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            AppSettings.Current.LoopWithinFolder = ChkLoopWithin.IsChecked == true;
            AppSettings.Current.Save();
        }

        private void ChkShowLoading_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            AppSettings.Current.ShowLoadingIndicator = ChkShowLoading.IsChecked == true;
            AppSettings.Current.Save();
        }

        private void ChkAutoNaming_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            AppSettings.Current.AutoNaming = ChkAutoNaming.IsChecked == true;
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
            ChkHeif.IsChecked = ChkAvif.IsChecked = ChkSvg.IsChecked = ChkRaw.IsChecked = true;
        }

        private void BtnAssocNone_Click(object sender, RoutedEventArgs e)
        {
            ChkJpg.IsChecked = ChkPng.IsChecked = ChkBmp.IsChecked = ChkGif.IsChecked =
            ChkTiff.IsChecked = ChkWebp.IsChecked = ChkIco.IsChecked = ChkHeic.IsChecked =
            ChkHeif.IsChecked = ChkAvif.IsChecked = ChkSvg.IsChecked = ChkRaw.IsChecked = false;
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

        private static void RegisterExtension(string ext, string exePath)
        {
            string progId = "ImageViewer" + ext.TrimStart('.').ToUpper();
            using (var extKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ext))
                extKey.SetValue("", progId);
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
