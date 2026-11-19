using System.Runtime.InteropServices;

namespace Galah2.Services;

/// <summary>
/// Single-instance handoff, same contract as 1.0: a second process tells the primary to toggle
/// the panel and exits, so double-launching never produces a second tray icon.
///
/// Implementation is a named event rather than 1.0's WM_COPYDATA pipe: the receiving side is a
/// dedicated wait thread, which keeps the signal off the UI thread and survives a busy dispatcher.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _signal;
    private readonly CancellationTokenSource _cts = new();
    private bool _primary;

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, @"Local\Galah2.SingleInstance", out _primary);
        _signal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Galah2.ToggleSignal");

        if (_primary)
        {
            var thread = new Thread(WaitLoop) { IsBackground = true, Name = "galah2-ipc" };
            thread.Start();
        }
    }

    public bool IsPrimary => _primary;

    /// <summary>Ask the primary instance to toggle the panel. Returns false when none is running.</summary>
    public static bool SignalPrimary()
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(@"Local\Galah2.ToggleSignal");
            handle.Set();
            return true;
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public event Action? ToggleRequested;

    private void WaitLoop()
    {
        var handles = new WaitHandle[] { _signal, _cts.Token.WaitHandle };
        while (!_cts.IsCancellationRequested)
        {
            var index = WaitHandle.WaitAny(handles);
            if (index != 0) break;
            ToggleRequested?.Invoke();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _signal.Dispose();
        if (_primary)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _mutex.Dispose();
    }
}
