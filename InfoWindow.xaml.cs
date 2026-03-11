using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace ImageViewer
{
    public partial class InfoWindow : Window
    {
        private string? _currentFilePath;

        public InfoWindow()
        {
            InitializeComponent();
        }

        // ─── 标题栏拖拽 ───────────────────────────────────────────────
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        // ─── 更新信息 ─────────────────────────────────────────────────
        public void UpdateInfo(string? filePath, BitmapSource? imageSource)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                ClearInfo();
                return;
            }

            _currentFilePath = filePath;

            try
            {
                var fileInfo = new FileInfo(filePath);

                TxtFileSize.Text = FormatFileSize(fileInfo.Length);
                TxtModifiedTime.Text = fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");
                TxtFilePath.Text = fileInfo.FullName;

                TxtDimensions.Text = imageSource != null
                    ? $"{imageSource.PixelWidth} × {imageSource.PixelHeight}"
                    : "";

                ReadExifInfo(filePath);
            }
            catch (Exception ex)
            {
                TxtFileSize.Text = "读取失败";
                TxtFilePath.Text = ex.Message;
                ClearExifFields();
            }
        }

        public void ClearInfo()
        {
            _currentFilePath = null;
            TxtFileSize.Text = "";
            TxtDimensions.Text = "";
            TxtModifiedTime.Text = "";
            TxtFilePath.Text = "";
            ClearExifFields();
        }

        private void ClearExifFields()
        {
            TxtDateTaken.Text = "";
            TxtCameraMaker.Text = "";
            TxtCameraModel.Text = "";
            TxtLensModel.Text = "";
            TxtAperture.Text = "";
            TxtMaxAperture.Text = "";
            TxtExposureTime.Text = "";
            TxtExposureBias.Text = "";
            TxtISO.Text = "";
            TxtFocalLength.Text = "";
            TxtMeteringMode.Text = "";
            TxtFlash.Text = "";
            TxtWhiteBalance.Text = "";
            TxtBrightness.Text = "";
            TxtExposureProgram.Text = "";
            TxtSoftware.Text = "";
            RowAiPrompt.Visibility = Visibility.Collapsed;
        }

        private void ReadExifInfo(string filePath)
        {
            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.None);
                var metadata = decoder.Frames[0].Metadata as BitmapMetadata;

                if (metadata == null)
                {
                    ClearExifFields();
                    return;
                }

                // 拍摄时间
                string? dateTaken = GetMeta(metadata, "System.Photo.DateTaken");
                if (!string.IsNullOrEmpty(dateTaken) && DateTime.TryParse(dateTaken, out var dt))
                    TxtDateTaken.Text = dt.ToString("yyyy-MM-dd HH:mm:ss");
                else
                    TxtDateTaken.Text = dateTaken ?? "";

                // 相机厂商 / 型号
                TxtCameraMaker.Text = metadata.CameraManufacturer ?? "";
                TxtCameraModel.Text = metadata.CameraModel ?? "";

                // 镜头型号
                TxtLensModel.Text = GetMeta(metadata, "System.Photo.LensModel") ?? "";

                // 光圈值
                TxtAperture.Text = ParseDouble(GetMeta(metadata, "System.Photo.FNumber"), v => $"f/{v:F1}");

                // 最大光圈
                TxtMaxAperture.Text = ParseDouble(GetMeta(metadata, "System.Photo.MaxAperture"), v => $"f/{v:F1}");

                // 曝光时间
                TxtExposureTime.Text = ParseDouble(GetMeta(metadata, "System.Photo.ExposureTime"), v =>
                    v >= 1 ? $"{v:F1} 秒" : $"1/{Math.Round(1 / v)} 秒");

                // 曝光补偿
                TxtExposureBias.Text = ParseDouble(GetMeta(metadata, "System.Photo.ExposureBias"), v =>
                    v == 0 ? "0 EV" : $"{v:+0.##;-0.##} EV");

                // ISO
                TxtISO.Text = GetMeta(metadata, "System.Photo.ISOSpeed") ?? "";

                // 焦距
                TxtFocalLength.Text = ParseDouble(GetMeta(metadata, "System.Photo.FocalLength"), v => $"{v:F0} mm");

                // 测光模式
                string? metering = GetMeta(metadata, "System.Photo.MeteringMode");
                TxtMeteringMode.Text = string.IsNullOrEmpty(metering) ? "" : FormatMeteringMode(metering);

                // 闪光灯
                string? flash = GetMeta(metadata, "System.Photo.Flash");
                TxtFlash.Text = string.IsNullOrEmpty(flash) ? "" : FormatFlash(flash);

                // 白平衡
                string? wb = GetMeta(metadata, "System.Photo.WhiteBalance");
                TxtWhiteBalance.Text = string.IsNullOrEmpty(wb) ? "" : FormatWhiteBalance(wb);

                // 亮度
                TxtBrightness.Text = ParseDouble(GetMeta(metadata, "System.Photo.Brightness"), v => $"{v:F2}");

                // 曝光程序
                string? ep = GetMeta(metadata, "System.Photo.ProgramMode");
                TxtExposureProgram.Text = string.IsNullOrEmpty(ep) ? "" : FormatExposureProgram(ep);

                // 软件工具
                TxtSoftware.Text = metadata.ApplicationName ?? "";

                // AI 提示词检测
                string? comment = metadata.Comment ?? GetMeta(metadata, "System.Comment");
                if (!string.IsNullOrEmpty(comment) && IsAiContent(comment, metadata.ApplicationName))
                {
                    TxtAiPrompt.Text = comment.Length > 600 ? comment[..600] + "…" : comment;
                    RowAiPrompt.Visibility = Visibility.Visible;
                }
                else
                {
                    RowAiPrompt.Visibility = Visibility.Collapsed;
                }
            }
            catch
            {
                ClearExifFields();
            }
        }

        // ─── AI 内容识别 ─────────────────────────────────────────────
        private static bool IsAiContent(string comment, string? software)
        {
            if (!string.IsNullOrEmpty(software))
            {
                var sw = software.ToLowerInvariant();
                if (sw.Contains("stable diffusion") || sw.Contains("comfyui") ||
                    sw.Contains("midjourney") || sw.Contains("dall-e") || sw.Contains("dalle") ||
                    sw.Contains("novelai") || sw.Contains("firefly") || sw.Contains("diffusion"))
                    return true;
            }

            var lc = comment.ToLowerInvariant();
            return lc.Contains("masterpiece") || lc.Contains("highly detailed") ||
                   lc.Contains("positive prompt") || lc.Contains("negative prompt") ||
                   lc.Contains("steps:") || lc.Contains("cfg scale") ||
                   lc.Contains("model hash") || lc.Contains("sampler:");
        }

        // ─── 工具方法 ─────────────────────────────────────────────────
        private static string? GetMeta(BitmapMetadata metadata, string query)
        {
            try
            {
                if (metadata.ContainsQuery(query))
                    return metadata.GetQuery(query)?.ToString();
            }
            catch { }
            return null;
        }

        private static string ParseDouble(string? raw, Func<double, string> format)
        {
            if (!string.IsNullOrEmpty(raw) && double.TryParse(raw, out double v))
                return format(v);
            return "";
        }

        private static string FormatMeteringMode(string mode) => mode switch
        {
            "0" => "未知", "1" => "平均", "2" => "中央重点",
            "3" => "点测", "4" => "多区", "5" => "图案",
            "6" => "部分", "255" => "其他", _ => mode
        };

        private static string FormatFlash(string flash) => flash switch
        {
            "0" => "未使用", "1" => "已闪光",
            "5" => "已闪光（无回光）", "7" => "已闪光（有回光）",
            "9" => "强制闪光", "16" => "强制不闪",
            "24" => "自动未闪", "25" => "自动闪光",
            _ => flash
        };

        private static string FormatWhiteBalance(string wb) => wb switch
        {
            "0" => "自动", "1" => "日光", "2" => "荧光灯",
            "3" => "钨丝灯", "4" => "闪光灯", "9" => "阴天",
            "10" => "阴影", _ => wb
        };

        private static string FormatExposureProgram(string program) => program switch
        {
            "0" => "未定义", "1" => "手动", "2" => "程序自动",
            "3" => "光圈优先", "4" => "快门优先", "5" => "创意程序",
            "6" => "运动程序", "7" => "人像模式", "8" => "风景模式",
            _ => program
        };

        private static string FormatFileSize(long bytes)
        {
            const long KB = 1024, MB = KB * 1024, GB = MB * 1024;
            if (bytes >= GB) return $"{bytes / (double)GB:F2} GB";
            if (bytes >= MB) return $"{bytes / (double)MB:F2} MB";
            if (bytes >= KB) return $"{bytes / (double)KB:F2} KB";
            return $"{bytes} B";
        }

        // ─── 防止关闭，改为隐藏 ──────────────────────────────────────
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            Hide();
            base.OnClosing(e);
        }
    }
}
