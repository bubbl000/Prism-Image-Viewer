using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageViewer;

internal static class ImageSharpHelper
{
    private static Image<Bgra32> ToImageSharp(BitmapSource src)
    {
        var converted = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int width  = converted.PixelWidth;
        int height = converted.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[height * stride];
        converted.CopyPixels(pixels, stride, 0);
        return Image.LoadPixelData<Bgra32>(pixels, width, height);
    }

    public static void EncodeJpeg(BitmapSource src, Stream dest, int quality)
    {
        using var img = ToImageSharp(src);
        img.Save(dest, new JpegEncoder { Quality = quality });
    }

    public static long EstimateJpegSize(BitmapSource src, int quality)
    {
        using var img = ToImageSharp(src);
        using var ms  = new MemoryStream();
        img.Save(ms, new JpegEncoder { Quality = quality });
        return ms.Length;
    }

    public static void EncodePng(BitmapSource src, Stream dest)
    {
        using var img = ToImageSharp(src);
        img.Save(dest, new PngEncoder());
    }
}
