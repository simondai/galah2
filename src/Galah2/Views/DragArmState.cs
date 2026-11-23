namespace Galah2.Views;

/// <summary>
/// Explicit press-gesture arming for the frameless panel's manual drag and edge resize.
///
/// The chrome drag and the edge resize record their origin in fields and then follow the pointer
/// in OnPointerMoved. A release does not always reach the panel: the ☰ MenuFlyout is a separate
/// HWND, so a press-and-release on one of its items happens entirely on the popup - the panel
/// never sees the release. If the origin state survives the gesture, the next button-down
/// movement (for example the pointer re-entering the panel while the flyout click is still down)
/// re-fires the "drag" from the stale origin and the panel teleports - reported as the panel
/// flying off the desktop when clicking 设置 in the menu.
///
/// Arming makes the state valid only for the press that armed it:
///   ArmDrag/ArmResize - a press started on this surface (empty chrome / edge zone)
///   Release           - the button came up anywhere the panel could observe
///   Deactivated       - another window (a flyout, a dialog) took activation, so any in-flight
///                       press is over even if the release event never arrives here
///   ButtonUpMoved     - a movement without the button down means the press ended somewhere
/// </summary>
public sealed class DragArmState
{
    private bool _dragArmed;
    private bool _resizeArmed;

    public bool DragArmed => _dragArmed;

    public bool ResizeArmed => _resizeArmed;

    public void ArmDrag() => _dragArmed = true;

    public void ArmResize() => _resizeArmed = true;

    public void Release()
    {
        _dragArmed = false;
        _resizeArmed = false;
    }

    public void Deactivated() => Release();

    public void ButtonUpMoved() => Release();
}
