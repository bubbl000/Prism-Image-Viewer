using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace ImageBrowser.Services;

/// <summary>
/// 资源管理器排序服务 - 获取Windows资源管理器的文件夹排序设置
/// </summary>
public static class ExplorerSortService
{
    #region COM Interfaces

    [ComImport]
    [Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void _VtblGap0_12();
        void QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object ppshv);
    }

    [ComImport]
    [Guid("cde725b0-ccc9-4519-917e-325d72fab4ce")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        void _VtblGap0_2();
        void GetFolder(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        [PreserveSig]
        int Item(int iItemIndex, out IntPtr ppidl);
        [PreserveSig]
        int ItemCount(uint uFlags, out int pcItems);
    }

    [ComImport]
    [Guid("1AC3D9F0-175C-11d1-95BE-00609797EA4F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFolder2
    {
        void GetClassID(out Guid pClassID);
        void Initialize(IntPtr pidl);
        void GetCurFolder(out IntPtr pidl);
    }

    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        void QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);
    }

    #endregion

    #region WinAPI

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern void SHParseDisplayName(
        [MarshalAs(UnmanagedType.LPWStr)] string name, 
        IntPtr bindingContext,
        out IntPtr pidl, 
        uint sfgaoIn, 
        out uint psfgaoOut);

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDList(IntPtr pidl, StringBuilder pszPath);

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SHOpenFolderAndSelectItems(
        IntPtr pidlFolder,
        uint cidl, 
        [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, 
        uint dwFlags);

    [DllImport("ole32.dll")]
    private static extern int CoInitialize(IntPtr pvReserved);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    #endregion

    private static readonly Guid SID_STopLevelBrowser = new(
        0x4C96BE40, 0x915C, 0x11CF, 0x99, 0xD3, 0x00, 0xAA, 0x00, 0x4A, 0xE8, 0x37);

    private static readonly Guid SID_ShellWindows = new(
        0x9BA05972, 0xF6A8, 0x11CF, 0xA4, 0x42, 0x00, 0xA0, 0xC9, 0x0A, 0x8F, 0x39);

    /// <summary>
    /// 获取资源管理器中的文件排序列表
    /// </summary>
    /// <param name="folderPath">文件夹路径</param>
    /// <returns>按资源管理器排序的文件路径列表，如果失败则返回null</returns>
    public static List<string>? GetExplorerSortedFiles(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            return null;

        try
        {
            CoInitialize(IntPtr.Zero);

            var shellWindowsType = Type.GetTypeFromCLSID(SID_ShellWindows);
            if (shellWindowsType == null) return null;
            
            var shellWindows = Activator.CreateInstance(shellWindowsType);
            if (shellWindows == null) return null;

            try
            {
                foreach (var window in (System.Collections.IEnumerable)shellWindows)
                {
                    if (window == null) continue;
                    
                    var sp = (IServiceProvider)window;
                    object? sb = null;
                    object? sv = null;
                    object? pf = null;
                    
                    try
                    {
                        Guid sidBrowser = SID_STopLevelBrowser;
                        Guid iidBrowser = IShellBrowserGUID;
                        sp.QueryService(ref sidBrowser, ref iidBrowser, out sb);

                        var shellBrowser = (IShellBrowser)sb;
                        shellBrowser.QueryActiveShellView(out sv);

                        if (sv is IFolderView folderView)
                        {
                            Guid iidPersist = IPersistFolder2GUID;
                            folderView.GetFolder(ref iidPersist, out pf);
                            var persistFolder = (IPersistFolder2)pf;
                            persistFolder.GetCurFolder(out IntPtr pidl);

                            try
                            {
                                var path = new StringBuilder(1024);
                                if (SHGetPathFromIDList(pidl, path))
                                {
                                    string windowPath = path.ToString();
                                    if (PathsEqual(windowPath, folderPath))
                                    {
                                        return GetFilesFromFolderView(folderView);
                                    }
                                }
                            }
                            finally
                            {
                                Marshal.FreeCoTaskMem(pidl);
                            }
                        }
                    }
                    finally
                    {
                        // 确保所有 COM 对象都被释放
                        if (pf != null) Marshal.ReleaseComObject(pf);
                        if (sv != null) Marshal.ReleaseComObject(sv);
                        if (sb != null) Marshal.ReleaseComObject(sb);
                        Marshal.ReleaseComObject(window);
                    }
                }
            }
            finally
            {
                if (shellWindows != null)
                    Marshal.FinalReleaseComObject(shellWindows);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"获取资源管理器排序失败: {ex.Message}");
        }
        finally
        {
            CoUninitialize();
        }

        return null;
    }

    /// <summary>
    /// 从FolderView获取文件列表
    /// </summary>
    private static List<string> GetFilesFromFolderView(IFolderView folderView)
    {
        var files = new List<string>();

        int count = 0;
        folderView.ItemCount(0, out count);

        for (int i = 0; i < count; i++)
        {
            IntPtr pidl = IntPtr.Zero;
            try
            {
                if (folderView.Item(i, out pidl) == 0)
                {
                    var path = new StringBuilder(1024);
                    if (SHGetPathFromIDList(pidl, path))
                    {
                        string filePath = path.ToString();
                        if (File.Exists(filePath))
                        {
                            files.Add(filePath);
                        }
                    }
                }
            }
            finally
            {
                if (pidl != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(pidl);
            }
        }

        return files;
    }

    /// <summary>
    /// 比较两个路径是否相等（忽略大小写和末尾分隔符）
    /// </summary>
    private static bool PathsEqual(string path1, string path2)
    {
        path1 = path1.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        path2 = path2.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(path1, path2, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Guid IShellBrowserGUID = typeof(IShellBrowser).GUID;
    private static readonly Guid IPersistFolder2GUID = typeof(IPersistFolder2).GUID;
}
