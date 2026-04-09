using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ImageBrowser.Utils;

/// <summary>
/// 图片动画辅助类
/// 提供平滑的平移、缩放动画
/// 参考 ImageGlass 的动画架构
/// </summary>
public static class ImageAnimationHelper
{
    /// <summary>
    /// 默认动画持续时间（毫秒）
    /// </summary>
    public const int DefaultDurationMs = 200;

    /// <summary>
    /// 默认帧率（FPS）
    /// </summary>
    public const int DefaultFps = 60;

    /// <summary>
    /// 缓动函数 - 三次缓出（平滑减速）
    /// </summary>
    public static double EaseOutCubic(double t)
    {
        return 1 - Math.Pow(1 - t, 3);
    }

    /// <summary>
    /// 缓动函数 - 三次缓入缓出
    /// </summary>
    public static double EaseInOutCubic(double t)
    {
        return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }

    /// <summary>
    /// 平滑平移动画
    /// </summary>
    public static async Task SmoothPanAsync(TranslateTransform transform, Vector delta, int durationMs = DefaultDurationMs)
    {
        var startX = transform.X;
        var startY = transform.Y;
        var targetX = startX + delta.X;
        var targetY = startY + delta.Y;

        await AnimateValueAsync(0, 1, durationMs, progress =>
        {
            var eased = EaseOutCubic(progress);
            transform.X = startX + (targetX - startX) * eased;
            transform.Y = startY + (targetY - startY) * eased;
        });
    }

    /// <summary>
    /// 平滑缩放到指定位置
    /// </summary>
    public static async Task SmoothZoomToAsync(
        ScaleTransform scaleTransform,
        TranslateTransform translateTransform,
        double targetScale,
        Point zoomCenter,
        int durationMs = DefaultDurationMs)
    {
        var startScale = scaleTransform.ScaleX;
        var startOffsetX = translateTransform.X;
        var startOffsetY = translateTransform.Y;

        // 计算缩放后的偏移，使 zoomCenter 保持在同一位置
        var scaleRatio = targetScale / startScale;
        var targetOffsetX = zoomCenter.X - (zoomCenter.X - startOffsetX) * scaleRatio;
        var targetOffsetY = zoomCenter.Y - (zoomCenter.Y - startOffsetY) * scaleRatio;

        await AnimateValueAsync(0, 1, durationMs, progress =>
        {
            var eased = EaseOutCubic(progress);

            var currentScale = startScale + (targetScale - startScale) * eased;
            scaleTransform.ScaleX = currentScale;
            scaleTransform.ScaleY = currentScale;

            translateTransform.X = startOffsetX + (targetOffsetX - startOffsetX) * eased;
            translateTransform.Y = startOffsetY + (targetOffsetY - startOffsetY) * eased;
        });
    }

    /// <summary>
    /// 平滑缩放（以中心点为基准）
    /// </summary>
    public static async Task SmoothZoomAsync(ScaleTransform transform, double deltaScale, int durationMs = DefaultDurationMs)
    {
        var startScale = transform.ScaleX;
        var targetScale = startScale * deltaScale;

        await AnimateValueAsync(0, 1, durationMs, progress =>
        {
            var eased = EaseOutCubic(progress);
            var currentScale = startScale + (targetScale - startScale) * eased;
            transform.ScaleX = currentScale;
            transform.ScaleY = currentScale;
        });
    }

    /// <summary>
    /// 平滑重置变换
    /// </summary>
    public static async Task SmoothResetAsync(
        ScaleTransform scaleTransform,
        TranslateTransform translateTransform,
        RotateTransform? rotateTransform = null,
        int durationMs = DefaultDurationMs)
    {
        var startScale = scaleTransform.ScaleX;
        var startX = translateTransform.X;
        var startY = translateTransform.Y;
        var startAngle = rotateTransform?.Angle ?? 0;

        await AnimateValueAsync(0, 1, durationMs, progress =>
        {
            var eased = EaseOutCubic(progress);

            var currentScale = startScale + (1 - startScale) * eased;
            scaleTransform.ScaleX = currentScale;
            scaleTransform.ScaleY = currentScale;

            translateTransform.X = startX + (0 - startX) * eased;
            translateTransform.Y = startY + (0 - startY) * eased;

            if (rotateTransform != null)
            {
                rotateTransform.Angle = startAngle + (0 - startAngle) * eased;
            }
        });
    }

    /// <summary>
    /// 淡入动画
    /// </summary>
    public static async Task FadeInAsync(UIElement element, int durationMs = 300)
    {
        element.Opacity = 0;
        element.Visibility = Visibility.Visible;

        await AnimateValueAsync(0, 1, durationMs, progress =>
        {
            element.Opacity = EaseOutCubic(progress);
        });
    }

    /// <summary>
    /// 淡出动画
    /// </summary>
    public static async Task FadeOutAsync(UIElement element, int durationMs = 200)
    {
        var startOpacity = element.Opacity;

        await AnimateValueAsync(0, 1, durationMs, progress =>
        {
            element.Opacity = startOpacity * (1 - EaseOutCubic(progress));
        });

        element.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 图片切换动画（淡入淡出）
    /// </summary>
    public static async Task CrossFadeAsync(UIElement oldImage, UIElement newImage, int durationMs = 300)
    {
        newImage.Opacity = 0;
        newImage.Visibility = Visibility.Visible;

        await AnimateValueAsync(0, 1, durationMs, progress =>
        {
            var eased = EaseOutCubic(progress);
            oldImage.Opacity = 1 - eased;
            newImage.Opacity = eased;
        });

        oldImage.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 滑动切换动画
    /// </summary>
    public static async Task SlideAsync(TranslateTransform transform, Vector direction, int distance, int durationMs = DefaultDurationMs)
    {
        var startX = transform.X;
        var startY = transform.Y;
        var targetX = startX + direction.X * distance;
        var targetY = startY + direction.Y * distance;

        // 先滑出
        await AnimateValueAsync(0, 1, durationMs / 2, progress =>
        {
            var eased = EaseInOutCubic(progress);
            transform.X = startX + (targetX - startX) * eased;
            transform.Y = startY + (targetY - startY) * eased;
        });

        // 重置位置（无动画）
        transform.X = startX - direction.X * distance;
        transform.Y = startY - direction.Y * distance;

        // 再滑入
        await AnimateValueAsync(0, 1, durationMs / 2, progress =>
        {
            var eased = EaseInOutCubic(progress);
            transform.X = (startX - direction.X * distance) + direction.X * distance * eased;
            transform.Y = (startY - direction.Y * distance) + direction.Y * distance * eased;
        });
    }

    /// <summary>
    /// 弹跳动画（用于边界回弹效果）
    /// </summary>
    public static async Task BounceBackAsync(TranslateTransform transform, Vector overshoot, int durationMs = 300)
    {
        var startX = transform.X;
        var startY = transform.Y;
        var overshootX = startX + overshoot.X;
        var overshootY = startY + overshoot.Y;
        var targetX = startX;
        var targetY = startY;

        // 先超出边界
        await AnimateValueAsync(0, 1, durationMs / 3, progress =>
        {
            var eased = EaseOutCubic(progress);
            transform.X = startX + (overshootX - startX) * eased;
            transform.Y = startY + (overshootY - startY) * eased;
        });

        // 再回弹
        await AnimateValueAsync(0, 1, durationMs * 2 / 3, progress =>
        {
            var eased = EaseOutCubic(progress);
            transform.X = overshootX + (targetX - overshootX) * eased;
            transform.Y = overshootY + (targetY - overshootY) * eased;
        });
    }

    /// <summary>
    /// 通用数值动画
    /// </summary>
    private static async Task AnimateValueAsync(double start, double end, int durationMs, Action<double> onUpdate)
    {
        var frameTime = 1000 / DefaultFps;
        var totalFrames = durationMs / frameTime;

        for (int i = 0; i <= totalFrames; i++)
        {
            var progress = i / (double)totalFrames;
            var value = start + (end - start) * progress;
            onUpdate(value);
            await Task.Delay(frameTime);
        }

        // 确保最终值
        onUpdate(end);
    }

    /// <summary>
    /// 创建 WPF 动画故事板 - 平移
    /// </summary>
    public static Storyboard CreatePanStoryboard(TranslateTransform transform, double deltaX, double deltaY, int durationMs = DefaultDurationMs)
    {
        var storyboard = new Storyboard();

        var animX = new DoubleAnimation
        {
            From = transform.X,
            To = transform.X + deltaX,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animX, transform);
        Storyboard.SetTargetProperty(animX, new PropertyPath(TranslateTransform.XProperty));
        storyboard.Children.Add(animX);

        var animY = new DoubleAnimation
        {
            From = transform.Y,
            To = transform.Y + deltaY,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animY, transform);
        Storyboard.SetTargetProperty(animY, new PropertyPath(TranslateTransform.YProperty));
        storyboard.Children.Add(animY);

        return storyboard;
    }

    /// <summary>
    /// 创建 WPF 动画故事板 - 缩放
    /// </summary>
    public static Storyboard CreateZoomStoryboard(ScaleTransform transform, double targetScale, int durationMs = DefaultDurationMs)
    {
        var storyboard = new Storyboard();

        var anim = new DoubleAnimation
        {
            From = transform.ScaleX,
            To = targetScale,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(anim, transform);
        Storyboard.SetTargetProperty(anim, new PropertyPath(ScaleTransform.ScaleXProperty));
        storyboard.Children.Add(anim);

        var animY = new DoubleAnimation
        {
            From = transform.ScaleY,
            To = targetScale,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animY, transform);
        Storyboard.SetTargetProperty(animY, new PropertyPath(ScaleTransform.ScaleYProperty));
        storyboard.Children.Add(animY);

        return storyboard;
    }
}

/// <summary>
/// 动画方向
/// </summary>
public enum AnimationDirection
{
    Left,
    Right,
    Up,
    Down
}

/// <summary>
/// 动画类型
/// </summary>
public enum AnimationType
{
    Pan,
    ZoomIn,
    ZoomOut,
    FadeIn,
    FadeOut,
    Slide
}
