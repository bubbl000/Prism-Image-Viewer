using System.Runtime.InteropServices;

namespace ImageBrowser;

/// <summary>
/// LibRaw C API P/Invoke 封装
/// 需要 libraw.dll 在程序目录或系统 PATH 中
/// </summary>
internal static class LibRawInterop
{
    private const string LibRawDll = "libraw-23";

    // LibRaw 错误码
    public enum LibRawErrors
    {
        LIBRAW_SUCCESS = 0,
        LIBRAW_UNSPECIFIED_ERROR = -1,
        LIBRAW_FILE_UNSUPPORTED = -2,
        LIBRAW_REQUEST_FOR_NONEXISTENT_IMAGE = -3,
        LIBRAW_OUT_OF_ORDER_CALL = -4,
        LIBRAW_NO_THUMBNAIL = -5,
        LIBRAW_UNSUPPORTED_THUMBNAIL = -6,
        LIBRAW_INPUT_CLOSED = -7,
        LIBRAW_NOT_IMPLEMENTED = -8,
        LIBRAW_UNSUFFICIENT_MEMORY = -100007,
        LIBRAW_DATA_ERROR = -100008,
        LIBRAW_IO_ERROR = -100009,
        LIBRAW_CANCELLED_BY_CALLBACK = -100010,
        LIBRAW_BAD_CROP = -100011,
        LIBRAW_TOO_BIG = -100012,
        LIBRAW_MEMPOOL_OVERFLOW = -100013
    }

    // 图像处理结果类型
    public enum LibRawImageType
    {
        LIBRAW_IMAGE_JPEG = 1,
        LIBRAW_IMAGE_BITMAP = 2
    }

    // 输出参数结构体
    [StructLayout(LayoutKind.Sequential)]
    public struct LibRawOutputParams
    {
        public uint greybox_x;
        public uint greybox_y;
        public uint greybox_width;
        public uint greybox_height;
        public uint cropbox_x;
        public uint cropbox_y;
        public uint cropbox_width;
        public uint cropbox_height;
        public float aber_red;
        public float aber_green;
        public float aber_blue;
        public float gamm_val_0;
        public float gamm_val_1;
        public float gamm_val_2;
        public float gamm_val_3;
        public float gamm_val_4;
        public float gamm_val_5;
        public float bright;
        public float threshold;
        public int half_size;
        public int four_color_rgb;
        public int highlight;
        public int use_auto_wb;
        public int use_camera_wb;
        public int use_camera_matrix;
        public int output_color;
        public int output_bps;
        public int output_tiff;
        public int user_flip;
        public int user_qual;
        public int user_black;
        public int user_cblack;
        public int user_sat;
        public int med_passes;
        public int auto_bright_thr;
        public int adjust_maximum_thr;
        public int no_auto_bright;
        public int use_fuji_rotate;
        public int green_matching;
        public int dcb_iterations;
        public int dcb_enhance_fl;
        public int fbdd_noiserd;
        public int exp_correc;
        public float exp_shift;
        public int exp_preser;
        public int use_rawspeed;
        public int no_auto_scale;
        public int no_interpolation;
        public int strig_green_fc;
        public int xtrans_af;
        public int input_encoding;
        public int output_encoding;
        public int gamma_16bit_ps;
    }

    // 图像处理结果
    [StructLayout(LayoutKind.Sequential)]
    public struct LibRawProcessedImage
    {
        public LibRawImageType type;
        public ushort height;
        public ushort width;
        public ushort colors;
        public ushort bits;
        public uint data_size;
        // data 字段在 C 结构体中是柔性数组，需要特殊处理
    }

    // ==================== 核心 API ====================

    // 创建/销毁处理实例
    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr libraw_init(uint flags);

    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libraw_close(IntPtr lr);

    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libraw_recycle(IntPtr lr);

    // 打开文件
    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int libraw_open_file(IntPtr lr, string file);

    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libraw_open_buffer(IntPtr lr, IntPtr buffer, uint size);

    // 解包（读取原始数据）
    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libraw_unpack(IntPtr lr);

    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libraw_unpack_thumb(IntPtr lr);

    // 解码处理
    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libraw_raw2image(IntPtr lr);

    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libraw_dcraw_process(IntPtr lr);

    // 生成处理后的图像
    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr libraw_dcraw_make_mem_image(IntPtr lr, out int errc);

    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr libraw_dcraw_make_mem_thumb(IntPtr lr, out int errc);

    // 释放内存
    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libraw_dcraw_clear_mem(IntPtr img);

    // 获取错误信息
    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr libraw_strerror(int errcode);

    // 获取输出参数指针（用于修改处理参数）
    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr libraw_get_output_params(IntPtr lr);

    // 设置输出参数
    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libraw_set_output_params(IntPtr lr, ref LibRawOutputParams param);

    // 调整大小 - 半尺寸模式
    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libraw_set_half_size(IntPtr lr, int half_size);

    // 设置亮度
    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libraw_set_bright(IntPtr lr, float bright);

    // 设置输出位数
    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libraw_set_output_bps(IntPtr lr, int bps);

    // 设置自动白平衡
    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libraw_set_auto_wb(IntPtr lr, int auto_wb);

    // 设置相机白平衡
    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libraw_set_camera_wb(IntPtr lr, int camera_wb);

    // 设置无自动亮度
    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libraw_set_no_auto_bright(IntPtr lr, int no_auto_bright);

    // 获取图像宽度
    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libraw_get_raw_width(IntPtr lr);

    // 获取图像高度
    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libraw_get_raw_height(IntPtr lr);

    // 获取颜色数量
    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libraw_get_colors(IntPtr lr);

    // 获取位深
    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libraw_get_bits(IntPtr lr);

    // 获取缩略图宽度
    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libraw_get_thumbnail_width(IntPtr lr);

    // 获取缩略图高度
    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libraw_get_thumbnail_height(IntPtr lr);

    // 获取缩略图格式
    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libraw_get_thumbnail_format(IntPtr lr);

    // ==================== 辅助方法 ====================

    /// <summary>
    /// 获取错误信息字符串
    /// </summary>
    public static string GetErrorString(int errcode)
    {
        IntPtr ptr = libraw_strerror(errcode);
        return ptr != IntPtr.Zero ? Marshal.PtrToStringAnsi(ptr) ?? "Unknown error" : "Unknown error";
    }

    /// <summary>
    /// 从处理后的图像指针读取数据
    /// </summary>
    public static byte[] ReadProcessedImageData(IntPtr imgPtr)
    {
        if (imgPtr == IntPtr.Zero)
            return Array.Empty<byte>();

        // 读取结构体头部
        var header = Marshal.PtrToStructure<LibRawProcessedImage>(imgPtr);
        
        // 数据紧跟在结构体后面
        IntPtr dataPtr = imgPtr + Marshal.SizeOf<LibRawProcessedImage>();
        
        byte[] data = new byte[header.data_size];
        Marshal.Copy(dataPtr, data, 0, (int)header.data_size);
        
        return data;
    }

    /// <summary>
    /// 获取处理后的图像信息
    /// </summary>
    public static LibRawProcessedImage GetProcessedImageInfo(IntPtr imgPtr)
    {
        if (imgPtr == IntPtr.Zero)
            return default;

        return Marshal.PtrToStructure<LibRawProcessedImage>(imgPtr);
    }
}
