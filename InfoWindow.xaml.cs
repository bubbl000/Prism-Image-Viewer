using ImageBrowser.Models;
using ImageBrowser.Services;
using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace ImageBrowser
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

        // ─── 更新信息（使用新的元数据服务）─────────────────────────────
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
                // 使用新的元数据服务加载完整元数据
                var metadata = MetadataService.LoadMetadata(filePath);
                
                if (metadata != null)
                {
                    DisplayMetadata(metadata);
                }
                else
                {
                    // 回退到基本显示
                    DisplayBasicInfo(filePath, imageSource);
                }
            }
            catch (Exception ex)
            {
                TxtFileSize.Text = "读取失败";
                TxtFilePath.Text = ex.Message;
                ClearExifFields();
            }
        }

        // ─── 显示完整元数据 ───────────────────────────────────────────
        private void DisplayMetadata(ImageMetadata metadata)
        {
            // 文件信息
            TxtFileSize.Text = metadata.GetFormattedFileSize();
            TxtModifiedTime.Text = metadata.FileModifiedTime.ToString("yyyy-MM-dd HH:mm:ss");
            TxtFilePath.Text = metadata.FilePath ?? "";

            // 图像信息
            TxtDimensions.Text = metadata.GetFormattedDimensions();

            // EXIF 信息
            TxtDateTaken.Text = metadata.DateTaken?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";
            TxtCameraMaker.Text = metadata.CameraMaker ?? "";
            TxtCameraModel.Text = metadata.CameraModel ?? "";
            TxtLensModel.Text = metadata.LensModel ?? "";
            TxtAperture.Text = metadata.GetFormattedAperture() ?? "";
            TxtMaxAperture.Text = metadata.MaxAperture.HasValue ? $"f/{metadata.MaxAperture.Value:F1}" : "";
            TxtExposureTime.Text = metadata.GetFormattedExposureTime() ?? "";
            TxtExposureBias.Text = metadata.GetFormattedExposureBias() ?? "";
            TxtISO.Text = metadata.ISOSpeed?.ToString() ?? "";
            TxtFocalLength.Text = metadata.GetFormattedFocalLength() ?? "";
            TxtMeteringMode.Text = metadata.MeteringMode ?? "";
            TxtFlash.Text = metadata.FlashMode ?? "";
            TxtWhiteBalance.Text = metadata.WhiteBalance ?? "";
            TxtBrightness.Text = metadata.Brightness?.ToString("F2") ?? "";
            TxtExposureProgram.Text = metadata.ExposureProgram ?? "";
            TxtSoftware.Text = metadata.Software ?? "";

            // AI 提示词
            if (!string.IsNullOrEmpty(metadata.AiPrompt))
            {
                TxtAiPrompt.Text = metadata.AiPrompt.Length > 600 
                    ? metadata.AiPrompt[..600] + "…" 
                    : metadata.AiPrompt;
                RowAiPrompt.Visibility = Visibility.Visible;
            }
            else
            {
                RowAiPrompt.Visibility = Visibility.Collapsed;
            }
        }

        // ─── 回退：基本信息显示 ───────────────────────────────────────
        private void DisplayBasicInfo(string filePath, BitmapSource? imageSource)
        {
            var fileInfo = new FileInfo(filePath);

            TxtFileSize.Text = FormatFileSize(fileInfo.Length);
            TxtModifiedTime.Text = fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");
            TxtFilePath.Text = fileInfo.FullName;

            TxtDimensions.Text = imageSource != null
                ? $"{imageSource.PixelWidth} × {imageSource.PixelHeight}"
                : "";

            ClearExifFields();
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

        private static string FormatFileSize(long bytes)
        {
            const long KB = 1024;
            const long MB = KB * 1024;
            const long GB = MB * 1024;

            return bytes switch
            {
                >= GB => $"{bytes / (double)GB:F2} GB",
                >= MB => $"{bytes / (double)MB:F2} MB",
                >= KB => $"{bytes / (double)KB:F2} KB",
                _ => $"{bytes} B"
            };
        }
    }
}
