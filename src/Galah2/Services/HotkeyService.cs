using System.Runtime.InteropServices;

namespace Galah2.Services;

/// <summary>
/// Global hotkey via a message-only window.
///
/// 1.0's version owned a full WPF <c>HwndSource</c>; here the sink is a bare Win32 window whose
/// only job is to receive <c>WM_HOTKEY</c>. Registering against a message-only window keeps the
/// hotkey alive even when the panel has never been created — the same reachability rule as 1.0
/// (a hidden app must still be summonable).
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HwndMessage = -3;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ManualResetEventSlim _stop = new(false);
    private IntPtr _window;
    private readonly HashSet<int> _registeredIds = new();
    private int _disposed;

    public const int MainPanelHotkeyId = 1;
    public const int ClipboardHotkeyId = 2;

    public HotkeyService()
    {
        // The delegate must be rooted for the lifetime of the class: the OS holds the raw
        // function pointer, and a collected delegate would be a dangling callback.
        _wndProc = WndProcCallback;
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "galah2-hotkey" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    /// <summary>Raised on the hotkey thread when the main panel hotkey (id=1) fires.</summary>
    public event Action? Triggered;

    /// <summary>Raised on the hotkey thread with the registered hotkey id.</summary>
    public event Action<int>? TriggeredId;

    /// <summary>Register (or replace) the main global hotkey (id=1). Returns false when the OS refuses it.</summary>
    public bool Register(HotkeySpec spec) => Register(MainPanelHotkeyId, spec);

    /// <summary>Register (or replace) a global hotkey slot by id. Returns false when the OS refuses it.</summary>
    public bool Register(int id, HotkeySpec spec)
    {
        if (Volatile.Read(ref _disposed) != 0 || _window == IntPtr.Zero) return false;
        var ok = false;
        using var done = new ManualResetEventSlim(false);
        RunOnLoop(() =>
        {
            if (_registeredIds.Remove(id)) UnregisterHotKey(_window, id);
            const uint ModNoRepeat = 0x4000;
            ok = RegisterHotKey(_window, id, spec.Modifiers | ModNoRepeat, spec.VirtualKey);
            if (ok) _registeredIds.Add(id);
            try { done.Set(); } catch (ObjectDisposedException) { }
        });

        done.Wait(TimeSpan.FromSeconds(2));
        return ok;
    }

    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _work = new();
    private const int WmRunWork = 0x0400 + 1;   // WM_USER + 1: private to this window class

    private void RunOnLoop(Action action)
    {
        if (Thread.CurrentThread.ManagedThreadId == _thread.ManagedThreadId) { action(); return; }
        _work.Enqueue(action);
        PostMessage(_window, WmRunWork, IntPtr.Zero, IntPtr.Zero);
    }

    private void MessageLoop()
    {
        var cls = new WindowClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WindowClassEx>(),
            lpfnWndProc = _wndProc,
            lpszClassName = "Galah2HotkeySink",
            hInstance = GetModuleHandle(null)
        };
        RegisterClassEx(ref cls);

        _window = CreateWindowEx(0, "Galah2HotkeySink", "", 0, 0, 0, 0, 0,
            (IntPtr)HwndMessage, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
        _ready.Set();
        if (_window == IntPtr.Zero) return;

        while (!_stop.IsSet)
        {
            if (GetMessage(out var msg, IntPtr.Zero, 0, 0) <= 0) break;
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        // 在创建窗口的同一线程上注销所有热键并销毁窗口句柄。
        foreach (var id in _registeredIds) UnregisterHotKey(_window, id);
        _registeredIds.Clear();
        DestroyWindow(_window);
        _window = IntPtr.Zero;
    }

    private readonly WndProc _wndProc;

    private IntPtr WndProcCallback(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmRunWork)
        {
            while (_work.TryDequeue(out var work)) work();
            return IntPtr.Zero;
        }
        if (msg == WmHotkey)
        {
            var id = (int)wParam;
            if (id == MainPanelHotkeyId) Triggered?.Invoke();
            TriggeredId?.Invoke(id);
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Set();
        if (_window != IntPtr.Zero) PostMessage(_window, 0x0012 /*WM_QUIT*/, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
        _ready.Dispose();
        _stop.Dispose();
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

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WindowClassEx lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern int GetMessage(out Msg msg, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Msg msg);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
