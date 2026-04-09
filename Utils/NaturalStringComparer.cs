using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ImageBrowser.Utils;

/// <summary>
/// 自然字符串比较器（解决 img1, img10, img2 排序问题）
/// 从 ImageGlass 借鉴
/// </summary>
public class NaturalStringComparer : IComparer<string>
{
    public static NaturalStringComparer Instance { get; } = new();
    
    public int Compare(string? x, string? y)
    {
        if (x == null && y == null) return 0;
        if (x == null) return -1;
        if (y == null) return 1;
        
        return StrCmpLogicalW(x, y);
    }
    
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string psz1, string psz2);
}
