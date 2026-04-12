using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ImageBrowser;

public partial class MainWindow
{
    // ─── GIF 控制 ─────────────────────────────────────────────────
    private void StopGifAnimator()
    {
        _gifAnimator?.Stop();
        _gifAnimator = null;
    }

    // ─── GIF 面板拖动功能 ──────────────────────────────────────────
    private bool _isGifPanelDragging = false;
    private Point _gifPanelDragStartPoint;
    private Point _gifPanelStartPosition;

    private void GifPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            _isGifPanelDragging = true;
            _gifPanelDragStartPoint = e.GetPosition(this);
            
            // 记录面板当前的位置
            if (GifPanel.HorizontalAlignment == HorizontalAlignment.Left && 
                GifPanel.VerticalAlignment == VerticalAlignment.Top)
            {
                _gifPanelStartPosition = new Point(GifPanel.Margin.Left, GifPanel.Margin.Top);
            }
            else
            {
                // 如果是初始位置，计算相对位置（居中顶部）
                double left = (ActualWidth - GifPanel.ActualWidth) / 2; // 水平居中
                double top = 60; // 顶部60px
                _gifPanelStartPosition = new Point(left, top);
            }
            
            GifPanel.CaptureMouse();
            e.Handled = true;
        }
    }

    private void OnGifFrameChanged(int frameIndex)
    {
        _suppressSliderEvent = true;
        GifSlider.Value = frameIndex;
        _suppressSliderEvent = false;
        TxtGifFrame.Text = $"{frameIndex + 1} / {(int)GifSlider.Maximum + 1}";
    }

    private void BtnGifFirst_Click(object sender, RoutedEventArgs e)
    {
        if (_gifAnimator == null) return;
        _gifAnimator.Pause();
        int prev = (_gifAnimator.CurrentFrame - 1 + _gifAnimator.TotalFrames) % _gifAnimator.TotalFrames;
        _gifAnimator.GoToFrame(prev);
        BtnGifPlayPause.Content = "▶";
    }

    private void BtnGifPlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_gifAnimator == null) return;
        if (_gifAnimator.IsPlaying)
        {
            _gifAnimator.Pause();
            BtnGifPlayPause.Content = "▶";
        }
        else
        {
            _gifAnimator.Play();
            BtnGifPlayPause.Content = "⏸";
        }
    }

    private void BtnGifLast_Click(object sender, RoutedEventArgs e)
    {
        if (_gifAnimator == null) return;
        _gifAnimator.Pause();
        int next = (_gifAnimator.CurrentFrame + 1) % _gifAnimator.TotalFrames;
        _gifAnimator.GoToFrame(next);
        BtnGifPlayPause.Content = "▶";
    }

    private void GifSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderEvent || _gifAnimator == null) return;
        _gifAnimator.Pause();
        _gifAnimator.GoToFrame((int)e.NewValue);
        BtnGifPlayPause.Content = "▶";
    }

    // ─── GIF 动画管理器 ───────────────────────────────────────────
    private class GifAnimator
    {
        // 帧数据结构（可在后台线程加载）
        public record FrameData(BitmapSource Frame, int DelayMs);

        // 静态方法：在后台线程加载所有帧，返回冻结数据
        public static List<FrameData> LoadFrames(string filePath)
        {
            var result = new List<FrameData>();
            using var stream = new FileStream(filePath, FileMode.Open,
                FileAccess.Read, FileShare.Read);
            var decoder = new GifBitmapDecoder(stream,
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

            foreach (var frame in decoder.Frames)
            {
                int delayMs = 100;
                if (frame.Metadata is BitmapMetadata meta)
                {
                    try
                    {
                        if (meta.ContainsQuery("/grctlext/Delay"))
                        {
                            var raw = meta.GetQuery("/grctlext/Delay");
                            if (raw != null && ushort.TryParse(raw.ToString(), out ushort cs))
                                delayMs = Math.Max(cs * 10, 20);
                        }
                    }
                    catch { }
                }
                var frozen = frame.Clone();
                frozen.Freeze();
                result.Add(new FrameData(frozen, delayMs));
            }
            return result;
        }

        private readonly List<FrameData> _frames;
        // DispatcherTimer 必须在 UI 线程构造，此构造函数必须在 UI 线程调用
        private readonly DispatcherTimer _timer;
        private int _currentIndex = 0;
        private bool _isPlaying = false;

        public int TotalFrames => _frames.Count;
        public int CurrentFrame => _currentIndex;
        public Action<int>? OnFrameChanged { get; set; }
        public Image? TargetImage { get; set; }
        public Controls.Direct2DImageViewer? TargetViewer { get; set; }
        public bool IsPlaying => _isPlaying;

        public GifAnimator(List<FrameData> frames)
        {
            _frames = frames;
            _timer = new DispatcherTimer(); // 在 UI 线程创建，Dispatcher 正确
            _timer.Tick += OnTick;
        }

        public BitmapSource GetFrame(int index)
        {
            if (_frames.Count == 0) throw new InvalidOperationException("No frames");
            return _frames[Math.Clamp(index, 0, _frames.Count - 1)].Frame;
        }

        private void OnTick(object? sender, EventArgs e)
        {
            _currentIndex = (_currentIndex + 1) % _frames.Count;
            if (TargetImage != null)
                TargetImage.Source = _frames[_currentIndex].Frame;
            if (TargetViewer != null)
                TargetViewer.Source = _frames[_currentIndex].Frame;
            _timer.Interval = TimeSpan.FromMilliseconds(_frames[_currentIndex].DelayMs);
            OnFrameChanged?.Invoke(_currentIndex);
        }

        public void Play()
        {
            if (_frames.Count <= 1) return;
            _isPlaying = true;
            _timer.Interval = TimeSpan.FromMilliseconds(_frames[_currentIndex].DelayMs);
            _timer.Start();
        }

        public void Pause()
        {
            _isPlaying = false;
            _timer.Stop();
        }

        public void Stop()
        {
            _isPlaying = false;
            _timer.Stop();
        }

        public void GoToFrame(int index)
        {
            if (index < 0 || index >= _frames.Count) return;
            _currentIndex = index;
            if (TargetImage != null)
                TargetImage.Source = _frames[_currentIndex].Frame;
            if (TargetViewer != null)
                TargetViewer.Source = _frames[_currentIndex].Frame;
            OnFrameChanged?.Invoke(_currentIndex);
        }
    }
}
