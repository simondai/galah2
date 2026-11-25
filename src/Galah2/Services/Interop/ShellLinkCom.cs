using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Galah2.Services.Interop;

/// <summary>
/// Shell link (<c>.lnk</c>) creation without <c>ComImport</c>.
///
/// 1.0 used <c>[ComImport]</c> declarations, whose runtime COM wrappers are <b>not available
/// under NativeAOT</b> (BuiltInComInteropSupport=false). This file calls the vtables directly
/// with unmanaged function pointers instead — no RCW, no marshalling stubs, nothing the AOT
/// compiler has to synthesize at runtime.
///
/// Slot indices below were transcribed from 1.0's IShellLinkW / IPersistFile declarations in
/// their exact declaration order. Slot index IS the ABI contract, so the order must not drift:
///
///   IUnknown           0 QueryInterface  1 AddRef  2 Release
///   IShellLinkW        3 GetPath         4 GetIDList        5 SetIDList
///                      6 GetDescription  7 SetDescription
///                      8 GetWorkingDirectory  9 SetWorkingDirectory
///                     10 GetArguments  11 SetArguments
///                     12 GetHotkey     13 SetHotkey
///                     14 GetShowCmd    15 SetShowCmd
///                     16 GetIconLocation  17 SetIconLocation
///                     18 SetRelativePath  19 Resolve  20 SetPath
///   IPersistFile       3 GetClassID  4 IsDirty  5 Load  6 Save  7 SaveCompleted  8 GetCurFile
///
/// Marshalling policy: every boundary argument is blittable (IntPtr / int / uint). Strings are
/// widened by the caller, so no [MarshalAs] conversion runs at the boundary.
/// </summary>
internal static class ShellLinkCom
{
    public const int MaxPath = 2048;

    private static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid IidShellLinkW  = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid IidPersistFile = new("0000010B-0000-0000-c000-000000000046");

    private const int SlotsUnknown = 3;

    // IShellLinkW absolute slot indices.
    private const int SlotGetPath         = SlotsUnknown + 0;   // 3
    private const int SlotGetWorkingDir   = SlotsUnknown + 5;   // 8
    private const int SlotSetWorkingDir   = SlotsUnknown + 6;   // 9
    private const int SlotGetArguments    = SlotsUnknown + 7;   // 10
    private const int SlotSetArguments    = SlotsUnknown + 8;   // 11
    private const int SlotGetIconLocation = SlotsUnknown + 13;  // 16
    private const int SlotSetIconLocation = SlotsUnknown + 14;  // 17
    private const int SlotResolve         = SlotsUnknown + 16;  // 19
    private const int SlotSetPath         = SlotsUnknown + 17;  // 20

    // IPersistFile absolute slot indices (different interface, hence its own QI pointer).
    private const int SlotPfLoad = SlotsUnknown + 2;            // 5
    private const int SlotPfSave = SlotsUnknown + 3;            // 6

    private const int StgmRead           = 0x00000000;
    private const int ClsctxInprocServer = 0x1;
    private const int SOk                = 0;

    /// <summary>A thin owning handle over a raw <c>IShellLinkW</c> pointer.</summary>
    public sealed class ShellLink : IDisposable
    {
        private readonly IntPtr _p;

        internal ShellLink(IntPtr p) => _p = p;

        private static IntPtr Vtbl(IntPtr obj, int slot) =>
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size);

        private unsafe int Call(int slot, IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e)
        {
            var fn = Vtbl(_p, slot);
            return ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int>)fn)(
                _p, a, b, c, d, e);
        }

        /// <summary>Call a setter taking an LPWSTR. The COM callee copies the string, so the
        /// CoTaskMem buffer we allocated must be freed after the call returns.</summary>
        private int SetString(int slot, string value, IntPtr extra = default)
        {
            var p = Widen(value);
            try { return Call(slot, p, extra, default, default, default); }
            finally { Marshal.FreeCoTaskMem(p); }
        }

        public int SetPath(string path)             => SetString(SlotSetPath, path);
        public int SetWorkingDirectory(string dir)  => SetString(SlotSetWorkingDir, dir);
        public int SetArguments(string args)        => SetString(SlotSetArguments, args);
        public int SetIconLocation(string icon, int index) => SetString(SlotSetIconLocation, icon, (IntPtr)index);

        public string GetPath() =>
            ReadBuffer(buf => Call(SlotGetPath, buf, (IntPtr)MaxPath, IntPtr.Zero, (IntPtr)0, default));

        public string GetWorkingDirectory() =>
            ReadBuffer(buf => Call(SlotGetWorkingDir, buf, (IntPtr)MaxPath, default, default, default));

        public string GetArguments() =>
            ReadBuffer(buf => Call(SlotGetArguments, buf, (IntPtr)MaxPath, default, default, default));

        /// <summary>Icon path and its index; the index arrives through an out param, not the buffer.
        public unsafe (string Path, int Index) GetIconLocation()
        {
            var charBytes = MaxPath * 2;
            var buf = Marshal.AllocHGlobal(charBytes);
            try
            {
                for (var i = 0; i < charBytes; i += IntPtr.Size) Marshal.WriteIntPtr(buf, i, IntPtr.Zero);
                var pIndex = Marshal.AllocHGlobal(sizeof(int));
                try
                {
                    Marshal.WriteInt32(pIndex, 0);
                    Call(SlotGetIconLocation, buf, (IntPtr)MaxPath, pIndex, default, default);
                    return (Marshal.PtrToStringUni(buf) ?? "", Marshal.ReadInt32(pIndex));
                }
                finally { Marshal.FreeHGlobal(pIndex); }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        public int Resolve(IntPtr hwnd, uint flags) =>
            Call(SlotResolve, hwnd, (IntPtr)flags, default, default, default);

        /// <summary>Persist the link to disk. QIs for <c>IPersistFile</c> — a distinct interface,
        /// so its vtable must be reached through its own interface pointer, never via IShellLinkW's.</summary>
        public unsafe int Save(string linkPath)
        {
            var hr = QueryInterface(_p, IidPersistFilePtr(), out var pf);
            if (hr != SOk || pf == IntPtr.Zero) return hr;

            try
            {
                var fn = Vtbl(pf, SlotPfSave);
                var path = Widen(linkPath);
                try
                {
                    // Save(pszFileName, fRemember=true): LPCWSTR is blittable as IntPtr.
                    return ((delegate* unmanaged<IntPtr, IntPtr, int, int>)fn)(pf, path, 1);
                }
                finally { Marshal.FreeCoTaskMem(path); }
            }
            finally { Marshal.Release(pf); }
        }

        /// <summary>Load an existing link from disk (used by the resolver's read path).</summary>
        public unsafe int Load(string linkPath)
        {
            var hr = QueryInterface(_p, IidPersistFilePtr(), out var pf);
            if (hr != SOk || pf == IntPtr.Zero) return hr;

            try
            {
                var fn = Vtbl(pf, SlotPfLoad);
                var path = Widen(linkPath);
                try
                {
                    return ((delegate* unmanaged<IntPtr, IntPtr, int, int>)fn)(pf, path, StgmRead);
                }
                finally { Marshal.FreeCoTaskMem(path); }
            }
            finally { Marshal.Release(pf); }
        }

        private static string ReadBuffer(Func<IntPtr, int> read)
        {
            var charBytes = MaxPath * 2;
            var buf = Marshal.AllocHGlobal(charBytes);
            try
            {
                // Zero the whole buffer first: some implementations leave it untouched on failure.
                for (var i = 0; i < charBytes; i += IntPtr.Size) Marshal.WriteIntPtr(buf, i, IntPtr.Zero);
                read(buf);
                return Marshal.PtrToStringUni(buf) ?? "";
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private static IntPtr Widen(string s) => Marshal.StringToCoTaskMemUni(s);

        public void Dispose()
        {
            if (_p != IntPtr.Zero) Marshal.Release(_p);
        }
    }

    /// <summary>Create a shell link object and QI for <c>IShellLinkW</c>.</summary>
    public static ShellLink? Create()
    {
        var clsid = ClsidShellLink;
        var iid = IidShellLinkW;
        if (CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxInprocServer, ref iid, out var p) != SOk)
            return null;
        if (p == IntPtr.Zero) return null;
        try { return new ShellLink(p); }
        catch { Marshal.Release(p); return null; }
    }

    /// <summary>Open an existing <c>.lnk</c> and bind it to <paramref name="link"/>.</summary>
    public static bool TryLoad(string linkPath, out ShellLink? link)
    {
        link = Create();
        if (link is null) return false;
        if (link.Load(linkPath) == SOk) return true;
        link.Dispose();
        link = null;
        return false;
    }

    /// <summary>Stable unmanaged copy of the IPersistFile IID, allocated once (the COM call only
    /// reads it, so it can be shared for the lifetime of the process).</summary>
    private static IntPtr _iidPersistFilePtr;
    private static readonly object _iidGate = new();

    private static IntPtr IidPersistFilePtr()
    {
        lock (_iidGate)
        {
            if (_iidPersistFilePtr != IntPtr.Zero) return _iidPersistFilePtr;
            var p = Marshal.AllocHGlobal(16);
            Marshal.StructureToPtr(IidPersistFile, p, false);
            _iidPersistFilePtr = p;
            return p;
        }
    }

    private static unsafe int QueryInterface(IntPtr obj, IntPtr iid, out IntPtr result)
    {
        var fn = Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), 0);
        var pResult = IntPtr.Zero;
        var hr = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)fn)(obj, iid, &pResult);
        result = pResult;
        return hr;
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, int dwClsContext,
                                              ref Guid riid, out IntPtr ppv);
}
