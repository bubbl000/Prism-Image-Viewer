using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ImageBrowser
{
    public partial class SaveAsDialog : Window
    {
        private readonly BitmapSource    _bmp;
        private readonly string          _srcPath;
        private readonly bool            _srcIsJpeg;
        private readonly long            _srcFileSize;
        private readonly DispatcherTimer _sizeTimer;

        public string ChosenFormat  { get; private set; } = "png";
        public int    JpegQuality   { get; private set; } = 100;

        /// <summary>原图是 JPG 且质量选 100 时直接复制，不重新编码</summary>
        public bool ShouldCopyOriginal =>
            ChosenFormat == "jpg" && JpegQuality == 100 && _srcIsJpeg;

        public SaveAsDialog(BitmapSource bmp, string srcPath)
        {
            _bmp         = bmp;
            _srcPath     = srcPath;
            _srcIsJpeg   = Path.GetExtension(srcPath).ToLowerInvariant() is ".jpg" or ".jpeg";
            _srcFileSize = File.Exists(srcPath) ? new FileInfo(srcPath).Length : 0;

            _sizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _sizeTimer.Tick += (_, _) => { _sizeTimer.Stop(); EstimateSize(); };

            InitializeComponent();

            RbJpg.IsChecked = _srcIsJpeg;
            RbPng.IsChecked = !_srcIsJpeg;
        }

        // ─── 标题栏 ────────────────────────────────────────────────────
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            => DragMove();

        // ─── 格式切换 ──────────────────────────────────────────────────
        private void Format_Changed(object sender, RoutedEventArgs e)
        {
            if (QualityPanel == null) return;
            bool jpg = RbJpg.IsChecked == true;
            QualityPanel.Visibility = jpg ? Visibility.Visible : Visibility.Collapsed;
            if (jpg) EstimateSize();
        }

        // ─── 质量滑条 ──────────────────────────────────────────────────
        private void SliderQuality_ValueChanged(object sender,
            RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtQualityPct == null) return;
            TxtQualityPct.Text  = $"{(int)SliderQuality.Value}";
            TxtSizePreview.Text = "计算中…";
            _sizeTimer.Stop();
            _sizeTimer.Start();
        }

        // ─── 估算文件大小 ──────────────────────────────────────────────
        private void EstimateSize()
        {
            if (TxtSizePreview == null) return;
            int quality = (int)SliderQuality.Value;

            // 原图 JPG + 质量 100：直接显示原始文件大小
            if (_srcIsJpeg && quality == 100)
            {
                TxtSizePreview.Text = _srcFileSize > 0
                    ? FormatSize(_srcFileSize) + "（原始文件）"
                    : "—";
                TxtSizePreview.Foreground = System.Windows.Media.Brushes.Silver;
                return;
            }

            var  bmp     = _bmp;
            long srcSize = _srcFileSize;
            TxtSizePreview.Text = "计算中…";
            Task.Run(() =>
            {
                try
                {
                    long   estimated = ImageSharpHelper.EstimateJpegSize(bmp, quality);
                    string label     = FormatSize(estimated);
                    bool   tooBig    = srcSize > 0 && estimated > srcSize;
                    if (tooBig) label += "  ↑ 超过原文件";

                    Dispatcher.Invoke(() =>
                    {
                        TxtSizePreview.Text = label;
                        TxtSizePreview.Foreground = tooBig
                            ? new System.Windows.Media.SolidColorBrush(
                                  System.Windows.Media.Color.FromRgb(0xFF, 0x99, 0x33))
                            : System.Windows.Media.Brushes.Silver;
                    });
                }
                catch { Dispatcher.Invoke(() => TxtSizePreview.Text = "—"); }
            });
        }

        private static string FormatSize(long bytes) =>
            bytes >= 1024 * 1024
                ? $"{bytes / 1024.0 / 1024.0:F2} MB"
                : $"{bytes / 1024.0:F1} KB";

        // ─── 按钮 ──────────────────────────────────────────────────────
        private void BtnOK_Click(object sender, RoutedEventArgs e)
        {
            ChosenFormat = RbJpg.IsChecked == true ? "jpg" : "png";
            JpegQuality  = (int)SliderQuality.Value;
            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
            => DialogResult = false;
    }
}
