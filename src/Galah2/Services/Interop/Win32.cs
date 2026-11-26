using System.Runtime.InteropServices;

namespace Galah2.Services.Interop;

/// <summary>Win32 error codes the launcher has to translate into user-visible outcomes.</summary>
internal static class Win32Error
{
    public const int FileNotFound = 2;
    public const int PathNotFound = 3;
    public const int AccessDenied = 5;
    public const int BadPath = 123;
    public const int Cancelled = 1223;              // UAC prompt dismissed
    public const int ElevationRequired = 740;
    public const int NoAssociation = 31;            // SE_ERR_NOASSOC
}

/// <summary>Native structures used by shell and process APIs.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct STARTUPINFOW
{
    public int cb;
    public IntPtr lpReserved;
    public IntPtr lpDesktop;
    public IntPtr lpTitle;
    public int dwX;
    public int dwY;
    public int dwXSize;
    public int dwYSize;
    public int dwXCountChars;
    public int dwYCountChars;
    public int dwFillAttribute;
    public int dwFlags;
    public short wShowWindow;
    public short cbReserved2;
    public IntPtr lpReserved2;
    public IntPtr hStdInput;
    public IntPtr hStdOutput;
    public IntPtr hStdError;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROCESS_INFORMATION
{
    public IntPtr hProcess;
    public IntPtr hThread;
    public int dwProcessId;
    public int dwThreadId;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct SHELLEXECUTEINFOW
{
    public int cbSize;
    public int fMask;
    public IntPtr hwnd;
    public string? lpVerb;
    public string? lpFile;
    public string? lpParameters;
    public string? lpDirectory;
    public int nShow;
    public IntPtr hInstApp;
    public IntPtr lpIDList;
    public string? lpClass;
    public IntPtr hkeyClass;
    public int dwHotKey;
    public IntPtr hIcon;
    public IntPtr hProcess;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MONITORINFOW
{
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public int dwFlags;
}

/// <summary>Screen point in physical pixels.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct SHFILEINFOW
{
    public IntPtr hIcon;
    public int iIcon;
    public int dwAttributes;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string szDisplayName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
    public string szTypeName;
}

/// <summary>
/// Every P/Invoke used by Galah. Kept in one internal class so the rest of the code base only talks
/// to managed wrappers (module boundary in design §4).
/// </summary>
internal static class Win32
{
    internal delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);

    public const int SW_SHOWNORMAL = 1;
    public const uint SEE_MASK_NOCLOSEPROCESS = 0x00000002;
    public const uint STARTF_USESHOWWINDOW = 0x00000001;

    // ---- processes ------------------------------------------------------------

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateProcessW(
        string? lpApplicationName,
        System.Text.StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool ShellExecuteExW(ref SHELLEXECUTEINFOW lpExecInfo);

    // ---- shell icons ----------------------------------------------------------

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfoW(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFOW psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    // ---- monitors & cursor ----------------------------------------------------

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOW lpmi);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    /// <summary>GetAsyncKeyState high bit: the key is physically down right now.</summary>
    public static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    public const int VkLButton = 0x01;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    // ---- gdi bitmaps / icons --------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    /// <summary>Extract an icon by index from an executable/dll. Returns the number extracted.</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern uint ExtractIconExW(string file, int index, IntPtr[]? large, IntPtr[]? small, uint nIcons);

    /// <summary>按指定高清像素尺寸（如 128/256）直接提取可执行文件/图标资源中的大图，避免 32px 放大发糊。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint PrivateExtractIconsW(string lpszFile, int nIconIndex, int cxIcon, int cyIcon,
        IntPtr[] phicon, uint[] piconid, uint nIcons, uint flags);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetObjectW(IntPtr hgdiobj, int cbBuffer, out BITMAP lpvObject);

    [DllImport("gdi32.dll")]
    public static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines,
        IntPtr lpvBits, ref BITMAPINFO lpbmi, uint usage);

    // ---- window state & z-order (for Win+D / Show Desktop detection) ---------

    public const uint GW_HWNDPREV = 3;
    public const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    private const int DwmwaCloaked = 14;

    /// <summary>
    /// 判断窗口是否被最小化，或被“显示桌面”(WorkerW/Progman) 及其他可见普通窗口压在后面。
    /// 跳过任务栏/托盘/气泡及 DWM Cloaked 不可见后台窗口，避免点击托盘图标时误判。
    /// </summary>
    public static bool IsMinimizedOrObscured(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;
        if (IsIconic(hWnd)) return true;
        if (!GetWindowRect(hWnd, out var selfRect) || selfRect.Width <= 0 || selfRect.Height <= 0)
            return true;

        var cls = new System.Text.StringBuilder(128);
        var cur = GetWindow(hWnd, GW_HWNDPREV);
        while (cur != IntPtr.Zero)
        {
            if (IsWindowVisible(cur) && GetWindowRect(cur, out var r) && r.Width > 0 && r.Height > 0)
            {
                // 跳过 Win11 后台挂起/隐藏的 DWM Cloaked 窗口（如 UWP 商店应用、小组件宿主等）
                if (DwmGetWindowAttribute(cur, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
                {
                    cur = GetWindow(cur, GW_HWNDPREV);
                    continue;
                }

                cls.Clear();
                GetClassNameW(cur, cls, cls.Capacity);
                var className = cls.ToString();

                // Win11 点击“显示桌面”会把 WorkerW / Progman 提到普通窗口之上
                if (className is "WorkerW" or "Progman")
                    return true;

                // 忽略任务栏、托盘溢出区、工具提示等不遮挡主面板的系统壳层窗口
                if (className is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
                    or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland"
                    or "TaskListThumbnailWnd" or "tooltips_class32" or "ForegroundStaging"
                    or "ApplicationManager_DesktopShellWindow"))
                {
                    var intersects = r.Left < selfRect.Right && r.Right > selfRect.Left
                                  && r.Top < selfRect.Bottom && r.Bottom > selfRect.Top;
                    if (intersects) return true;
                }
            }
            cur = GetWindow(cur, GW_HWNDPREV);
        }
        return false;
    }
}
