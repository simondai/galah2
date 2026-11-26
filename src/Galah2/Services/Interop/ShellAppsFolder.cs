using System.Runtime.InteropServices;

namespace Galah2.Services.Interop;

/// <summary>
/// 基于原生 Shell COM vtable 的 <c>shell:AppsFolder</c> 枚举与图标提取（100% NativeAOT 兼容，无 WinRT 依赖）。
/// </summary>
internal static unsafe class ShellAppsFolder
{
    private static readonly Guid FolderIdAppsFolder = new("1E87508D-89C2-42F0-8A7E-645A0F50CA58");
    private static readonly Guid IidShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static readonly Guid IidShellItem2 = new("7E9FB0D3-919F-4307-AB2E-9B1860310C93");
    private static readonly Guid BhidEnumItems = new("94F60519-2850-4924-AA5A-D15E84868039");
    private static readonly Guid IidEnumShellItems = new("70629033-E363-4A28-A567-0DB78006E6D7");
    private static readonly Guid IidShellItemImageFactory = new("BCC18B79-BA16-442F-80C4-8A59C30C463B");

    private static readonly PROPERTYKEY PkeyAppUserModelId = new()
    {
        Fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        Pid = 5,
    };

    private const int SOk = 0;
    private const uint CoInitApartmentThreaded = 0x2;
    private const uint SigdnNormalDisplay = 0x00000000;
    private const uint SiigbfIconOnly = 0x00000004;
    private const uint SiigbfBiggerSizeOk = 0x00000001;

    // COM vtable 槽位索引
    private const int SlotBindToHandler = 3;      // IShellItem
    private const int SlotGetDisplayName = 5;     // IShellItem
    private const int SlotGetString = 17;         // IShellItem2
    private const int SlotEnumNext = 3;           // IEnumShellItems
    private const int SlotGetImage = 3;           // IShellItemImageFactory

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid Fmtid;
        public uint Pid;
    }

    /// <summary>一条来自 shell:AppsFolder 的商店应用记录。</summary>
    public readonly record struct StoreAppEntry(string Name, string Aumid);

    /// <summary>
    /// 在独立 STA 线程中枚举本机所有 UWP / MSIX 商店应用。
    /// </summary>
    public static List<StoreAppEntry> EnumerateStoreApps()
    {
        var results = new List<StoreAppEntry>();
        var thread = new Thread(() =>
        {
            var hrInit = CoInitializeEx(IntPtr.Zero, CoInitApartmentThreaded);
            try
            {
                EnumerateOnSta(results);
            }
            catch (Exception) { }
            finally
            {
                if (hrInit == SOk || hrInit == 1) CoUninitialize();
            }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(8));
        return results;
    }

    /// <summary>
    /// 提取指定 shell 解析路径（如 <c>shell:AppsFolder\&lt;AUMID&gt;</c> 或 <c>shell:::{CLSID}</c>）的 HBITMAP。
    /// 调用方负责通过 <see cref="Win32.DeleteObject"/> 释放返回的句柄。
    /// </summary>
    public static IntPtr ExtractShellItemBitmap(string parsingName, int size)
    {
        IntPtr hBitmap = IntPtr.Zero;
        var thread = new Thread(() =>
        {
            var hrInit = CoInitializeEx(IntPtr.Zero, CoInitApartmentThreaded);
            try
            {
                var iid = IidShellItemImageFactory;
                if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var factory) != SOk
                    || factory == IntPtr.Zero)
                    return;

                try
                {
                    // x64 ABI 下 8 字节 SIZE(cx, cy) 按值打包在一个 64 位整数寄存器中传递
                    var packedSize = ((long)size << 32) | (uint)size;
                    var fn = Vtbl(factory, SlotGetImage);
                    var hr = ((delegate* unmanaged<IntPtr, long, uint, out IntPtr, int>)fn)(
                        factory, packedSize, SiigbfIconOnly | SiigbfBiggerSizeOk, out var bmp);
                    if (hr == SOk && bmp != IntPtr.Zero)
                        hBitmap = bmp;
                }
                finally
                {
                    Marshal.Release(factory);
                }
            }
            catch (Exception) { hBitmap = IntPtr.Zero; }
            finally
            {
                if (hrInit == SOk || hrInit == 1) CoUninitialize();
            }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(4));
        return hBitmap;
    }

    private static void EnumerateOnSta(List<StoreAppEntry> results)
    {
        var folderId = FolderIdAppsFolder;
        var iidItem = IidShellItem;
        if (SHGetKnownFolderItem(ref folderId, 0, IntPtr.Zero, ref iidItem, out var folder) != SOk
            || folder == IntPtr.Zero)
            return;

        try
        {
            var bhid = BhidEnumItems;
            var iidEnum = IidEnumShellItems;
            var bindFn = Vtbl(folder, SlotBindToHandler);
            if (((delegate* unmanaged<IntPtr, IntPtr, Guid*, Guid*, out IntPtr, int>)bindFn)(
                    folder, IntPtr.Zero, &bhid, &iidEnum, out var enumerator) != SOk
                || enumerator == IntPtr.Zero)
                return;

            try
            {
                var nextFn = (delegate* unmanaged<IntPtr, uint, out IntPtr, out uint, int>)Vtbl(enumerator, SlotEnumNext);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                while (nextFn(enumerator, 1, out var item, out var fetched) == SOk && fetched == 1 && item != IntPtr.Zero)
                {
                    try
                    {
                        var aumid = ReadAumid(item);
                        if (string.IsNullOrWhiteSpace(aumid) || !IsStoreAppAumid(aumid)) continue;
                        if (!seen.Add(aumid)) continue;

                        var name = ReadDisplayName(item);
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        results.Add(new StoreAppEntry(name.Trim(), aumid.Trim()));
                    }
                    finally
                    {
                        Marshal.Release(item);
                    }
                }
            }
            finally
            {
                Marshal.Release(enumerator);
            }
        }
        finally
        {
            Marshal.Release(folder);
        }
    }

    /// <summary>
    /// 区分 UWP/MSIX 商店应用与传统 Win32 应用：
    /// 商店应用的 AUMID 形如 <c>Microsoft.WindowsCalculator_8wekyb3d8bbwe!App</c>（含 '!' 且不含路径分隔符）。
    /// </summary>
    private static bool IsStoreAppAumid(string aumid)
    {
        var bang = aumid.IndexOf('!');
        if (bang <= 0 || bang == aumid.Length - 1) return false;
        if (aumid.Contains('\\') || aumid.Contains('/')) return false;
        if (aumid.StartsWith('{')) return false;
        return aumid[..bang].Contains('_');
    }

    private static string? ReadDisplayName(IntPtr shellItem)
    {
        var fn = Vtbl(shellItem, SlotGetDisplayName);
        if (((delegate* unmanaged<IntPtr, uint, out IntPtr, int>)fn)(shellItem, SigdnNormalDisplay, out var psz) != SOk
            || psz == IntPtr.Zero)
            return null;
        try { return Marshal.PtrToStringUni(psz); }
        finally { Marshal.FreeCoTaskMem(psz); }
    }

    private static string? ReadAumid(IntPtr shellItem)
    {
        var iid2 = IidShellItem2;
        if (Marshal.QueryInterface(shellItem, ref iid2, out var item2) != SOk || item2 == IntPtr.Zero)
            return null;
        try
        {
            var key = PkeyAppUserModelId;
            var fn = Vtbl(item2, SlotGetString);
            if (((delegate* unmanaged<IntPtr, PROPERTYKEY*, out IntPtr, int>)fn)(item2, &key, out var psz) != SOk
                || psz == IntPtr.Zero)
                return null;
            try { return Marshal.PtrToStringUni(psz); }
            finally { Marshal.FreeCoTaskMem(psz); }
        }
        finally
        {
            Marshal.Release(item2);
        }
    }

    private static IntPtr Vtbl(IntPtr obj, int slot) =>
        Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderItem(
        ref Guid rfid, uint flags, IntPtr hToken, ref Guid riid, out IntPtr ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid, out IntPtr ppv);
}
