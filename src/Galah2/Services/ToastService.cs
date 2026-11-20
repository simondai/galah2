using Avalonia.Threading;
using Galah2.ViewModels;

namespace Galah2.Services;

/// <summary>
/// A transient message strip inside the panel (last call wins, auto-dismiss).
///
/// 1.x used this for "could not launch X" and for the first-run hint. The distinction from the
/// save-failure banner matters: a banner stays until the problem is fixed, a toast is news.
/// </summary>
public sealed class ToastService : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _timer;
    private string _message = "";

    public ToastService()
    {
        // Constructed on the UI thread by the runtime; the timer runs on it too.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3000) };
        _timer.Tick += (_, _) => Close();
    }

    public bool IsOpen
    {
        get => _isOpen;
        private set => Set(ref _isOpen, value);
    }

    private bool _isOpen;

    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    /// <summary>Show a message for a few seconds. A later call replaces the current one.</summary>
    public void Show(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        Message = message;
        IsOpen = true;

        // Restart the clock so a second message gets its own full dwell time.
        _timer.Stop();
        _timer.Start();
    }

    public void Close()
    {
        _timer.Stop();
        IsOpen = false;
    }

    public void Dispose() => _timer.Stop();
}
