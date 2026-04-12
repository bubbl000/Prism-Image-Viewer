using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ImageBrowser;

public partial class MainWindow
{
    // ─── 缩略图热区（智能展示） ───────────────────────────────────
    private void HotZone_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!_smartThumb || _currentIndex < 0) return;
        StopHideTimer();
        FadeInThumbStrip();
    }

    private void HotZone_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_smartThumb) return;
        EnsureHideTimer().Start();
    }

    private DispatcherTimer EnsureHideTimer()
    {
        if (_thumbHideTimer == null)
        {
            _thumbHideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            _thumbHideTimer.Tick += (_, _) =>
            {
                _thumbHideTimer.Stop();
                FadeOutThumbStrip();
            };
        }
        _thumbHideTimer.Stop();
        return _thumbHideTimer;
    }

    private void StopHideTimer() => _thumbHideTimer?.Stop();

    // ─── 缩略图渐显 / 渐隐动画 ───────────────────────────────────
    private void FadeInThumbStrip()
    {
        if (BottomPanel.Visibility == Visibility.Visible && BottomPanel.Opacity >= 1) return;
        BottomPanel.Visibility = Visibility.Visible;
        var anim = new DoubleAnimation(BottomPanel.Opacity, 1.0,
            new Duration(TimeSpan.FromMilliseconds(180)));
        BottomPanel.BeginAnimation(OpacityProperty, anim);
    }

    private void FadeOutThumbStrip()
    {
        if (BottomPanel.Visibility == Visibility.Collapsed) return;
        var anim = new DoubleAnimation(BottomPanel.Opacity, 0.0,
            new Duration(TimeSpan.FromMilliseconds(200)));
        // 淡出后保持 Visible，热区仍可触发
        BottomPanel.BeginAnimation(OpacityProperty, anim);
    }

    private void ShowThumbImmediate()
    {
        ThumbStrip.Visibility = Visibility.Visible;
        BottomPanel.BeginAnimation(OpacityProperty, null);
        BottomPanel.Opacity = 1;
        BottomPanel.Visibility = Visibility.Visible;
    }

    // 智能模式专用：BottomPanel 透明但占位，热区鼠标事件可触发
    private void PrepareSmartThumb()
    {
        ThumbStrip.Visibility = Visibility.Visible;
        BottomPanel.BeginAnimation(OpacityProperty, null);
        BottomPanel.Opacity = 0;
        BottomPanel.Visibility = Visibility.Visible;
    }

    private void HideThumbImmediate()
    {
        StopHideTimer();
        BottomPanel.BeginAnimation(OpacityProperty, null);
        if (_smartThumb)
        {
            // 智能模式：BottomPanel 保持 Visible+Opacity=0，热区继续有效
            BottomPanel.Opacity = 0;
            BottomPanel.Visibility = Visibility.Visible;
        }
        else
        {
            ThumbStrip.Visibility = Visibility.Collapsed;
            BottomPanel.Opacity = 1;
            BottomPanel.Visibility = Visibility.Visible;
        }
    }

    // ─── 缩略图 ───────────────────────────────────────────────────
    private void ReloadThumbnails()
    {
        _thumbCts?.Cancel();
        _thumbCts = new CancellationTokenSource();
        ThumbPanel.Children.Clear();
        _ = LoadThumbnailsAsync(_imageFiles.ToList(), _thumbCts.Token);
    }

    private async Task LoadThumbnailsAsync(List<string> files, CancellationToken ct)
    {
        // 先同步添加所有占位符
        for (int i = 0; i < files.Count; i++)
        {
            if (ct.IsCancellationRequested) return;
            ThumbPanel.Children.Add(CreateThumbPlaceholder(i));
        }

        // 并发加载（最多 3 个后台线程同时解码），按优先顺序提交任务
        using var sem = new System.Threading.SemaphoreSlim(3);
        var tasks = new List<Task>();

        foreach (int i in BuildThumbLoadOrder(files.Count, _currentIndex))
        {
            if (ct.IsCancellationRequested) break;

            int idx = i;
            string file = files[idx];

            await sem.WaitAsync(ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) { sem.Release(); break; }

            var t = Task.Run(async () =>
            {
                BitmapSource? bmp = null;
                try
                {
                    // 1. 磁盘缓存命中 → 直接返回，极快
                    bmp = ThumbnailCache.TryLoad(file);
                    if (bmp == null)
                    {
                        // 2. 缓存未命中 → 从源文件解码（降采样到 60px 高）
                        BitmapSource? b = null;
                        bool isProfessionalFormat = file.EndsWith(".psd", StringComparison.OrdinalIgnoreCase) ||
                                                   file.EndsWith(".psb", StringComparison.OrdinalIgnoreCase) ||
                                                   file.EndsWith(".cr3", StringComparison.OrdinalIgnoreCase);

                        if (isProfessionalFormat)
                        {
                            // PSD/PSB 使用 Aspose.PSD 加载内置缩略图（最快）
                            if (file.EndsWith(".psd", StringComparison.OrdinalIgnoreCase) ||
                                file.EndsWith(".psb", StringComparison.OrdinalIgnoreCase))
                            {
                                b = PsdLoader.LoadThumbnail(file);
                            }
                            // RAW 文件使用 LibRaw 加载内嵌缩略图
                            else if (RawLoader.IsRawFile(file))
                            {
                                b = RawLoader.LoadThumbnail(file);
                            }
                            else
                            {
                                // 其他专业格式使用 Magick.NET
                                b = LoadProfessionalThumbnail(file);
                            }
                        }
                        else
                        {
                            // 标准格式使用 WIC 解码
                            using var stream = new FileStream(file, FileMode.Open,
                                FileAccess.Read, FileShare.Read);
                            var bi = new BitmapImage();
                            bi.BeginInit();
                            bi.CacheOption      = BitmapCacheOption.OnLoad;
                            bi.DecodePixelHeight = 60;
                            bi.StreamSource     = stream;
                            bi.EndInit();
                            bi.Freeze();
                            b = bi;
                        }

                        // 3. 写入磁盘缓存
                        if (b != null)
                        {
                            ThumbnailCache.TrySave(file, b);
                            bmp = b;
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch { /* 跳过加载失败的缩略图 */ }
                finally { sem.Release(); }

                if (bmp != null && !ct.IsCancellationRequested)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (ct.IsCancellationRequested) return;
                        if (idx < ThumbPanel.Children.Count &&
                            ThumbPanel.Children[idx] is Border border &&
                            border.Child is Image img)
                        {
                            img.Source = bmp;
                        }
                    });
                }
            }, ct);

            tasks.Add(t);
        }

        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }
    }

    /// <summary>从 center 索引出发，向两侧交替展开的加载顺序。</summary>
    private static IEnumerable<int> BuildThumbLoadOrder(int count, int center)
    {
        if (count == 0) yield break;
        center = Math.Clamp(center, 0, count - 1);
        yield return center;
        for (int d = 1; d < count; d++)
        {
            int r = center + d;
            int l = center - d;
            if (r < count) yield return r;
            if (l >= 0)    yield return l;
        }
    }

    private Border CreateThumbPlaceholder(int index)
    {
        var img = new Image
        {
            Stretch = Stretch.Uniform,
            MaxWidth = 66,
            MaxHeight = 54,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var border = new Border
        {
            Width = 74,
            Height = 66,
            Margin = new Thickness(2, 4, 2, 4),
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            Cursor = Cursors.Hand,
            Tag = index,
            Child = img,
        };

        border.MouseLeftButtonDown += (s, _) => ShowImage((int)((Border)s).Tag);
        border.MouseEnter += (s, _) =>
        {
            var b = (Border)s;
            if ((int)b.Tag != _currentIndex)
            {
                b.Background  = ThumbHoverBg;
                b.BorderBrush = ThumbHoverBorder;
            }
        };
        border.MouseLeave += (s, _) =>
        {
            var b = (Border)s;
            if ((int)b.Tag != _currentIndex)
            {
                b.Background  = ThumbNormalBg;
                b.BorderBrush = Brushes.Transparent;
            }
        };
        return border;
    }

    private static readonly SolidColorBrush ThumbActiveBrush =
        (SolidColorBrush)new SolidColorBrush(Color.FromRgb(0x90, 0xC2, 0x08)).GetAsFrozen();
    private static readonly SolidColorBrush ThumbNormalBg =
        (SolidColorBrush)new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)).GetAsFrozen();
    private static readonly SolidColorBrush ThumbHoverBg =
        (SolidColorBrush)new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)).GetAsFrozen();
    private static readonly SolidColorBrush ThumbHoverBorder =
        (SolidColorBrush)new SolidColorBrush(Color.FromArgb(0x88, 0x90, 0xC2, 0x08)).GetAsFrozen();

    private void UpdateThumbSelection(int index)
    {
        for (int i = 0; i < ThumbPanel.Children.Count; i++)
        {
            if (ThumbPanel.Children[i] is Border b)
            {
                b.BorderBrush = (i == index) ? ThumbActiveBrush : Brushes.Transparent;
                if (i != index) b.Background = ThumbNormalBg; // 重置悬停背景
            }
        }
        ScrollThumbIntoView(index);
    }

    private void ScrollThumbIntoView(int index)
    {
        if (index < 0) return;
        if (ThumbScroll.ActualWidth == 0)
        {
            if (ThumbStrip.Visibility == Visibility.Visible)
                Dispatcher.InvokeAsync(() => ScrollThumbIntoView(index),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }

        // 用 TransformToAncestor 精确计算缩略图中心偏移
        try
        {
            if (index < ThumbPanel.Children.Count)
            {
                var item = ThumbPanel.Children[index] as FrameworkElement;
                if (item != null && item.ActualWidth > 0)
                {
                    var transform = item.TransformToAncestor(ThumbPanel);
                    var pos = transform.Transform(new Point(0, 0));
                    double offset = pos.X + item.ActualWidth / 2.0 - ThumbScroll.ActualWidth / 2.0;
                    ThumbScroll.ScrollToHorizontalOffset(Math.Max(0, offset));
                    return;
                }
            }
        }
        catch { /* 回退到固定宽度计算 */ }

        const double itemWidth = 78;
        double center = index * itemWidth + itemWidth / 2.0 - ThumbScroll.ActualWidth / 2.0;
        ThumbScroll.ScrollToHorizontalOffset(Math.Max(0, center));
    }
}
