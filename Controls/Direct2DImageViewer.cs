using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageBrowser.Controls;

/// <summary>
/// GPU 加速图像显示控件（当前使用 WPF 渲染，预留 Direct2D 接口）
/// </summary>
public class Direct2DImageViewer : Control
{
    private Image? _image;
    private ScaleTransform? _scaleTransform;
    private TranslateTransform? _translateTransform;
    private RotateTransform? _rotateTransform;
    private TransformGroup? _transformGroup;

    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(nameof(Source), typeof(BitmapSource),
            typeof(Direct2DImageViewer),
            new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty ScaleProperty =
        DependencyProperty.Register(nameof(Scale), typeof(double),
            typeof(Direct2DImageViewer),
            new PropertyMetadata(1.0, OnTransformChanged));

    public static readonly DependencyProperty RotationProperty =
        DependencyProperty.Register(nameof(Rotation), typeof(double),
            typeof(Direct2DImageViewer),
            new PropertyMetadata(0.0, OnTransformChanged));

    public static readonly DependencyProperty StretchProperty =
        DependencyProperty.Register(nameof(Stretch), typeof(Stretch),
            typeof(Direct2DImageViewer),
            new PropertyMetadata(Stretch.Uniform));

    public BitmapSource? Source
    {
        get => (BitmapSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public double Scale
    {
        get => (double)GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    public double Rotation
    {
        get => (double)GetValue(RotationProperty);
        set => SetValue(RotationProperty, value);
    }

    public Stretch Stretch
    {
        get => (Stretch)GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    public double OffsetX { get; set; }
    public double OffsetY { get; set; }

    static Direct2DImageViewer()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(Direct2DImageViewer),
            new FrameworkPropertyMetadata(typeof(Direct2DImageViewer)));
    }

    public Direct2DImageViewer()
    {
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyTemplate();
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _image = GetTemplateChild("PART_Image") as Image;
        if (_image != null)
        {
            _scaleTransform = new ScaleTransform();
            _translateTransform = new TranslateTransform();
            _rotateTransform = new RotateTransform();

            _transformGroup = new TransformGroup();
            _transformGroup.Children.Add(_scaleTransform);
            _transformGroup.Children.Add(_rotateTransform);
            _transformGroup.Children.Add(_translateTransform);

            _image.RenderTransform = _transformGroup;
            _image.RenderTransformOrigin = new Point(0.5, 0.5);

            UpdateImageSource();
            UpdateTransform();
        }
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var viewer = (Direct2DImageViewer)d;
        viewer.UpdateImageSource();
    }

    private static void OnTransformChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var viewer = (Direct2DImageViewer)d;
        viewer.UpdateTransform();
    }

    private void UpdateImageSource()
    {
        if (_image != null)
        {
            _image.Source = Source;
            _image.Stretch = Stretch;
        }
    }

    private void UpdateTransform()
    {
        if (_scaleTransform != null)
        {
            _scaleTransform.ScaleX = Scale;
            _scaleTransform.ScaleY = Scale;
        }

        if (_rotateTransform != null)
        {
            _rotateTransform.Angle = Rotation;
        }

        if (_translateTransform != null)
        {
            _translateTransform.X = OffsetX;
            _translateTransform.Y = OffsetY;
        }
    }

    public void Invalidate()
    {
        // 用于兼容 Direct2D 接口
    }
}
