namespace Galah2.Services;

/// <summary>
/// Counts open modal dialogs so "hide on blur" and "hide on launch" never swallow a dialog the user
/// is working in (spec §4.2: the edit window lives independently of the panel).
/// </summary>
public sealed class DialogGuard
{
    /// <summary>Number of dialogs currently open.</summary>
    public int OpenCount { get; private set; }

    /// <summary>True while at least one dialog is open.</summary>
    public bool IsBlocking => OpenCount > 0;

    /// <summary>Raised when the count reaches zero.</summary>
    public event Action? AllClosed;

    /// <summary>Register a dialog; dispose the result when it closes.</summary>
    public IDisposable Open()
    {
        OpenCount++;
        return new Lease(this);
    }

    private void Release()
    {
        if (OpenCount > 0) OpenCount--;
        if (OpenCount == 0) AllClosed?.Invoke();
    }

    private sealed class Lease(DialogGuard owner) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            owner.Release();
        }
    }
}
