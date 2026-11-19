using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Galah2.Services.Interop;

/// <summary>
/// The shell's own icon handler (<c>{7A80E4A8-8005-11D2-BCF8-00C04F72C717}</c>), called through
/// raw vtables.
///
/// Why this exists at all: modern <c>.msc</c> snap-ins store their icon as a compressed PNG
/// resource. <c>ExtractIconExW</c> returns 0 icons for them, <c>SHGetFileInfoW</c> falls back to
/// the grey "document" placeholder, and <c>PrivateExtractIconsW</c> does not see the resource
/// either. Only the shell handler reads it, because it is what Explorer itself uses.
///
/// 1.x reached the handler with <c>[ComImport]</c> interfaces; NativeAOT has no built-in COM
/// wrappers, so this file calls the vtables by hand the same way
/// <see cref="ShellLinkCom"/> does.
///
/// Two constraints that were measured, not assumed:
///  - the handler's IExtractIconW must be called from an STA apartment; on an MTA thread
///    QueryInterface answers E_NOINTERFACE. Callers must run this on an STA worker.
///  - <c>GetIconLocation</c> returns S_FALSE (1) when the handler only wants to point at a
///    location rather than rasterize. Only <c>Extract</c> returning S_OK with a non-null HICON
///    means a real bitmap came back.
///
/// Slot order (IUnknown 0-2, then declaration order):
///   IPersistFile   3 GetClassID  4 IsDirty  5 Load  6 Save  7 SaveCompleted  8 GetCurFile
///   IExtractIconW  3 GetIconLocation  4 Extract
/// </summary>
internal static class ShellIconHandler
{
    public const int MaxPath = 2048;

    private static readonly Guid ClsidShellIconHandler = new("7A80E4A8-8005-11D2-BCF8-00C04F72C717");
    private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IidPersistFile = new("0000010B-0000-0000-C000-000000000046");
    private static readonly Guid IidExtractIconW = new("000214FA-0000-0000-C000-000000000046");

    private const int SlotsUnknown = 3;
    private const int SlotPfLoad = SlotsUnknown + 2;          // 5
    private const int SlotGetIconLocation = SlotsUnknown + 0; // 3
    private const int SlotExtract = SlotsUnknown + 1;         // 4

    private const int ClsctxInprocServer = 0x1;
    private const int StgmRead = 0x0;
    private const int SOk = 0;

    /// <summary>Icon sizes to request: low word = large, high word = small (32/16 px).</summary>
    public const uint IconSizeRequest = 0x20 | 0x10;

    private delegate int GetIconLocationFn(IntPtr self, uint flags, IntPtr iconFile, int cchMax,
                                           out int index, out uint gilFlags);

    private delegate int ExtractFn(IntPtr self, IntPtr file, uint index,
                                    out IntPtr large, out IntPtr small, uint iconSize);

    private delegate int LoadFn(IntPtr self, IntPtr fileName, int mode);

    /// <summary>
    /// Ask the handler for the real icon of <paramref name="osPath"/>.
    /// Returns a native HICON the caller owns, or <see cref="IntPtr.Zero"/> when the handler
    /// declines. Must be called from an STA thread.
    /// </summary>
    public static IntPtr Extract(string osPath, bool large)
    {
        var clsid = ClsidShellIconHandler;
        var iid = IidUnknown;
        if (CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxInprocServer, ref iid, out var handler) != SOk)
            return IntPtr.Zero;
        if (handler == IntPtr.Zero) return IntPtr.Zero;

        try
        {
            // 1. Load the document through IPersistFile — a separate interface, so a QI.
            if (QueryInterface(handler, IidPersistFile, out var persist) != SOk || persist == IntPtr.Zero)
                return IntPtr.Zero;

            try
            {
                var loadFn = (LoadFn)Marshal.GetDelegateForFunctionPointer(
                    Marshal.ReadIntPtr(Marshal.ReadIntPtr(persist), SlotPfLoad * IntPtr.Size),
                    typeof(LoadFn));
                var path = Marshal.StringToCoTaskMemUni(osPath);
                try
                {
                    if (loadFn(persist, path, StgmRead) != SOk) return IntPtr.Zero;
                }
                finally { Marshal.FreeCoTaskMem(path); }
            }
            finally { Marshal.Release(persist); }

            // 2. The handler tells us where the icon lives.
            if (QueryInterface(handler, IidExtractIconW, out var extract) != SOk || extract == IntPtr.Zero)
                return IntPtr.Zero;

            try
            {
                var location = Marshal.AllocHGlobal(MaxPath * 2);
                try
                {
                    for (var i = 0; i < MaxPath * 2; i += IntPtr.Size)
                        Marshal.WriteIntPtr(location, i, IntPtr.Zero);

                    var getLocation = (GetIconLocationFn)Marshal.GetDelegateForFunctionPointer(
                        Marshal.ReadIntPtr(Marshal.ReadIntPtr(extract), SlotGetIconLocation * IntPtr.Size),
                        typeof(GetIconLocationFn));

                    if (getLocation(extract, 0, location, MaxPath, out var index, out _) < 0)
                        return IntPtr.Zero;

                    var file = Marshal.PtrToStringUni(location) ?? "";
                    if (file.Length == 0) return IntPtr.Zero;

                    // 3. Rasterize. A non-zero index means an index inside the icon resource.
                    var extractFn = (ExtractFn)Marshal.GetDelegateForFunctionPointer(
                        Marshal.ReadIntPtr(Marshal.ReadIntPtr(extract), SlotExtract * IntPtr.Size),
                        typeof(ExtractFn));

                    var filePtr = Marshal.StringToCoTaskMemUni(file);
                    try
                    {
                        // "minus one" tells the handler to use the location/index it reported.
                        var hr = extractFn(extract, filePtr, unchecked((uint)index), out var big, out var small,
                            IconSizeRequest);

                        var handle = large ? big : (small != IntPtr.Zero ? small : big);
                        var other = large ? small : big;

                        if (hr == SOk && handle != IntPtr.Zero)
                        {
                            if (other != IntPtr.Zero) Win32.DestroyIcon(other);
                            return handle;
                        }

                        if (big != IntPtr.Zero) Win32.DestroyIcon(big);
                        if (small != IntPtr.Zero) Win32.DestroyIcon(small);
                        return IntPtr.Zero;
                    }
                    finally { Marshal.FreeCoTaskMem(filePtr); }
                }
                finally { Marshal.FreeHGlobal(location); }
            }
            finally { Marshal.Release(extract); }
        }
        catch (Exception)
        {
            return IntPtr.Zero;   // a broken handler is a missing icon, never a crash
        }
        finally { Marshal.Release(handler); }
    }

    /// <summary>QueryInterface through slot 0. Returns S_OK when the pointer came back non-null.</summary>
    private static unsafe int QueryInterface(IntPtr obj, Guid iid, out IntPtr result)
    {
        var fn = Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), 0);
        var iidPtr = Marshal.AllocHGlobal(16);
        var res = IntPtr.Zero;
        try
        {
            Marshal.StructureToPtr(iid, iidPtr, false);
            ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)fn)(obj, iidPtr, &res);
        }
        finally { Marshal.FreeHGlobal(iidPtr); }
        result = res;
        return res == IntPtr.Zero ? 1 : 0;
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, int dwClsContext,
                                              ref Guid riid, out IntPtr ppv);
}
