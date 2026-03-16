using System.IO;
using System.Text.Json;

namespace ImageViewer
{
    public class AppSettings
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ImageViewer", "appsettings.json");

        public static AppSettings Current { get; private set; } = Load();

        // ── 语言 ─────────────────────────────────────────────────────────
        public string Language { get; set; } = "zh-CN";

        // ── 常规设置 ──────────────────────────────────────────────────────
        public bool AlwaysOnTop           { get; set; } = false;
        public bool SmartToolbar          { get; set; } = false;
        public bool RememberPosition      { get; set; } = false;
        public double? WindowLeft         { get; set; }
        public double? WindowTop          { get; set; }
        public double? WindowWidth        { get; set; }
        public double? WindowHeight       { get; set; }
        public bool OriginalPixelMode     { get; set; } = false;
        public bool RawOriginalView       { get; set; } = false;
        public bool HardwareAcceleration  { get; set; } = true;
        public string FullscreenBgType    { get; set; } = "dark";   // "dark" | "black"
        public double FullscreenBgOpacity { get; set; } = 0.8;

        // ── 习惯设置 ──────────────────────────────────────────────────────
        public bool   MultiWindow         { get; set; } = false;
        public string RotationMode        { get; set; } = "manual"; // "manual" | "keep"
        public bool   LoopWithinFolder    { get; set; } = false;
        public bool   ShowLoadingIndicator{ get; set; } = true;
        public bool   AutoNaming          { get; set; } = false;
        public string WheelMode           { get; set; } = "zoom";   // "zoom" | "page" | "scroll"

        // ── 显示设置（与主窗口更多菜单同步） ──────────────────────────────
        public bool ShowThumbnails  { get; set; } = false;
        public bool SmartThumbnails { get; set; } = false;
        public bool ShowBirdEye     { get; set; } = false;
        public bool FolderTraverse  { get; set; } = false;

        // ── 专业格式设置 ─────────────────────────────────────────────────
        public bool ProfessionalFormatScaleTo4K { get; set; } = true;

        // ─────────────────────────────────────────────────────────────────
        public void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(SettingsPath)!;
                Directory.CreateDirectory(dir);
                string json = JsonSerializer.Serialize(this,
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json);
            }
            catch { /* 静默忽略 */ }
        }

        private static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string json = File.ReadAllText(SettingsPath);
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch { }
            return new AppSettings();
        }
    }
}
