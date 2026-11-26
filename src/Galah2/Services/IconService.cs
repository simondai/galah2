using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Galah2.Models;
using Galah2.Persistence;
using Galah2.Services.Interop;

namespace Galah2.Services;

/// <summary>
/// Shell icon extraction and a PNG cache.
///
/// Mirrors 1.x's contract: everything here is derived data, <c>GalahData/icons</c> may be deleted
/// and is simply rebuilt, and no icon work ever writes to the authoritative documents.
///
/// Differences from 1.x, all forced by the rewrite:
///  - no <c>System.Drawing</c> and no WPF imaging: an <c>HICON</c> is read as BGRA through GDI and
///    handed to Avalonia's <see cref="WriteableBitmap"/>;
///  - no <c>RenderTargetBitmap</c>: 1.x redrew app icons through WPF visuals, which does not exist
///    here, so a missing icon falls back to a generated letter tile instead of a drawn glyph;
///  - extraction runs on the caller's thread pool, never on the UI thread.
/// </summary>
public static class IconService
{
    private const int IconSize = 32;
    private const uint DibRgbColors = 0;
    private const uint DiMask = 0x0001;
    private const uint DiNormal = 0x0003;
    private const uint ShgfiIcon = 0x000000100;

    /// <summary>Cap on a shell-handler extraction before falling back to the generic route.</summary>
    private static readonly TimeSpan ShellHandlerTimeout = TimeSpan.FromSeconds(4);

    private static AppDataPaths? _paths;

    /// <summary>Point the service at a data root (boot calls this once; tests use a scratch dir).</summary>
    public static void Initialize(AppDataPaths paths) => _paths = paths;

    /// <summary>
    /// Resolve an item's icon: the override source when it names a readable image, otherwise the
    /// target's shell icon. Never throws — a launcher must render even when an icon cannot load.
    /// </summary>
    public static Task<Bitmap?> GetAsync(CatalogItem item, int size = IconSize)
    {
        var (source, index) = ResolveSource(item);
        return GetAsync(source, index, size);
    }

    /// <summary>Resolve an icon for an explicit stored source path + icon index.</summary>
    public static Task<Bitmap?> GetAsync(string storedSource, int index, int size = IconSize) =>
        Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(storedSource)) return null;

            // 按至少 128px 高清像素提取与缓存，高 DPI 屏下由 Skia HighQuality 下采样，彻底消除锯齿发糊。
            var pixelSize = Math.Max(128, size * 2);

            // 所有 ms-settings:* 子页面共享同一个 Windows 原生设置齿轮图标，归一化缓存键避免重复写 52 份相同 PNG。
            var cacheSource = IsMsSettingsUri(storedSource) ? "ms-settings:" : storedSource;

            var cached = LoadCached(cacheSource, index, pixelSize);
            if (cached is not null) return cached;

            var bitmap = Extract(storedSource, index, pixelSize);
            if (bitmap is not null) CacheToFile(bitmap, cacheSource, index, pixelSize);
            return bitmap;
        });

    /// <summary>Which file and icon index an item points at (override wins).</summary>
    private static (string Source, int Index) ResolveSource(CatalogItem item)
    {
        if (item.Icon.Mode == IconModes.Override && !string.IsNullOrWhiteSpace(item.Icon.OverrideSource))
        {
            var (path, index) = SplitIndexed(item.Icon.OverrideSource);
            return (path, index);
        }
        return (item.Target, 0);
    }

    /// <summary>Parse the documented <c>"path,index"</c> override form.</summary>
    private static (string Path, int Index) SplitIndexed(string source)
    {
        var comma = source.LastIndexOf(',');
        if (comma > 1 && int.TryParse(source[(comma + 1)..], out var index))
            return (source[..comma], index);
        return (source, 0);
    }

    /// <summary>Best-effort extraction with a graceful chain of fallbacks.</summary>
    private static Bitmap? Extract(string storedSource, int index, int size)
    {
        // 内嵌 Avalonia 资源（如 avares://Galah2/Assets/actions/shutdown.png）直接从程序集解码。
        if (storedSource.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
            return DecodeAvaloniaAsset(storedSource);

        var osPath = PathNorm.ToOs(storedSource);

        // An image override is decoded directly: no shell round trip needed.
        // 注意 .ico 走高清图标提取路径以拿到 256/128px 层，其他图片直接解码。
        if (IsDirectImageFile(osPath) && File.Exists(osPath))
            return DecodeImage(osPath, size);

        // Windows 设置页协议（ms-settings:*）统一提取系统原生高清设置齿轮图标。
        if (IsMsSettingsUri(storedSource))
            return ExtractSettingsAppIcon(size);

        // 商店应用（shell:AppsFolder\<AUMID>）通过 IShellItemImageFactory 提取原生 128px 高清图标。
        if (IsAppsFolderUri(storedSource))
            return ExtractAppsFolderIcon(storedSource, size);

        if (!File.Exists(osPath))
            return ExtractShellIcon(osPath, size);   // protocols, URLs and missing paths

        // .lnk 快捷方式优先解析其图标来源或目标程序走高清 128px 提取，避免 32px 放大发糊。
        if (osPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var lnkBmp = ExtractLinkHighResIcon(osPath, size);
            if (lnkBmp is not null) return lnkBmp;
        }

        // 优先提取高清大图（.exe / .dll / .ico / .cpl 等均内嵌 128~256px 资源）。
        var highRes = ExtractHighResIcon(osPath, index, size);
        if (highRes is not null) return highRes;

        if (index > 0)
        {
            var byIndex = ExtractByIndex(osPath, index, size);
            if (byIndex is not null) return byIndex;
        }

        // A .msc snap-in keeps its icon as a compressed PNG resource that none of the plain
        // shell APIs can read; only the handler Explorer itself uses can. Try it first, and fall
        // back to the generic route if it declines.
        if (osPath.EndsWith(".msc", StringComparison.OrdinalIgnoreCase))
        {
            var viaHandler = ExtractViaShellHandler(osPath, size);
            if (viaHandler is not null) return viaHandler;
        }

        return ExtractShellIcon(osPath, size);
    }

    private static bool IsMsSettingsUri(string source) =>
        source.TrimStart().StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase);

    private static bool IsAppsFolderUri(string source) =>
        source.TrimStart().StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>通过 IShellItemImageFactory 提取商店应用的高清 HBITMAP 并转为 Avalonia Bitmap。</summary>
    private static Bitmap? ExtractAppsFolderIcon(string parsingName, int size)
    {
        var hBitmap = ShellAppsFolder.ExtractShellItemBitmap(parsingName, size);
        if (hBitmap == IntPtr.Zero) return null;
        try { return FromHBitmap(hBitmap); }
        finally { Win32.DeleteObject(hBitmap); }
    }

    /// <summary>解析 .lnk 快捷方式的自定义图标或目标可执行文件，按高清像素提取。</summary>
    private static Bitmap? ExtractLinkHighResIcon(string osLnkPath, int size)
    {
        try
        {
            var info = LinkResolver.Resolve(osLnkPath);
            if (!info.Ok) return null;

            if (!string.IsNullOrWhiteSpace(info.IconSource))
            {
                var iconPath = Environment.ExpandEnvironmentVariables(PathNorm.ToOs(info.IconSource));
                if (File.Exists(iconPath))
                {
                    if (IsDirectImageFile(iconPath)) return DecodeImage(iconPath, size);
                    var fromIcon = ExtractHighResIcon(iconPath, info.IconIndex, size);
                    if (fromIcon is not null) return fromIcon;
                }
            }

            var targetPath = Environment.ExpandEnvironmentVariables(PathNorm.ToOs(info.Target));
            if (File.Exists(targetPath))
            {
                var fromTarget = ExtractHighResIcon(targetPath, 0, size);
                if (fromTarget is not null) return fromTarget;
            }
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>将 GDI HBITMAP（如 IShellItemImageFactory 返回的 32 位位图）转换为 Avalonia WriteableBitmap。</summary>
    private static unsafe Bitmap? FromHBitmap(IntPtr hBitmap)
    {
        if (hBitmap == IntPtr.Zero) return null;
        if (Win32.GetObjectW(hBitmap, Marshal.SizeOf<Win32.BITMAP>(), out var bm) == 0) return null;
        if (bm.bmWidth <= 0 || bm.bmHeight <= 0) return null;

        var width = bm.bmWidth;
        var height = bm.bmHeight;
        var screenDc = Win32.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;

        try
        {
            var info = new Win32.BITMAPINFO
            {
                bmiHeader = new Win32.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height, // top-down
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = DibRgbColors,
                },
                bmiColors = 0,
            };

            var totalBytes = width * height * 4;
            var pixels = new byte[totalBytes];
            fixed (byte* p = pixels)
            {
                var lines = Win32.GetDIBits(screenDc, hBitmap, 0, (uint)height, (IntPtr)p, ref info, DibRgbColors);
                if (lines == 0) return null;

                // 若 32 位位图不含有效 Alpha（极少数旧图标），统一设为不透明避免隐形
                if (!HasAnyAlpha(p, totalBytes))
                {
                    for (var i = 3; i < totalBytes; i += 4) p[i] = 255;
                }

                var bitmap = new WriteableBitmap(
                    new PixelSize(width, height),
                    new Vector(96, 96),
                    PixelFormat.Bgra8888,
                    AlphaFormat.Premul);

                using (var buffer = bitmap.Lock())
                {
                    var rowBytes = width * 4;
                    var dst = (byte*)buffer.Address;
                    for (var y = 0; y < height; y++)
                    {
                        Buffer.MemoryCopy(p + y * rowBytes, dst + y * buffer.RowBytes, rowBytes, rowBytes);
                    }
                }
                return bitmap;
            }
        }
        catch (Exception) { return null; }
        finally { Win32.ReleaseDC(IntPtr.Zero, screenDc); }
    }

    /// <summary>提取 Windows 原生「设置」应用的高清齿轮图标。</summary>
    private static Bitmap? ExtractSettingsAppIcon(int size)
    {
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(winDir)) return null;

        var panelDir = Path.Combine(winDir, "ImmersiveControlPanel");
        var imagesDir = Path.Combine(panelDir, "images");
        foreach (var fileName in new[] { "logo.scale-400.png", "logo.scale-200.png", "logo.png" })
        {
            var path = Path.Combine(imagesDir, fileName);
            if (!File.Exists(path)) continue;
            var decoded = DecodeImage(path, size);
            if (decoded is not null) return decoded;
        }

        var exePath = Path.Combine(panelDir, "SystemSettings.exe");
        return File.Exists(exePath)
            ? ExtractHighResIcon(exePath, 0, size) ?? ExtractShellIcon(exePath, size)
            : null;
    }

    private static bool IsDirectImageFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>通过 user32!PrivateExtractIconsW 按高清像素提取资源图标（避免 32px 放大发糊）。</summary>
    private static Bitmap? ExtractHighResIcon(string osPath, int index, int size)
    {
        var ext = Path.GetExtension(osPath);
        if (!ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            && !ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)
            && !ext.Equals(".ico", StringComparison.OrdinalIgnoreCase)
            && !ext.Equals(".cpl", StringComparison.OrdinalIgnoreCase))
            return null;

        var handles = new IntPtr[1];
        var ids = new uint[1];
        var extracted = Win32.PrivateExtractIconsW(osPath, index, size, size, handles, ids, 1, 0);
        if (extracted == 0 || extracted == uint.MaxValue || handles[0] == IntPtr.Zero)
            return null;

        try { return FromIconHandle(handles[0], size); }
        finally { Win32.DestroyIcon(handles[0]); }
    }

    private static Bitmap? DecodeAvaloniaAsset(string avaresUri)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(avaresUri));
            return new Bitmap(stream);
        }
        catch (Exception) { return null; }
    }

    private static Bitmap? DecodeImage(string osPath, int size)
    {
        try
        {
            using var stream = File.OpenRead(osPath);
            return Bitmap.DecodeToWidth(stream, size);
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Run the shell icon handler on a dedicated STA worker.
    ///
    /// The handler's IExtractIconW is only reachable from an STA apartment: on an MTA thread
    /// QueryInterface answers E_NOINTERFACE. Extraction runs on the thread pool (MTA), so the
    /// whole COM conversation is handed to a worker with the right apartment. The wait is bounded
    /// because a stuck shell handler must never hold up a panel.
    /// </summary>
    private static Bitmap? ExtractViaShellHandler(string osPath, int size)
    {
        const int Waiting = 0, Claimed = 1, Abandoned = 2;
        var state = Waiting;                 // which side owns the HICON the worker publishes
        var handle = IntPtr.Zero;
        // 用 ManualResetEventSlim 等待 worker 完成；不在此处由 worker 抢先 Dispose，
        // 避免与主线程 Wait 返回路径竞争触发 ObjectDisposedException。
        var done = new ManualResetEventSlim(false);
        var worker = new Thread(() =>
        {
            IntPtr h = IntPtr.Zero;
            try { h = ShellIconHandler.Extract(osPath, large: true); }
            catch (Exception) { h = IntPtr.Zero; }
            finally
            {
                handle = h;                  // publish before the state transition
                if (h != IntPtr.Zero && Interlocked.CompareExchange(ref state, Claimed, Waiting) == Abandoned)
                    Win32.DestroyIcon(h);    // caller gave up: this worker owns the handle
                done.Set();
            }
        })
        { IsBackground = true };                 // abandoned workers never block shutdown
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();

        if (!done.Wait(ShellHandlerTimeout))
        {
            if (Interlocked.CompareExchange(ref state, Abandoned, Waiting) == Claimed)
            {
                var late = handle;
                if (late != IntPtr.Zero) Win32.DestroyIcon(late);
            }
            return null;                          // fall back to the legacy routes
        }

        done.Dispose();
        if (handle == IntPtr.Zero) return null;

        // 回退路径拿到的 HICON 为 32px，1:1 绘制避免 GDI 暴力放大，交由界面 Skia HighQuality 平滑缩放。
        try { return FromIconHandle(handle, IconSize); }
        finally { Win32.DestroyIcon(handle); }
    }

    /// <summary>Ask the shell for the icon it would show for this path.</summary>
    private static Bitmap? ExtractShellIcon(string osPath, int size)
    {
        // SHGFI_LARGEICON (0) is the shell's own 32px icon; SHGFI_ICON asks for the handle.
        const uint flags = ShgfiIcon;
        var info = new SHFILEINFOW();
        var structSize = (uint)Marshal.SizeOf<SHFILEINFOW>();

        var ok = Win32.SHGetFileInfoW(osPath, 0, ref info, structSize, flags);
        if (ok == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

        try { return FromIconHandle(info.hIcon, IconSize); }
        finally { Win32.DestroyIcon(info.hIcon); }
    }

    /// <summary>Extract by icon index from an executable or resource file.</summary>
    private static Bitmap? ExtractByIndex(string osPath, int index, int size)
    {
        var large = new IntPtr[1];
        var small = new IntPtr[1];
        if (Win32.ExtractIconExW(osPath, index, large, small, 1) == 0)
            return null;

        var handle = large[0] != IntPtr.Zero ? large[0] : small[0];
        if (handle == IntPtr.Zero) return null;

        try { return FromIconHandle(handle, IconSize); }
        finally
        {
            if (large[0] != IntPtr.Zero) Win32.DestroyIcon(large[0]);
            if (small[0] != IntPtr.Zero && small[0] != large[0]) Win32.DestroyIcon(small[0]);
        }
    }

    /// <summary>
    /// Convert an <c>HICON</c> to an Avalonia bitmap.
    ///
    /// The icon is drawn into a 32-bit top-down DIB through GDI, which composites the AND mask for
    /// us, and those pixels are handed straight to <see cref="WriteableBitmap"/>. Reading the
    /// colour bitmap directly would lose the mask and give icons black fringes.
    /// </summary>
    private static unsafe Bitmap? FromIconHandle(IntPtr hIcon, int size)
    {
        if (hIcon == IntPtr.Zero) return null;

        var screenDc = Win32.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;

        var memDc = Win32.CreateCompatibleDC(screenDc);
        var dib = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            if (memDc == IntPtr.Zero) return null;

            var header = new Win32.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                biWidth = size,
                biHeight = -size,          // negative = top-down rows
                biPlanes = 1,
                biBitCount = 32,
                biCompression = DibRgbColors
            };
            var info = new Win32.BITMAPINFO { bmiHeader = header, bmiColors = 0 };

            dib = Win32.CreateDIBSection(memDc, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return null;

            previous = Win32.SelectObject(memDc, dib);

            // DrawIconEx composites the icon and its mask onto the DIB.
            if (!DrawIconEx(memDc, 0, 0, hIcon, size, size, 0, IntPtr.Zero, DiNormal))
                return null;

            // 传统老图标（4/8/24位，如 PuTTY）不含 32 位 Alpha 通道，GDI 绘制后会把所有 Alpha 写成 0 导致整图隐形。
            // 仅在整张图全为 0 Alpha 时，才用 DI_MASK 还原真实不透明区域，现代带 Alpha 图标零额外开销。
            var totalBytes = size * size * 4;
            var src = (byte*)bits;
            if (!HasAnyAlpha(src, totalBytes))
                RestoreLegacyAlpha(memDc, hIcon, size, ref info, src, totalBytes);

            var bitmap = new WriteableBitmap(
                new PixelSize(size, size),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);

            using (var buffer = bitmap.Lock())
            {
                var rowBytes = size * 4;
                var dst = (byte*)buffer.Address;
                for (var y = 0; y < size; y++)
                {
                    Buffer.MemoryCopy(src + y * rowBytes, dst + y * buffer.RowBytes, rowBytes, rowBytes);
                }
            }

            return bitmap;
        }
        catch (Exception) { return null; } // extraction is cosmetic and must never throw
        finally
        {
            if (previous != IntPtr.Zero && memDc != IntPtr.Zero) Win32.SelectObject(memDc, previous);
            if (dib != IntPtr.Zero) Win32.DeleteObject(dib);
            if (memDc != IntPtr.Zero) Win32.DeleteDC(memDc);
            Win32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>检查 BGRA 像素缓冲中是否存在非零 Alpha 通道。</summary>
    private static unsafe bool HasAnyAlpha(byte* bgra, int totalBytes)
    {
        for (var i = 3; i < totalBytes; i += 4)
        {
            if (bgra[i] != 0) return true;
        }
        return false;
    }

    /// <summary>
    /// 通过绘制图标的 1-bit AND 掩码（DI_MASK）为传统无 Alpha 图标还原透明度：
    /// 掩码黑色（0）表示不透明本体，设 Alpha=255 并保留纯黑像素；掩码白色表示透明背景，清零整像素。
    /// </summary>
    private static unsafe void RestoreLegacyAlpha(
        IntPtr memDc, IntPtr hIcon, int size, ref Win32.BITMAPINFO info, byte* colorBgra, int totalBytes)
    {
        var maskDib = Win32.CreateDIBSection(memDc, ref info, 0, out var maskBits, IntPtr.Zero, 0);
        if (maskDib == IntPtr.Zero || maskBits == IntPtr.Zero) return;

        var prevMask = Win32.SelectObject(memDc, maskDib);
        try
        {
            if (!DrawIconEx(memDc, 0, 0, hIcon, size, size, 0, IntPtr.Zero, DiMask))
                return;

            var mask = (byte*)maskBits;
            for (var i = 0; i < totalBytes; i += 4)
            {
                // AND 掩码中 0x000000（黑）= 不透明，0xFFFFFF（白）= 透明
                if (mask[i] == 0 && mask[i + 1] == 0 && mask[i + 2] == 0)
                {
                    colorBgra[i + 3] = 255;
                }
                else
                {
                    colorBgra[i] = 0;
                    colorBgra[i + 1] = 0;
                    colorBgra[i + 2] = 0;
                    colorBgra[i + 3] = 0;
                }
            }
        }
        finally
        {
            if (prevMask != IntPtr.Zero) Win32.SelectObject(memDc, prevMask);
            Win32.DeleteObject(maskDib);
        }
    }

    /// <summary>
    /// Stable short hash of some content, used to name stored files without encoding their origin
    /// in a path. Same algorithm as the icon cache, so the two cannot disagree.
    /// </summary>
    public static string ContentHash(string content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant()[..24];

    private static string CachePath(string storedSource, int index, int size)
    {
        if (_paths is null) return "";
        // 升级缓存盐值至 v4，自动重建此前被缓存成全透明的传统老图标。
        var key = "v4|" + storedSource + "|" + index + "|" + size;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(key)))[..24];
        return Path.Combine(_paths.IconsDirectory, hash + ".png");
    }

    private static Bitmap? LoadCached(string storedSource, int index, int size)
    {
        var path = CachePath(storedSource, index, size);
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            return new Bitmap(stream);
        }
        catch (Exception) { return null; }
    }

    private static void CacheToFile(Bitmap bitmap, string storedSource, int index, int size)
    {
        var path = CachePath(storedSource, index, size);
        if (string.IsNullOrEmpty(path) || File.Exists(path)) return;
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            bitmap.Save(tmp);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            /* derived data: a cache miss next time is not a failure */
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon,
        int cxWidth, int cyHeight, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);
}
