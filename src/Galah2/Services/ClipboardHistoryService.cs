using System.Runtime.InteropServices;
using Galah2.Services.Interop;

namespace Galah2.Services;

/// <summary>单条内存剪贴板纯文本历史记录。</summary>
public sealed class ClipboardEntry
{
    public required string Id { get; init; }
    public required string Text { get; init; }
    public DateTimeOffset CopiedAt { get; set; }
    public bool IsPinned { get; set; }
}

/// <summary>
/// 纯文本内存剪贴板历史记录服务（退出程序自动清空，不写磁盘）。
/// 通过原生 Win32 <c>AddClipboardFormatListener</c> 事件驱动监听，零轮询、0 CPU 占用；
/// 自动跳过密码管理器与 RandPass 标记的敏感剪贴板内容。
/// </summary>
public sealed class ClipboardHistoryService : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private const int WmQuit = 0x0012;
    private const int HwndMessage = -3;
    private const uint CfUnicodeText = 13;
    private const int MaxEntries = 150;
    private const int MaxTextLength = 200_000;

    private static readonly object SuppressGate = new();
    private static string? _suppressedText;
    private static DateTimeOffset _suppressedUntil;

    private readonly object _gate = new();
    private readonly List<ClipboardEntry> _entries = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly WndProc _wndProc;
    private IntPtr _window;
    private int _disposed;
    private uint _fmtExcludeMonitor;
    private uint _fmtCanIncludeHistory;

    public ClipboardHistoryService()
    {
        _wndProc = WndProcCallback;
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "galah2-clipboard" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    /// <summary>历史列表发生变化时触发（可能在监听线程上，UI 层需切回 Dispatcher）。</summary>
    public event Action? Changed;

    /// <summary>
    /// 抑制下一次对指定文本的剪贴板记录（供 RandPass 复制密码或内部回贴调用，有效期 3 秒）。
    /// </summary>
    public static void SuppressNext(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (SuppressGate)
        {
            _suppressedText = text;
            _suppressedUntil = DateTimeOffset.UtcNow.AddSeconds(3);
        }
    }

    /// <summary>获取按“置顶优先 + 复制时间倒序”排列的快照。</summary>
    public List<ClipboardEntry> GetSnapshot()
    {
        lock (_gate)
        {
            return _entries
                .OrderByDescending(e => e.IsPinned)
                .ThenByDescending(e => e.CopiedAt)
                .ToList();
        }
    }

    /// <summary>手工追加或置顶一条文本（主要用于探针测试及内部复用）。</summary>
    public void RecordText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (text.Length > MaxTextLength) text = text[..MaxTextLength];

        lock (_gate)
        {
            var existing = _entries.Find(e => string.Equals(e.Text, text, StringComparison.Ordinal));
            if (existing is not null)
            {
                existing.CopiedAt = DateTimeOffset.Now;
            }
            else
            {
                _entries.Add(new ClipboardEntry
                {
                    Id = Guid.NewGuid().ToString("N")[..12],
                    Text = text,
                    CopiedAt = DateTimeOffset.Now,
                    IsPinned = false,
                });
                TrimOverflowLocked();
            }
        }
        Changed?.Invoke();
    }

    /// <summary>切换置顶状态。</summary>
    public void TogglePin(string id)
    {
        var changed = false;
        lock (_gate)
        {
            var item = _entries.Find(e => e.Id == id);
            if (item is not null)
            {
                item.IsPinned = !item.IsPinned;
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>删除单条历史记录。</summary>
    public void Remove(string id)
    {
        var removed = false;
        lock (_gate)
        {
            removed = _entries.RemoveAll(e => e.Id == id) > 0;
        }
        if (removed) Changed?.Invoke();
    }

    /// <summary>清空所有未置顶的历史记录。</summary>
    public void ClearUnpinned()
    {
        var removed = false;
        lock (_gate)
        {
            removed = _entries.RemoveAll(e => !e.IsPinned) > 0;
        }
        if (removed) Changed?.Invoke();
    }

    /// <summary>
    /// 将文本写回系统剪贴板；若开启自动粘贴且提供了目标窗口句柄，则把焦点还给目标窗口并模拟 Ctrl+V。
    /// </summary>
    public bool CopyAndPaste(string text, IntPtr targetWindow, bool autoPaste)
    {
        SuppressNext(text);
        if (!TrySetClipboardText(text)) return false;

        // 把这条重新选中的内容更新时间提至顶部
        lock (_gate)
        {
            var existing = _entries.Find(e => string.Equals(e.Text, text, StringComparison.Ordinal));
            if (existing is not null) existing.CopiedAt = DateTimeOffset.Now;
        }
        Changed?.Invoke();

        if (autoPaste && targetWindow != IntPtr.Zero)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    Win32.SetForegroundWindow(targetWindow);
                    await Task.Delay(90).ConfigureAwait(false);
                    SendCtrlV();
                }
                catch { }
            });
        }
        return true;
    }

    /// <summary>获取当前前台窗口句柄（在弹出剪贴板窗口前调用，用于回贴归还焦点）。</summary>
    public static IntPtr CaptureForegroundWindow() => GetForegroundWindow();

    private void TrimOverflowLocked()
    {
        while (_entries.Count > MaxEntries)
        {
            var oldest = _entries
                .Where(e => !e.IsPinned)
                .OrderBy(e => e.CopiedAt)
                .FirstOrDefault();
            if (oldest is null) break;
            _entries.Remove(oldest);
        }
    }

    private void MessageLoop()
    {
        _fmtExcludeMonitor = RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
        _fmtCanIncludeHistory = RegisterClipboardFormatW("CanIncludeInClipboardHistory");

        var cls = new WindowClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WindowClassEx>(),
            lpfnWndProc = _wndProc,
            lpszClassName = "Galah2ClipboardSink",
            hInstance = GetModuleHandle(null),
        };
        RegisterClassEx(ref cls);

        _window = CreateWindowEx(0, "Galah2ClipboardSink", "", 0, 0, 0, 0, 0,
            (IntPtr)HwndMessage, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
        if (_window != IntPtr.Zero)
            AddClipboardFormatListener(_window);
        _ready.Set();
        if (_window == IntPtr.Zero) return;

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        RemoveClipboardFormatListener(_window);
        DestroyWindow(_window);
        _window = IntPtr.Zero;
    }

    private IntPtr WndProcCallback(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmClipboardUpdate)
        {
            TryCaptureCurrentClipboard();
            return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void TryCaptureCurrentClipboard()
    {
        // 短重试 3 次，避开源程序刚复制完尚未释放剪贴板锁的瞬间
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (OpenClipboard(_window))
            {
                try
                {
                    if (ShouldSkipByPrivacyFormat()) return;
                    if (!IsClipboardFormatAvailable(CfUnicodeText)) return;

                    var hData = GetClipboardData(CfUnicodeText);
                    if (hData == IntPtr.Zero) return;
                    var ptr = GlobalLock(hData);
                    if (ptr == IntPtr.Zero) return;
                    string? text;
                    try { text = Marshal.PtrToStringUni(ptr); }
                    finally { GlobalUnlock(hData); }

                    if (string.IsNullOrWhiteSpace(text)) return;
                    if (IsSuppressed(text)) return;

                    RecordText(text);
                    return;
                }
                finally
                {
                    CloseClipboard();
                }
            }
            Thread.Sleep(25);
        }
    }

    /// <summary>遵循 Windows 隐私剪贴板协议：密码管理器标记为私密的内容绝不记录。</summary>
    private bool ShouldSkipByPrivacyFormat()
    {
        if (_fmtExcludeMonitor != 0 && IsClipboardFormatAvailable(_fmtExcludeMonitor))
            return true;

        if (_fmtCanIncludeHistory != 0 && IsClipboardFormatAvailable(_fmtCanIncludeHistory))
        {
            var h = GetClipboardData(_fmtCanIncludeHistory);
            if (h != IntPtr.Zero)
            {
                var p = GlobalLock(h);
                if (p != IntPtr.Zero)
                {
                    try
                    {
                        if (Marshal.ReadInt32(p) == 0) return true;
                    }
                    finally { GlobalUnlock(h); }
                }
            }
        }
        return false;
    }

    private static bool IsSuppressed(string text)
    {
        lock (SuppressGate)
        {
            if (_suppressedText is null) return false;
            if (DateTimeOffset.UtcNow > _suppressedUntil)
            {
                _suppressedText = null;
                return false;
            }
            if (string.Equals(_suppressedText, text, StringComparison.Ordinal))
            {
                _suppressedText = null;
                return true;
            }
            return false;
        }
    }

    private static bool TrySetClipboardText(string text)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    EmptyClipboard();
                    var bytes = (text.Length + 1) * 2;
                    var hGlobal = GlobalAlloc(0x0002 /*GMEM_MOVEABLE*/, (UIntPtr)bytes);
                    if (hGlobal == IntPtr.Zero) return false;
                    var target = GlobalLock(hGlobal);
                    if (target == IntPtr.Zero) { GlobalFree(hGlobal); return false; }
                    try
                    {
                        Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                        Marshal.WriteInt16(target, text.Length * 2, 0);
                    }
                    finally { GlobalUnlock(hGlobal); }

                    if (SetClipboardData(CfUnicodeText, hGlobal) == IntPtr.Zero)
                    {
                        GlobalFree(hGlobal);
                        return false;
                    }
                    return true;
                }
                finally { CloseClipboard(); }
            }
            Thread.Sleep(25);
        }
        return false;
    }

    /// <summary>先抬起可能残留按住的 Alt/Shift/Win 修饰键，再发送干净的 Ctrl+V。</summary>
    private static void SendCtrlV()
    {
        const ushort VkMenu = 0x12;     // Alt
        const ushort VkControl = 0x11;  // Ctrl
        const ushort VkV = 0x56;
        const uint KeyEventFKeyUp = 0x0002;

        var inputs = new[]
        {
            MakeKeyInput(VkMenu, KeyEventFKeyUp),
            MakeKeyInput(VkControl, 0),
            MakeKeyInput(VkV, 0),
            MakeKeyInput(VkV, KeyEventFKeyUp),
            MakeKeyInput(VkControl, KeyEventFKeyUp),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT MakeKeyInput(ushort vk, uint flags) => new()
    {
        type = 1, // INPUT_KEYBOARD
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } },
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_window != IntPtr.Zero) PostMessage(_window, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
        _ready.Dispose();
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WindowClassEx lpwcx);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetMessage(out Msg m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Msg m);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormatW(string lpszFormat);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
