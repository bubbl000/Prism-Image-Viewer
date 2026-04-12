using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Printing;
using System.Windows.Documents;
using System.Windows.Xps.Packaging;
using ImageBrowser.Utils;
using ImageBrowser.Services;

namespace ImageBrowser
{
    public partial class MainWindow : Window
    {
        // ─── 图片列表与当前索引 ────────────────────────────────────────
        private List<string> _imageFiles = new();
        private int _currentIndex = -1;
        private double _currentRotation = 0;

        // 异步加载
        private CancellationTokenSource? _loadCts;

        // 原图 LRU 内存缓存 + 预读取
        private readonly ImageCache _imageCache = new();
        private CancellationTokenSource? _prefetchCts;

        // GIF 动画
        private GifAnimator? _gifAnimator;
        private bool _suppressSliderEvent = false;

        // Ctrl+拖拽外部拖出
        private Point _dragStartPoint;
        private bool _isDragging = false;
        private bool _isExternalDrag = false;  // 是否正在跨软件拖拽
        private Point _dragCurrentPoint;       // 当前拖拽位置

        // 缩略图异步加载
        private CancellationTokenSource? _thumbCts;

        // 缩略图显示模式
        private bool _thumbAlwaysOn = false;  // 常驻显示
        private bool _smartThumb = false;      // 智能展示（悬停渐显）
        private DispatcherTimer? _thumbHideTimer;

        // 鸟瞰图
        private bool _showBirdEye = false;
        private DispatcherTimer? _birdEyeTimer;

        // 文件夹穿透
        private bool _folderTraverse = false;

        // 文件监视（使用增强型 SmartFileWatcher 带防抖）
        private SmartFileWatcher? _fileWatcher;

        // 导航队列（连续按键优化）
        private readonly System.Collections.Concurrent.ConcurrentQueue<int> _navigationQueue = new();
        private CancellationTokenSource? _navCts;

        private static readonly string[] SupportedExts =
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp", ".psd", ".psb",
            // RAW 格式
            ".arw", ".cr2", ".cr3", ".nef", ".orf", ".raf", ".rw2",
            ".dng", ".pef", ".sr2", ".srf", ".x3f", ".kdc", ".mos",
            ".raw", ".erf", ".mrw", ".nrw", ".ptx", ".r3d", ".3fr"
        };

        public MainWindow()
        {
            InitializeComponent();
            MouseMove += Window_MouseMove;
            MouseLeftButtonUp += Window_MouseLeftButtonUp;
            Loaded += Window_Loaded;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var s = AppSettings.Current;

            // 窗口位置
            if (s.RememberPosition &&
                s.WindowLeft.HasValue && s.WindowTop.HasValue &&
                s.WindowWidth.HasValue && s.WindowHeight.HasValue)
            {
                Left   = s.WindowLeft.Value;
                Top    = s.WindowTop.Value;
                Width  = s.WindowWidth.Value;
                Height = s.WindowHeight.Value;
            }

            // 即时生效设置
            if (s.AlwaysOnTop) Topmost = true;
            if (s.OriginalPixelMode)
                RenderOptions.SetBitmapScalingMode(MainImageViewer,
                    System.Windows.Media.BitmapScalingMode.NearestNeighbor);
            ApplyHardwareAcceleration(s.HardwareAcceleration);

            // 同步显示状态
            _thumbAlwaysOn  = s.ShowThumbnails;
            _smartThumb     = s.SmartThumbnails;
            _showBirdEye    = s.ShowBirdEye;
            _folderTraverse = s.FolderTraverse;

            // 应用显示设置
            ApplyThumbStripVisibility(_thumbAlwaysOn);
            ApplyBirdEyeVisibility(_showBirdEye);
        }
    }
}
